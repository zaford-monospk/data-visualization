using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        static readonly char[] Separators = { ' ' };
        const int SampleRowCount = 5;
        const int MinResolution = 8;
        const int MaxResolution = 128;

        public string FieldName { get; set; } = "Temperature(C)";

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
            using var reader = new StreamReader(FilePath);

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

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                line = line.Trim();
                if (line.Length == 0)
                    continue;

                string[] tokens = line.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

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
                        float[] scalarValues = ReadScalars(reader, _cellCount, cancellationToken);
                        data.CellScalars[scalarField] = scalarValues;
                        Debug.Log($"[VtkUnstructuredGridReader] SCALARS {scalarField} ({scalarValues.Length} cells)");
                        LogSample(scalarField, scalarValues);
                        break;

                    case "VECTORS":
                        string vectorField = tokens[1];
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

        List<Vector3> ReadPoints(StreamReader reader, int count, CancellationToken cancellationToken)
        {
            var result = new List<Vector3>(count);
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string rawLine = reader.ReadLine() ?? throw new EndOfStreamException("Unexpected end of file while reading POINTS.");
                string[] tokens = rawLine.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
                result.Add(new Vector3(
                    float.Parse(tokens[0], CultureInfo.InvariantCulture),
                    float.Parse(tokens[1], CultureInfo.InvariantCulture),
                    float.Parse(tokens[2], CultureInfo.InvariantCulture)));
            }

            Debug.Log($"[VtkUnstructuredGridReader] POINTS {count}");
            for (int i = 0; i < Mathf.Min(SampleRowCount, result.Count); i++)
                Debug.Log($"  [{i}] {result[i].x:F4} {result[i].y:F4} {result[i].z:F4}");

            return result;
        }

        static int[][] ReadCellPointIndices(StreamReader reader, int count, CancellationToken cancellationToken)
        {
            var result = new int[count][];
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string rawLine = reader.ReadLine() ?? throw new EndOfStreamException("Unexpected end of file while reading CELLS.");
                string[] tokens = rawLine.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
                int vertexCount = int.Parse(tokens[0], CultureInfo.InvariantCulture);

                var indices = new int[vertexCount];
                for (int v = 0; v < vertexCount; v++)
                    indices[v] = int.Parse(tokens[v + 1], CultureInfo.InvariantCulture);
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
                types[i] = int.Parse(rawLine.Trim(), CultureInfo.InvariantCulture);
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
                values[i] = float.Parse(rawLine.Trim(), CultureInfo.InvariantCulture);
            }
            return values;
        }

        static Vector3[] ReadVectors(StreamReader reader, int count, CancellationToken cancellationToken)
        {
            var values = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string rawLine = reader.ReadLine() ?? throw new EndOfStreamException("Unexpected end of file while reading vector data.");
                string[] tokens = rawLine.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
                values[i] = new Vector3(
                    float.Parse(tokens[0], CultureInfo.InvariantCulture),
                    float.Parse(tokens[1], CultureInfo.InvariantCulture),
                    float.Parse(tokens[2], CultureInfo.InvariantCulture));
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