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
    // Reads a SINGLE static snapshot file -- FilePath is one file, exactly
    // like VtkUnstructuredGridReader, not a directory (that's only
    // VtkFrameSequenceReader, which needs a directory because its format
    // splits frames.raw from frames_meta.json). Format is picked from
    // FilePath's extension:
    //   .vtk - a legacy ASCII VTK UNSTRUCTURED_GRID file (e.g. room.vtk).
    //          Parsing is delegated to VtkUnstructuredGridReader itself --
    //          same format it already reads, no reason to duplicate that
    //          ~250-line parser here.
    //   .csv - a point-cloud export (e.g. Test_Room_16000.csv): one row per
    //          sample, "X (m)"/"Y (m)"/"Z (m)" position columns plus a
    //          scalar value column (CsvValueColumn, default "Temperature"),
    //          no cell connectivity.
    // Either way the result is voxelized into a static Texture3D with the
    // same RGBAHalf convention every volume reader in this package uses:
    // .r = normalized scalar value, .a = occupancy (0 where no sample landed).
    public class VtkFrameReader : DataConverter
    {
        const int MinResolution = 8;
        const int MaxResolution = 128;
        const int StreamBufferSize = 1 << 20; // 1 MB — fewer underlying reads over a large CSV

        // .vtk SCALARS field name, forwarded to the inner VtkUnstructuredGridReader.
        public string FieldName { get; set; } = "Temperature(C)";

        // .csv column header to voxelize, matched by prefix (so "Temperature"
        // matches the sample data's "Temperature (K)" without hardcoding the unit).
        public string CsvValueColumn { get; set; } = "Temperature";

        // Which axis the file's raw X/Y/Z treats as "up" -- see WorldUpAxis.
        // Applied to .csv points here, and forwarded to the inner
        // VtkUnstructuredGridReader for .vtk (ReadVtkAsTexture3D).
        public WorldUpAxis WorldUp { get; set; } = WorldUpAxis.Y;

        // Physical bounds size (world units) of the source file's points --
        // valid once BuildData's callback has fired with SUCCESS. Lets a
        // caller (e.g. VtkFrameRenderer, via CFDFactory) size a display proxy
        // to the data's actual real-world extents instead of guessing from
        // voxel-grid resolution.
        public Vector3 DataSize { get; private set; }

        // Runs the blocking file read on a background thread so callers (e.g. a
        // MonoBehaviour's Start) never stall a frame, then builds the Texture3D
        // back on the calling (main) thread once the await resumes -- Unity's
        // Texture3D/SetPixels/Apply can't run off it -- and hands the result
        // back through the callback per DataConverter's async contract.
        public override async void BuildData(OnProcessTex3DData callback)
        {
            try
            {
                callback?.Invoke(new Progress { Status = eStatus.ONPROGRESS, ProgressValue = 0f }, null);

                if (!File.Exists(FilePath))
                    throw new FileNotFoundException($"File not found: {FilePath}", FilePath);

                string extension = Path.GetExtension(FilePath);
                Texture3D texture;

                if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    (Vector3[] points, float[] values) = await Task.Run(() => ParseCsv(CancellationToken.None));

                    if (points.Length == 0)
                        throw new InvalidDataException($"No points found in {FilePath}.");

                    Bounds bounds = ComputeBounds(points);
                    DataSize = bounds.size;

                    callback?.Invoke(new Progress { Status = eStatus.ONPROGRESS, ProgressValue = 0.9f }, null);

                    texture = ToTexture3D(points, values, bounds);
                }
                else if (extension.Equals(".vtk", StringComparison.OrdinalIgnoreCase))
                {
                    texture = await ReadVtkAsTexture3D();
                }
                else
                {
                    throw new NotSupportedException(
                        $"{nameof(VtkFrameReader)} only reads .vtk or .csv files, got '{extension}' ({FilePath}).");
                }

                callback?.Invoke(new Progress { Status = eStatus.SUCCESS, ProgressValue = 1f }, texture);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                callback?.Invoke(new Progress { Status = eStatus.ERROR, ProgressValue = 0f }, null);
            }
        }

        // This reader produces a single static Texture3D, not the raw
        // per-cell buffer of an unstructured-grid snapshot -- that's
        // VtkUnstructuredGridReader's job.
        public override void BuildData(OnProcessBufferData callback)
        {
            throw new NotSupportedException(
                $"{nameof(VtkFrameReader)} does not produce a {nameof(VtkUnstructuredGridData)}; " +
                "use VtkUnstructuredGridReader instead.");
        }

        // This reader reads one static file, not an animated sequence --
        // for that, use VtkFrameSequenceReader instead.
        public override void BuildData(OnProcessFrameSequenceData callback)
        {
            throw new NotSupportedException(
                $"{nameof(VtkFrameReader)} does not produce a {nameof(VtkFrameSequenceData)}; " +
                "use VtkFrameSequenceReader instead.");
        }

        // Bridges VtkUnstructuredGridReader's callback-based BuildData onto an
        // awaitable, so the .vtk branch above can sit in the same async flow
        // as the .csv branch.
        Task<Texture3D> ReadVtkAsTexture3D()
        {
            var tcs = new TaskCompletionSource<Texture3D>();

            // ParseAllFields = false: this instance is only ever asked for
            // FieldName's Texture3D (never BuildData(OnProcessBufferData)),
            // so every OTHER SCALARS/VECTORS section in the file (e.g.
            // room.vtk's unused Pressure/Velocity, on top of Temperature) gets
            // skipped instead of fully parsed -- a large chunk of the total
            // parse time for a file with several per-cell fields.
            var inner = new VtkUnstructuredGridReader { FieldName = FieldName, ParseAllFields = false, WorldUp = WorldUp };
            inner.Init(FilePath);
            inner.BuildData((progress, texture) =>
            {
                switch (progress.Status)
                {
                    case eStatus.SUCCESS:
                        DataSize = inner.Size;
                        tcs.TrySetResult(texture);
                        break;
                    case eStatus.ERROR:
                        tcs.TrySetException(new Exception($"Failed to read '{FilePath}' as an unstructured grid."));
                        break;
                }
            });

            return tcs.Task;
        }

        (Vector3[] points, float[] values) ParseCsv(CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(FilePath, Encoding.UTF8, true, StreamBufferSize);

            string headerLine = reader.ReadLine() ?? throw new InvalidDataException($"{FilePath} is empty.");
            string[] headers = SplitCsvLine(headerLine);

            int valueIndex = FindColumn(headers, CsvValueColumn);
            int xIndex = FindColumn(headers, "X");
            int yIndex = FindColumn(headers, "Y");
            int zIndex = FindColumn(headers, "Z");

            if (valueIndex < 0)
                throw new KeyNotFoundException($"Column '{CsvValueColumn}' was not found in {FilePath}.");
            if (xIndex < 0 || yIndex < 0 || zIndex < 0)
                throw new KeyNotFoundException($"X/Y/Z columns were not found in {FilePath}.");

            int maxIndex = Mathf.Max(Mathf.Max(valueIndex, xIndex), Mathf.Max(yIndex, zIndex));

            var pointList = new List<Vector3>();
            var valueList = new List<float>();

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (line.Length == 0)
                    continue;

                string[] tokens = SplitCsvLine(line);
                if (tokens.Length <= maxIndex)
                    continue; // malformed/short row -- skip rather than throw over one bad line

                Vector3 point = new Vector3(
                    ParseFloat(tokens[xIndex]), ParseFloat(tokens[yIndex]), ParseFloat(tokens[zIndex]));
                pointList.Add(WorldUp.ToUnity(point));
                valueList.Add(ParseFloat(tokens[valueIndex]));
            }

            Debug.Log($"[VtkFrameReader] Read {pointList.Count} points from '{FilePath}' (value column '{headers[valueIndex].Trim()}')");

            return (pointList.ToArray(), valueList.ToArray());
        }

        static int FindColumn(string[] headers, string name)
        {
            for (int i = 0; i < headers.Length; i++)
            {
                if (headers[i].Trim().StartsWith(name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        // Minimal CSV split: no quoted-comma support (none of this export's
        // fields need it), just strips the surrounding quotes each field here
        // is wrapped in.
        static string[] SplitCsvLine(string line)
        {
            string[] tokens = line.Split(',');
            for (int i = 0; i < tokens.Length; i++)
                tokens[i] = tokens[i].Trim('"', ' ');
            return tokens;
        }

        static float ParseFloat(string token) =>
            float.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);

        static Bounds ComputeBounds(Vector3[] points)
        {
            var bounds = new Bounds(points[0], Vector3.zero);
            for (int i = 1; i < points.Length; i++)
                bounds.Encapsulate(points[i]);
            return bounds;
        }

        // Same bucket-and-average voxelization as VtkUnstructuredGridReader.ToTexture3D,
        // just against raw CSV points instead of cell centroids -- a point-cloud
        // export has no cell connectivity, so each row already IS a sample.
        static Texture3D ToTexture3D(Vector3[] points, float[] values, Bounds bounds)
        {
            (int resX, int resY, int resZ) = ComputeResolution(points.Length, bounds.size);

            Vector3 min = bounds.min;
            Vector3 size = bounds.size;

            int voxelCountTotal = resX * resY * resZ;
            var voxelSum = new float[voxelCountTotal];
            var voxelHits = new int[voxelCountTotal];

            float minValue = float.MaxValue;
            float maxValue = float.MinValue;

            for (int i = 0; i < points.Length; i++)
            {
                Vector3 p = points[i];
                int vx = Mathf.Clamp((int)((p.x - min.x) / size.x * resX), 0, resX - 1);
                int vy = Mathf.Clamp((int)((p.y - min.y) / size.y * resY), 0, resY - 1);
                int vz = Mathf.Clamp((int)((p.z - min.z) / size.z * resZ), 0, resZ - 1);
                int index = vx + vy * resX + vz * resX * resY;

                float value = values[i];
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

        // Derives a near-cubic-voxel resolution from the data itself, same as
        // VtkUnstructuredGridReader.ComputeResolution: voxel edge length = cube
        // root of (bounds volume / sample count), clamped to a sane texture size range.
        static (int x, int y, int z) ComputeResolution(int sampleCount, Vector3 size)
        {
            float volume = Mathf.Max(size.x * size.y * size.z, Mathf.Epsilon);
            float voxelEdge = Mathf.Pow(volume / Mathf.Max(sampleCount, 1), 1f / 3f);

            int resX = Mathf.Clamp(Mathf.RoundToInt(size.x / voxelEdge), MinResolution, MaxResolution);
            int resY = Mathf.Clamp(Mathf.RoundToInt(size.y / voxelEdge), MinResolution, MaxResolution);
            int resZ = Mathf.Clamp(Mathf.RoundToInt(size.z / voxelEdge), MinResolution, MaxResolution);
            return (resX, resY, resZ);
        }
    }
}
