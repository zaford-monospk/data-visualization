using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Monospark
{
    // Reads a SINGLE static snapshot file -- FilePath is one file, exactly
    // like VtkUnstructuredGridReader, not a directory (that's only
    // VtkFrameSequenceReader, which needs a directory because its format
    // splits frames.raw from frames_meta.json). The source is either Init'd as
    // a plain path (disk / InitFromStreamingAssets) or InitFromAddressable'd --
    // an Addressable source is always CSV, loaded as a TextAsset and parsed
    // straight from its .text (see BuildDataRoutine), no temp file involved.
    // A FilePath source picks its format from the file's extension:
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
        // VtkUnstructuredGridReader for .vtk (see BuildDataRoutine).
        public WorldUpAxis WorldUp { get; set; } = WorldUpAxis.Y;

        // Physical bounds size (world units) of the source file's points --
        // valid once BuildData's callback has fired with SUCCESS. Lets a
        // caller (e.g. VtkFrameRenderer, via CFDFactory) size a display proxy
        // to the data's actual real-world extents instead of guessing from
        // voxel-grid resolution.
        public Vector3 DataSize { get; private set; }

        // Runs entirely as a coroutine (no async/Task) so every step -- the
        // Addressables load, TextAsset.text, CSV/VTK parsing, Texture3D build --
        // stays on the main thread, which Unity API (TextAsset.text/.bytes,
        // Texture3D.SetPixels/Apply) requires anyway. VtkFrameReader isn't a
        // MonoBehaviour, so CoroutineRunner hosts the coroutine on a hidden
        // helper GameObject instead -- callers keep calling BuildData exactly
        // as before, nothing about this method's signature changes.
        public override void BuildData(OnProcessTex3DData callback)
        {
            CoroutineRunner.Run(BuildDataRoutine(callback));
        }

        IEnumerator BuildDataRoutine(OnProcessTex3DData callback)
        {
            callback?.Invoke(new Progress { Status = eStatus.ONPROGRESS, ProgressValue = 0f }, null);

            Texture3D texture = null;
            Exception error = null;

            if (!string.IsNullOrEmpty(AddressableKey))
            {
                // Addressable source is always a .csv TextAsset, parsed
                // straight from its in-memory .text -- no FilePath, no temp
                // file, no format dispatch (that's only needed for the
                // FilePath cases below, which can also be .vtk).
                AsyncOperationHandle<TextAsset> handle = Addressables.LoadAssetAsync<TextAsset>(AddressableKey);
                yield return handle;

                if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null)
                {
                    error = new FileNotFoundException(
                        $"Addressable '{AddressableKey}' could not be loaded as a TextAsset.", AddressableKey);
                }
                else
                {
                    try
                    {
                        (Vector3[] points, float[] values) = ParseCsv(handle.Result.text, AddressableKey);
                        texture = BuildCsvTexture(points, values, $"addressable '{AddressableKey}'", callback);
                    }
                    catch (Exception e)
                    {
                        error = e;
                    }
                }
            }
            else if (!File.Exists(FilePath))
            {
                error = new FileNotFoundException($"File not found: {FilePath}", FilePath);
            }
            else
            {
                string extension = Path.GetExtension(FilePath);

                if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        (Vector3[] points, float[] values) = ParseCsv();
                        texture = BuildCsvTexture(points, values, FilePath, callback);
                    }
                    catch (Exception e)
                    {
                        error = e;
                    }
                }
                else if (extension.Equals(".vtk", StringComparison.OrdinalIgnoreCase))
                {
                    // ParseAllFields = false: this instance is only ever asked
                    // for FieldName's Texture3D (never BuildData(OnProcessBufferData)),
                    // so every OTHER SCALARS/VECTORS section in the file (e.g.
                    // room.vtk's unused Pressure/Velocity, on top of
                    // Temperature) gets skipped instead of fully parsed -- a
                    // large chunk of the total parse time for a file with
                    // several per-cell fields.
                    var inner = new VtkUnstructuredGridReader { FieldName = FieldName, ParseAllFields = false, WorldUp = WorldUp };
                    inner.Init(FilePath);

                    bool innerDone = false;
                    Texture3D innerTexture = null;
                    Exception innerError = null;

                    inner.BuildData((progress, tex) =>
                    {
                        switch (progress.Status)
                        {
                            case eStatus.SUCCESS:
                                DataSize = inner.Size;
                                innerTexture = tex;
                                innerDone = true;
                                break;
                            case eStatus.ERROR:
                                innerError = new Exception($"Failed to read '{FilePath}' as an unstructured grid.");
                                innerDone = true;
                                break;
                        }
                    });

                    while (!innerDone)
                        yield return null;

                    if (innerError != null)
                        error = innerError;
                    else
                        texture = innerTexture;
                }
                else
                {
                    error = new NotSupportedException(
                        $"{nameof(VtkFrameReader)} only reads .vtk or .csv files, got '{extension}' ({FilePath}).");
                }
            }

            if (error != null)
            {
                Debug.LogError($"[VtkFrameReader] BuildData failed (AddressableKey='{AddressableKey}', FilePath='{FilePath}'): {error}");
                callback?.Invoke(new Progress { Status = eStatus.ERROR, ProgressValue = 0f }, null);
                yield break;
            }

            Debug.Log($"[VtkFrameReader] Built texture: {texture.width}x{texture.height}x{texture.depth}");
            callback?.Invoke(new Progress { Status = eStatus.SUCCESS, ProgressValue = 1f }, texture);
        }

        // Shared tail of both CSV entry points (disk-file and Addressable):
        // bounds -> DataSize -> progress -> voxelize. sourceDescription is
        // only used for the "no points" error message.
        Texture3D BuildCsvTexture(Vector3[] points, float[] values, string sourceDescription, OnProcessTex3DData callback)
        {
            if (points.Length == 0)
                throw new InvalidDataException($"No points found in {sourceDescription}.");

            Bounds bounds = ComputeBounds(points);
            DataSize = bounds.size;

            callback?.Invoke(new Progress { Status = eStatus.ONPROGRESS, ProgressValue = 0.9f }, null);

            return ToTexture3D(points, values, bounds);
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

        // Disk entry point: FilePath, streamed rather than loaded whole (a CSV
        // export can be large).
        (Vector3[] points, float[] values) ParseCsv()
        {
            using var reader = new StreamReader(FilePath, Encoding.UTF8, true, StreamBufferSize);
            return ParseCsv(reader, FilePath);
        }

        // Addressable entry point: content is already fully in memory (a
        // TextAsset's .text), so there's no file/stream to open -- just wrap
        // it in a StringReader and run the same line-by-line parse below.
        (Vector3[] points, float[] values) ParseCsv(string content, string sourceName)
        {
            using var reader = new StringReader(content);
            return ParseCsv(reader, sourceName);
        }

        (Vector3[] points, float[] values) ParseCsv(TextReader reader, string sourceName)
        {
            string headerLine = reader.ReadLine() ?? throw new InvalidDataException($"{sourceName} is empty.");
            string[] headers = SplitCsvLine(headerLine);

            int valueIndex = FindColumn(headers, CsvValueColumn);
            int xIndex = FindColumn(headers, "X");
            int yIndex = FindColumn(headers, "Y");
            int zIndex = FindColumn(headers, "Z");

            if (valueIndex < 0)
                throw new KeyNotFoundException($"Column '{CsvValueColumn}' was not found in {sourceName}.");
            if (xIndex < 0 || yIndex < 0 || zIndex < 0)
                throw new KeyNotFoundException($"X/Y/Z columns were not found in {sourceName}.");

            int maxIndex = Mathf.Max(Mathf.Max(valueIndex, xIndex), Mathf.Max(yIndex, zIndex));

            var pointList = new List<Vector3>();
            var valueList = new List<float>();

            string line;
            while ((line = reader.ReadLine()) != null)
            {
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

            Debug.Log($"[VtkFrameReader] Read {pointList.Count} points from '{sourceName}' (value column '{headers[valueIndex].Trim()}')");

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

        // Minimal MonoBehaviour host for BuildData's coroutine -- VtkFrameReader
        // is a plain C# class (not a MonoBehaviour, unlike DataConvertManager),
        // and StartCoroutine only exists on MonoBehaviour. Lazily creates one
        // hidden GameObject the first time it's needed (HideAndDontSave keeps
        // it alive across scene loads and out of the Hierarchy/scene file) and
        // reuses it for every call after that, so BuildData's signature and
        // every existing caller (CFDFactory, DataConvertManager, TestAction)
        // stay unchanged.
        class CoroutineRunner : MonoBehaviour
        {
            static CoroutineRunner _instance;

            public static void Run(IEnumerator routine)
            {
                if (_instance == null)
                {
                    var host = new GameObject(nameof(CoroutineRunner)) { hideFlags = HideFlags.HideAndDontSave };
                    _instance = host.AddComponent<CoroutineRunner>();
                }
                _instance.StartCoroutine(routine);
            }
        }
    }
}
