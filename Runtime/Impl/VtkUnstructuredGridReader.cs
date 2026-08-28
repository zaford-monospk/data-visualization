using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Monospark
{
    // Reads a legacy ASCII VTK UNSTRUCTURED_GRID file (POINTS / CELLS / CELL_TYPES / CELL_DATA)
    // such as Assets/Resources/Data/room.vtk. Exposes the raw parse as a
    // VtkUnstructuredGridData buffer, or one of its per-cell scalar fields
    // (e.g. "Temperature(C)") converted into a Texture3D. Constructed and driven
    // by DataConvertManager.GetMap<T>, which calls Init(filepath) then BuildData(callback).
    public class VtkUnstructuredGridReader : DataConverter
    {
        const int SampleRowCount = 5;
        const int MinResolution = 8;
        const int MaxResolution = 128;
        const int StreamBufferSize = 1 << 20; // 1 MB — fewer underlying reads over a ~4M-line file

        public string FieldName { get; set; } = "Temperature(C)";

        // When true (default), every CELL_DATA section (each SCALARS/VECTORS
        // field) is parsed and cached in _data, so a later
        // BuildData(OnProcessBufferData) call has everything. Set to false
        // when this instance will only ever be asked for FieldName's
        // Texture3D (e.g. VtkFrameReader's internal use) -- every OTHER
        // SCALARS/VECTORS section is then skipped (ReadLine()'d past without
        // tokenizing/parsing/allocating), which for a file with several
        // per-cell fields (room.vtk: Temperature + Pressure + Velocity) is a
        // large chunk of the total parse that a Texture3D-only caller never
        // uses anyway.
        public bool ParseAllFields { get; set; } = true;

        // Which axis the file's raw X/Y/Z treats as "up" -- see WorldUpAxis.
        // Applied to every POINTS and VECTORS value as it's parsed, so
        // downstream (bounds, cell centroids, voxelization) only ever sees
        // Unity-space coordinates.
        public WorldUpAxis WorldUp { get; set; } = WorldUpAxis.Y;

        // Physical bounds (world units) of the parsed POINTS -- valid once
        // BuildData's callback has fired with SUCCESS. Lets callers (e.g.
        // VtkFrameReader) size a display proxy to the data's actual
        // real-world extents instead of guessing from voxel-grid resolution.
        public Vector3 Size => _bounds.size;

        // Raw (un-normalized) min/max of FieldName's values across all cells
        // -- valid once BuildData(OnProcessTex3DData)'s callback has fired
        // with SUCCESS. Lets a caller (e.g. VtkFrameRenderer) convert a
        // real-world value (e.g. a Celsius temperature) into the Texture3D's
        // normalized 0..1 space the same way ToTexture3D itself does,
        // instead of duplicating/guessing that range.
        public float ValueMin { get; private set; }
        public float ValueMax { get; private set; }

        int _cellCount;
        Bounds _bounds;
        Vector3[] _cellCentroids;
        VtkUnstructuredGridData _data;

        // Runs the blocking file read on a background thread so callers (e.g. a
        // MonoBehaviour's Start) never stall a frame, then hands the resulting
        // Texture3D back through the callback per DataConverter's async contract.
        public override async void BuildData(OnProcessTex3DData callback)
        {
            try
            {
                callback?.Invoke(new Progress { Status = eStatus.ONPROGRESS, ProgressValue = 0f }, null);

                await EnsureParsedAsync();

                callback?.Invoke(new Progress { Status = eStatus.ONPROGRESS, ProgressValue = 0.9f }, null);

                (int resX, int resY, int resZ) = ComputeResolution(_cellCount, _bounds.size);
                Texture3D texture = ToTexture3D(FieldName, resX, resY, resZ);

                callback?.Invoke(new Progress { Status = eStatus.SUCCESS, ProgressValue = 1f }, texture);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                callback?.Invoke(new Progress { Status = eStatus.ERROR, ProgressValue = 0f }, null);
            }
        }

        // Same parse, but hands back the raw VtkUnstructuredGridData buffer
        // (points/cells/scalars/vectors) instead of converting to a Texture3D.
        public override async void BuildData(OnProcessBufferData callback)
        {
            try
            {
                callback?.Invoke(new Progress { Status = eStatus.ONPROGRESS, ProgressValue = 0f }, null);

                await EnsureParsedAsync();

                callback?.Invoke(new Progress { Status = eStatus.SUCCESS, ProgressValue = 1f }, _data);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                callback?.Invoke(new Progress { Status = eStatus.ERROR, ProgressValue = 0f }, null);
            }
        }

        // This reader parses a single unstructured-grid snapshot, not a voxelized
        // time sequence — that's VtkFrameSequenceReader's job (a standalone
        // loader, not a DataConverter, since it consumes a directory of two
        // files rather than DataConverter's single FilePath).
        public override void BuildData(OnProcessFrameSequenceData callback)
        {
            throw new NotSupportedException(
                $"{nameof(VtkUnstructuredGridReader)} does not produce a {nameof(VtkFrameSequenceData)}; use VtkFrameSequenceReader instead.");
        }

        // Both BuildData overloads need a parsed file; only do the actual
        // (expensive, 4M-line) parse once even if both are called on this instance.
        Task EnsureParsedAsync()
        {
            if (_data != null)
                return Task.CompletedTask;

            if (!File.Exists(FilePath))
                throw new FileNotFoundException($"VTK file not found: {FilePath}", FilePath);

            return Task.Run(() => ParseFile(CancellationToken.None));
        }

        void ParseFile(CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(FilePath, Encoding.UTF8, true, StreamBufferSize);

            string versionLine = reader.ReadLine();
            string title = reader.ReadLine();
            string formatLine = reader.ReadLine();

            if (formatLine == null || !formatLine.Trim().Equals("ASCII", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Only ASCII-format legacy VTK files are supported.");

            Debug.Log($"[VtkUnstructuredGridReader] Reading '{FilePath}'\n" +
                       $"  Version : {versionLine}\n" +
                       $"  Title   : {title}\n" +
                       $"  Format  : {formatLine}");

            var data = new VtkUnstructuredGridData { Title = title };
            List<Vector3> pointList = null;
            int[][] cellPointIndices = null;
            int[] cellTypes = null;

            // Section header lines (a few dozen total, not the ~4M data rows)
            // are rare enough that plain Split here costs nothing measurable —
            // only the per-row parsers below are worth the span-based rewrite.
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                line = line.Trim();
                if (line.Length == 0)
                    continue;

                string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                switch (tokens[0])
                {
                    case "DATASET":
                        if (tokens[1] != "UNSTRUCTURED_GRID")
                            throw new NotSupportedException(
                                $"Dataset type '{tokens[1]}' is not supported, only UNSTRUCTURED_GRID.");
                        break;

                    case "POINTS":
                        int pointCount = int.Parse(tokens[1], CultureInfo.InvariantCulture);
                        pointList = ReadPoints(reader, pointCount, cancellationToken);
                        break;

                    case "CELLS":
                        _cellCount = int.Parse(tokens[1], CultureInfo.InvariantCulture);
                        cellPointIndices = ReadCellPointIndices(reader, _cellCount, cancellationToken);
                        break;

                    case "CELL_TYPES":
                        cellTypes = ReadCellTypes(reader, _cellCount, cancellationToken);
                        break;

                    case "SCALARS":
                        string scalarField = tokens[1];
                        reader.ReadLine(); // LOOKUP_TABLE <name>
                        if (!ParseAllFields && scalarField != FieldName)
                        {
                            SkipLines(reader, _cellCount, cancellationToken);
                            break;
                        }
                        float[] scalarValues = ReadScalars(reader, _cellCount, cancellationToken);
                        data.CellScalars[scalarField] = scalarValues;
                        Debug.Log($"[VtkUnstructuredGridReader] SCALARS {scalarField} ({scalarValues.Length} cells)");
                        LogSample(scalarField, scalarValues);
                        break;

                    case "VECTORS":
                        string vectorField = tokens[1];
                        if (!ParseAllFields)
                        {
                            SkipLines(reader, _cellCount, cancellationToken);
                            break;
                        }
                        Vector3[] vectorValues = ReadVectors(reader, _cellCount, cancellationToken);
                        data.CellVectors[vectorField] = vectorValues;
                        Debug.Log($"[VtkUnstructuredGridReader] VECTORS {vectorField} ({vectorValues.Length} cells)");
                        break;
                }
            }

            data.Points = pointList?.ToArray() ?? Array.Empty<Vector3>();
            data.Cells = BuildCells(cellPointIndices, cellTypes);

            _bounds = ComputeBounds(pointList);
            _cellCentroids = ComputeCellCentroids(data.Points, data.Cells);
            _data = data;
        }

        // Splits a line on ASCII spaces without allocating a string[] or any
        // per-token strings (unlike string.Split) — yields slices of the
        // original line instead. Across a ~4M-line file feeding ~9M Parse
        // calls, this per-token allocation was the main parsing cost.
        ref struct LineTokens
        {
            ReadOnlySpan<char> _remaining;

            public LineTokens(ReadOnlySpan<char> line) => _remaining = line;

            public bool TryNext(out ReadOnlySpan<char> token)
            {
                int start = 0;
                while (start < _remaining.Length && _remaining[start] == ' ')
                    start++;

                if (start >= _remaining.Length)
                {
                    token = default;
                    return false;
                }

                int end = start;
                while (end < _remaining.Length && _remaining[end] != ' ')
                    end++;

                token = _remaining.Slice(start, end - start);
                _remaining = _remaining.Slice(end);
                return true;
            }
        }

        static ReadOnlySpan<char> NextToken(ref LineTokens tokens)
        {
            if (!tokens.TryNext(out ReadOnlySpan<char> token))
                throw new FormatException("Expected another token on this line.");
            return token;
        }

        static float ParseFloat(ReadOnlySpan<char> token) =>
            float.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);

        static int ParseInt(ReadOnlySpan<char> token) =>
            int.Parse(token, NumberStyles.Integer, CultureInfo.InvariantCulture);

        List<Vector3> ReadPoints(StreamReader reader, int count, CancellationToken cancellationToken)
        {
            var result = new List<Vector3>(count);
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string rawLine = reader.ReadLine() ?? throw new EndOfStreamException("Unexpected end of file while reading POINTS.");
                var tokens = new LineTokens(rawLine.AsSpan());
                float x = ParseFloat(NextToken(ref tokens));
                float y = ParseFloat(NextToken(ref tokens));
                float z = ParseFloat(NextToken(ref tokens));
                result.Add(WorldUp.ToUnity(new Vector3(x, y, z)));
            }

            Debug.Log($"[VtkUnstructuredGridReader] POINTS {count}");
            for (int i = 0; i < Mathf.Min(SampleRowCount, result.Count); i++)
                Debug.Log($"  [{i}] {result[i].x:F4} {result[i].y:F4} {result[i].z:F4}");

            return result;
        }

        // Advances past `count` lines without tokenizing/parsing/allocating --
        // used by ParseAllFields = false to discard a SCALARS/VECTORS section
        // this instance was never going to read, far cheaper per line than
        // ReadScalars/ReadVectors since there's no float.Parse or array write.
        static void SkipLines(StreamReader reader, int count, CancellationToken cancellationToken)
        {
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                reader.ReadLine();
            }
        }

        static int[][] ReadCellPointIndices(StreamReader reader, int count, CancellationToken cancellationToken)
        {
            var result = new int[count][];
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string rawLine = reader.ReadLine() ?? throw new EndOfStreamException("Unexpected end of file while reading CELLS.");
                var tokens = new LineTokens(rawLine.AsSpan());
                int vertexCount = ParseInt(NextToken(ref tokens));

                var indices = new int[vertexCount];
                for (int v = 0; v < vertexCount; v++)
                    indices[v] = ParseInt(NextToken(ref tokens));
                result[i] = indices;
            }

            Debug.Log($"[VtkUnstructuredGridReader] CELLS {count}");
            return result;
        }

        static int[] ReadCellTypes(StreamReader reader, int count, CancellationToken cancellationToken)
        {
            var types = new int[count];
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string rawLine = reader.ReadLine() ?? throw new EndOfStreamException("Unexpected end of file while reading CELL_TYPES.");
                types[i] = ParseInt(rawLine.AsSpan().Trim());
            }
            return types;
        }

        static float[] ReadScalars(StreamReader reader, int count, CancellationToken cancellationToken)
        {
            var values = new float[count];
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string rawLine = reader.ReadLine() ?? throw new EndOfStreamException("Unexpected end of file while reading scalar data.");
                values[i] = ParseFloat(rawLine.AsSpan().Trim());
            }
            return values;
        }

        // Instance (not static, unlike the other Read*/Skip helpers here): needs
        // WorldUp so a vector field like Velocity gets the same axis
        // conversion as POINTS, or its direction would point wrong once
        // OrientToVelocity (InstancedPointRender.shader) rotates a glyph to it.
        Vector3[] ReadVectors(StreamReader reader, int count, CancellationToken cancellationToken)
        {
            var values = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string rawLine = reader.ReadLine() ?? throw new EndOfStreamException("Unexpected end of file while reading vector data.");
                var tokens = new LineTokens(rawLine.AsSpan());
                float x = ParseFloat(NextToken(ref tokens));
                float y = ParseFloat(NextToken(ref tokens));
                float z = ParseFloat(NextToken(ref tokens));
                values[i] = WorldUp.ToUnity(new Vector3(x, y, z));
            }
            return values;
        }

        static void LogSample(string scalarField, float[] values)
        {
            for (int i = 0; i < Mathf.Min(SampleRowCount, values.Length); i++)
                Debug.Log($"  [{scalarField}][{i}] {values[i]:F4}");
        }

        static VtkCell[] BuildCells(int[][] pointIndices, int[] types)
        {
            if (pointIndices == null)
                return Array.Empty<VtkCell>();

            var cells = new VtkCell[pointIndices.Length];
            for (int i = 0; i < cells.Length; i++)
            {
                cells[i] = new VtkCell
                {
                    Type = types != null && i < types.Length ? types[i] : 0,
                    PointIndices = pointIndices[i]
                };
            }
            return cells;
        }

        static Vector3[] ComputeCellCentroids(Vector3[] points, VtkCell[] cells)
        {
            var centroids = new Vector3[cells.Length];
            for (int i = 0; i < cells.Length; i++)
            {
                int[] indices = cells[i].PointIndices;
                Vector3 sum = Vector3.zero;
                for (int v = 0; v < indices.Length; v++)
                    sum += points[indices[v]];
                centroids[i] = sum / indices.Length;
            }
            return centroids;
        }

        static Bounds ComputeBounds(List<Vector3> pts)
        {
            if (pts == null || pts.Count == 0)
                return default;

            var result = new Bounds(pts[0], Vector3.zero);
            for (int i = 1; i < pts.Count; i++)
                result.Encapsulate(pts[i]);
            return result;
        }

        // Derives a near-cubic-voxel resolution from the data itself rather than a
        // fixed constant: voxel edge length = cube root of (bounds volume / sample
        // count), so denser/larger datasets naturally get a finer grid, clamped to
        // a sane texture size range.
        static (int x, int y, int z) ComputeResolution(int sampleCount, Vector3 size)
        {
            float volume = Mathf.Max(size.x * size.y * size.z, Mathf.Epsilon);
            float voxelEdge = Mathf.Pow(volume / Mathf.Max(sampleCount, 1), 1f / 3f);

            int resX = Mathf.Clamp(Mathf.RoundToInt(size.x / voxelEdge), MinResolution, MaxResolution);
            int resY = Mathf.Clamp(Mathf.RoundToInt(size.y / voxelEdge), MinResolution, MaxResolution);
            int resZ = Mathf.Clamp(Mathf.RoundToInt(size.z / voxelEdge), MinResolution, MaxResolution);
            return (resX, resY, resZ);
        }

        // Buckets each cell's centroid into the nearest voxel of a resX x resY x
        // resZ grid spanning bounds, averaging scalar values that land in the same
        // voxel. Empty voxels are left fully transparent. protected (not private) +
        // virtual so a subclass can override the resampling strategy (e.g.
        // trilinear) while everything else stays encapsulated.
        protected virtual Texture3D ToTexture3D(string scalarField, int resX, int resY, int resZ)
        {
            if (!_data.CellScalars.TryGetValue(scalarField, out float[] scalars))
                throw new KeyNotFoundException($"Scalar field '{scalarField}' was not found in {FilePath}.");

            Vector3 min = _bounds.min;
            Vector3 size = _bounds.size;

            int voxelCountTotal = resX * resY * resZ;
            var voxelSum = new float[voxelCountTotal];
            var voxelHits = new int[voxelCountTotal];

            float minValue = float.MaxValue;
            float maxValue = float.MinValue;

            for (int i = 0; i < _cellCentroids.Length; i++)
            {
                Vector3 c = _cellCentroids[i];
                int vx = Mathf.Clamp((int)((c.x - min.x) / size.x * resX), 0, resX - 1);
                int vy = Mathf.Clamp((int)((c.y - min.y) / size.y * resY), 0, resY - 1);
                int vz = Mathf.Clamp((int)((c.z - min.z) / size.z * resZ), 0, resZ - 1);
                int index = vx + vy * resX + vz * resX * resY;

                float value = scalars[i];
                voxelSum[index] += value;
                voxelHits[index]++;

                minValue = Mathf.Min(minValue, value);
                maxValue = Mathf.Max(maxValue, value);
            }

            float range = Mathf.Max(maxValue - minValue, Mathf.Epsilon);
            var colors = new Color[voxelCountTotal];
            for (int i = 0; i < voxelCountTotal; i++)
            {
                if (voxelHits[i] == 0)
                {
                    colors[i] = Color.clear;
                    continue;
                }
                float normalized = (voxelSum[i] / voxelHits[i] - minValue) / range;
                colors[i] = new Color(normalized, normalized, normalized, 1f);
            }

            ValueMin = minValue;
            ValueMax = maxValue;

            // RGBAHalf, not RFloat: RFloat is single-channel and would silently
            // drop the alpha (occupancy) channel written above.
            var texture = new Texture3D(resX, resY, resZ, TextureFormat.RGBAHalf, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            texture.SetPixels(colors);
            texture.Apply();

            return texture;
        }
    }
}