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
    // straight from its .text, no temp file involved. A FilePath source picks
    // its format from the file's extension:
    //   .vtk - a legacy ASCII VTK UNSTRUCTURED_GRID file (e.g. room.vtk).
    //          Parsing is delegated to VtkUnstructuredGridReader itself --
    //          same format it already reads, no reason to duplicate that
    //          ~250-line parser here. BuildData(OnProcessBufferData) is NOT
    //          supported for a .vtk source -- use VtkUnstructuredGridReader
    //          directly for that.
    //   .csv - a point-cloud export (e.g. Test_Room_16000.csv): one row per
    //          sample, "X (m)"/"Y (m)"/"Z (m)" position columns, a scalar
    //          value column (CsvValueColumn, default "Temperature"), and
    //          optionally "Velocity[i]/[j]/[k] (m/s)" columns (not every CSV
    //          export has them -- boundary-condition exports typically don't).
    // BuildData(OnProcessTex3DData) voxelizes either source into a static
    // Texture3D with the same RGBAHalf convention every volume reader in this
    // package uses: .r = normalized scalar value, .a = occupancy (0 where no
    // sample landed). BuildData(OnProcessBufferData) is CSV-only and requires
    // IncludeVelocity: it filters the (potentially 10,000+ row) CSV down to
    // rows whose velocity magnitude is at least MinVelocitySpeed -- each
    // surviving row becomes one degenerate single-point "cell" (no averaging,
    // unlike Texture3D voxelization) -- so the existing
    // VtkUnstructuredGridRenderer glyph pipeline (built for real .vtk cell
    // data) can display Velocity from a CSV point cloud too, with no changes
    // to that renderer. BuildData(OnProcessTex2DData) is CSV-only and
    // requires the source to be a genuine single 2D slice (exactly one axis
    // with no real spatial extent, e.g. a CFD "X1"/"X2" plane-cut export):
    // it voxelizes directly into a Texture2D over the two real axes, for
    // VolumeSlicePlane.shader to sample via the plane mesh's own UV.
    public class VtkFrameReader : DataConverter
    {
        const int MinResolution = 8;
        const int MaxResolution = 128;
        const int StreamBufferSize = 1 << 20; // 1 MB — fewer underlying reads over a large CSV
        const float KelvinToCelsiusOffset = 273.15f;

        // Fixed Celsius calibration range every Texture3D/Texture2D's r
        // channel is normalized against -- NOT each file's own local
        // min/max. Two different CFD exports of the very same physical
        // space can have very different local temperature spreads (e.g.
        // Summer1_X1.csv's hottest cell is ~41°C, Summer1_Z.csv's is
        // ~49°C); normalizing each against its own min/max made the same
        // color mean a different absolute temperature depending on which
        // file it came from, and the same real temperature render as a
        // different color between files. A shared, fixed scale fixes both
        // -- the tradeoff is any one file that only spans a narrow slice of
        // [0, 100] uses less of the LUT's contrast, which
        // VtkFrameRenderer.SetLutTemperatureRange exists to claw back
        // (narrows the visible band without touching how the texture itself
        // was normalized). Must match VtkUnstructuredGridReader's identical
        // constants (kept duplicated rather than shared, same as
        // MinResolution/MaxResolution above).
        const float TemperatureRangeMin = 0f;
        const float TemperatureRangeMax = 100f;

        // .vtk SCALARS field name, forwarded to the inner VtkUnstructuredGridReader.
        public string FieldName { get; set; } = "Temperature(C)";

        // .csv column header to voxelize, matched by prefix (so "Temperature"
        // matches the sample data's "Temperature (K)" without hardcoding the unit).
        public string CsvValueColumn { get; set; } = "Temperature";

        // Which axis the file's raw X/Y/Z (and Velocity[i]/[j]/[k]) treats as
        // "up" -- see WorldUpAxis. Applied to .csv points/velocities here, and
        // forwarded to the inner VtkUnstructuredGridReader for .vtk.
        public WorldUpAxis WorldUp { get; set; } = WorldUpAxis.Y;

        // When true, this CSV's Velocity[i]/[j]/[k] columns (if present) are
        // also parsed, and BuildData(OnProcessBufferData) becomes usable
        // instead of throwing NotSupportedException. False by default: an
        // extra Vector3 per row (and the downsample pass in
        // BuildData(OnProcessBufferData)) is wasted work for a caller that
        // only ever wants the Texture3D. Must be set before this instance's
        // FIRST BuildData call, of either kind -- the CSV is only ever parsed
        // once per instance (see EnsureCsvParsedRoutine) and cached for both,
        // so changing this afterward has no effect on an already-parsed reader.
        public bool IncludeVelocity { get; set; } = false;

        // Minimum velocity magnitude (m/s) a CSV row needs to be included in
        // BuildData(OnProcessBufferData)'s glyph cells -- the mechanism for
        // keeping a manageable cell count out of a CSV that can have 10,000+
        // rows. Filtering by speed (rather than spatially downsampling/
        // averaging into a coarser grid) keeps every surviving glyph an
        // actual, undistorted sample instead of a blurred average -- CFD
        // exports are typically dominated by near-stagnant rows anyway, so a
        // speed floor is usually also the more meaningful cut. 0 (the
        // default) includes every row with velocity data.
        public float MinVelocitySpeed { get; set; } = 0f;

        // Physical bounds size (world units) of the source file's points --
        // valid once BuildData's callback has fired with SUCCESS. Lets a
        // caller (e.g. VtkFrameRenderer, via CFDFactory) size a display proxy
        // to the data's actual real-world extents instead of guessing from
        // voxel-grid resolution.
        public Vector3 DataSize { get; private set; }

        // The fixed [TemperatureRangeMin, TemperatureRangeMax] calibration
        // range the built Texture3D/Texture2D was ACTUALLY normalized
        // against -- NOT this file's own local min/max (see
        // TemperatureRangeMin's doc comment). Valid once BuildData's
        // callback has fired with SUCCESS. Lets a caller (e.g.
        // VtkFrameRenderer.SetLutTemperatureRange) convert a real Celsius
        // value into the texture's normalized 0..1 space the same way
        // ToTexture3D/Build2DTexture themselves do.
        public float ValueMin { get; private set; }
        public float ValueMax { get; private set; }

        // Cached CSV parse, populated once by EnsureCsvParsedRoutine and
        // reused by both BuildData overloads -- a reader used for both a
        // Texture3D and a velocity buffer never re-parses the same
        // (potentially 10,000+ row) file twice. _csvVelocities is null when
        // IncludeVelocity was false at parse time, or this CSV simply has no
        // Velocity[i]/[j]/[k] columns.
        bool _csvParseAttempted;
        Vector3[] _csvPoints;
        float[] _csvValues;
        Vector3[] _csvVelocities;
        string _csvSourceDescription;
        Exception _csvParseError;

        // Runs entirely as a coroutine (no async/Task) so every step -- the
        // Addressables load, TextAsset.text, CSV/VTK parsing, Texture3D build --
        // stays on the main thread, which Unity API (TextAsset.text/.bytes,
        // Texture3D.SetPixels/Apply) requires anyway. VtkFrameReader isn't a
        // MonoBehaviour, so CoroutineRunner hosts the coroutine on a hidden
        // helper GameObject instead -- callers keep calling BuildData exactly
        // as before, nothing about this method's signature changes.
        public override void BuildData(OnProcessTex3DData callback)
        {
            CoroutineRunner.Run(BuildTex3DRoutine(callback));
        }

        IEnumerator BuildTex3DRoutine(OnProcessTex3DData callback)
        {
            callback?.Invoke(new Progress { Status = eStatus.ONPROGRESS, ProgressValue = 0f }, null);

            Texture3D texture = null;
            Exception error = null;

            bool isAddressable = !string.IsNullOrEmpty(AddressableKey);
            bool fileExists = !isAddressable && File.Exists(FilePath);
            string extension = isAddressable ? ".csv" : (fileExists ? Path.GetExtension(FilePath) : null);

            if (isAddressable || (fileExists && extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)))
            {
                yield return EnsureCsvParsedRoutine();
                error = _csvParseError;
                if (error == null)
                {
                    try
                    {
                        texture = BuildCsvTexture(_csvPoints, _csvValues, _csvSourceDescription, callback);
                    }
                    catch (Exception e)
                    {
                        error = e;
                    }
                }
            }
            else if (!fileExists)
            {
                error = new FileNotFoundException($"File not found: {FilePath}", FilePath);
            }
            else if (extension.Equals(".vtk", StringComparison.OrdinalIgnoreCase))
            {
                // ParseAllFields = false: this instance is only ever asked
                // for FieldName's Texture3D here (BuildData(OnProcessBufferData)
                // doesn't support a .vtk source at all -- see the class doc
                // comment), so every OTHER SCALARS/VECTORS section in the
                // file (e.g. room.vtk's unused Pressure/Velocity, on top of
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
                            ValueMin = inner.ValueMin;
                            ValueMax = inner.ValueMax;
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

            (Texture3D texture, float valueMin, float valueMax) = ToTexture3D(points, values, bounds);
            ValueMin = valueMin;
            ValueMax = valueMax;
            return texture;
        }

        // Builds a VtkUnstructuredGridData from this CSV's Velocity[i]/[j]/[k]
        // columns -- requires IncludeVelocity, and only supports a .csv
        // source (for .vtk, use VtkUnstructuredGridReader directly, which
        // already reads real cell connectivity/Velocity/Pressure).
        public override void BuildData(OnProcessBufferData callback)
        {
            CoroutineRunner.Run(BuildBufferDataRoutine(callback));
        }

        IEnumerator BuildBufferDataRoutine(OnProcessBufferData callback)
        {
            callback?.Invoke(new Progress { Status = eStatus.ONPROGRESS, ProgressValue = 0f }, null);

            Exception error = null;
            VtkUnstructuredGridData data = null;

            bool isAddressable = !string.IsNullOrEmpty(AddressableKey);
            bool isCsv = isAddressable ||
                (File.Exists(FilePath) && Path.GetExtension(FilePath).Equals(".csv", StringComparison.OrdinalIgnoreCase));

            if (!IncludeVelocity)
            {
                error = new NotSupportedException(
                    $"{nameof(VtkFrameReader)}.{nameof(IncludeVelocity)} is false -- set it to true before the " +
                    $"first BuildData call to build a {nameof(VtkUnstructuredGridData)} from this CSV's " +
                    "Velocity[i]/[j]/[k] columns.");
            }
            else if (!isCsv)
            {
                error = new NotSupportedException(
                    $"{nameof(VtkFrameReader)} only builds a {nameof(VtkUnstructuredGridData)} from a .csv " +
                    $"source; for .vtk, use {nameof(VtkUnstructuredGridReader)} directly.");
            }
            else
            {
                yield return EnsureCsvParsedRoutine();
                error = _csvParseError;

                if (error == null && _csvVelocities == null)
                    error = new KeyNotFoundException($"'{_csvSourceDescription}' has no Velocity[i]/[j]/[k] columns.");

                if (error == null)
                {
                    try
                    {
                        data = BuildVelocityGridData(_csvPoints, _csvValues, _csvVelocities, _csvSourceDescription);
                    }
                    catch (Exception e)
                    {
                        error = e;
                    }
                }
            }

            if (error != null)
            {
                Debug.LogError($"[VtkFrameReader] BuildData(OnProcessBufferData) failed " +
                                $"(AddressableKey='{AddressableKey}', FilePath='{FilePath}'): {error}");
                callback?.Invoke(new Progress { Status = eStatus.ERROR, ProgressValue = 0f }, null);
                yield break;
            }

            Debug.Log($"[VtkFrameReader] Built {data.Cells.Length} velocity glyph cells from " +
                      $"'{_csvSourceDescription}' (MinVelocitySpeed={MinVelocitySpeed}, {_csvPoints.Length} source rows).");
            callback?.Invoke(new Progress { Status = eStatus.SUCCESS, ProgressValue = 1f }, data);
        }

        // This reader reads one static file, not an animated sequence --
        // for that, use VtkFrameSequenceReader instead.
        public override void BuildData(OnProcessFrameSequenceData callback)
        {
            throw new NotSupportedException(
                $"{nameof(VtkFrameReader)} does not produce a {nameof(VtkFrameSequenceData)}; " +
                "use VtkFrameSequenceReader instead.");
        }

        // Builds a 2D texture from CSV data that's ALREADY effectively a
        // single slice -- exactly one axis with no real spatial extent (see
        // ComputeResolution/DegenerateAxisSize), e.g. a CFD "X1"/"X2"
        // plane-cut export (X collapses) or a "Z" export (Z collapses).
        // BuildData(OnProcessTex3DData) still handles this same data fine
        // too, baking it into a (MinAxisSize-padded, 1-voxel-thick) Texture3D
        // slab -- but VolumeSlicePlane.shader has no way to display a
        // Texture3D at all (see its own header comment), so this is the only
        // path that feeds it: the two REAL axes voxelize directly into a
        // Texture2D, which the shader samples via the plane mesh's own UV.
        // CSV-only; throws if the source isn't genuinely planar (not exactly
        // one degenerate axis).
        public override void BuildData(OnProcessTex2DData callback)
        {
            CoroutineRunner.Run(BuildTex2DRoutine(callback));
        }

        IEnumerator BuildTex2DRoutine(OnProcessTex2DData callback)
        {
            callback?.Invoke(new Progress { Status = eStatus.ONPROGRESS, ProgressValue = 0f }, null);

            Texture2D texture = null;
            Exception error = null;

            bool isAddressable = !string.IsNullOrEmpty(AddressableKey);
            bool isCsv = isAddressable ||
                (File.Exists(FilePath) && Path.GetExtension(FilePath).Equals(".csv", StringComparison.OrdinalIgnoreCase));

            if (!isCsv)
            {
                error = new NotSupportedException(
                    $"{nameof(VtkFrameReader)} only builds a 2D slice texture from a .csv source.");
            }
            else
            {
                yield return EnsureCsvParsedRoutine();
                error = _csvParseError;

                if (error == null)
                {
                    try
                    {
                        Bounds bounds = ComputeBounds(_csvPoints);
                        DataSize = bounds.size;

                        var result = Build2DTexture(_csvPoints, _csvValues, bounds, _csvSourceDescription);
                        texture = result.texture;
                        ValueMin = result.valueMin;
                        ValueMax = result.valueMax;
                    }
                    catch (Exception e)
                    {
                        error = e;
                    }
                }
            }

            if (error != null)
            {
                Debug.LogError($"[VtkFrameReader] BuildData(OnProcessTex2DData) failed " +
                                $"(AddressableKey='{AddressableKey}', FilePath='{FilePath}'): {error}");
                callback?.Invoke(new Progress { Status = eStatus.ERROR, ProgressValue = 0f }, null);
                yield break;
            }

            Debug.Log($"[VtkFrameReader] Built 2D slice texture: {texture.width}x{texture.height}");
            callback?.Invoke(new Progress { Status = eStatus.SUCCESS, ProgressValue = 1f }, texture);
        }

        // Parses this instance's CSV source (Addressable TextAsset or disk
        // FilePath) exactly once, caching points/values/velocities (and any
        // error) on this instance -- a second call, from either BuildData
        // overload, is a no-op that reuses whatever the first call produced.
        IEnumerator EnsureCsvParsedRoutine()
        {
            if (_csvParseAttempted)
                yield break;
            _csvParseAttempted = true;

            if (!string.IsNullOrEmpty(AddressableKey))
            {
                AsyncOperationHandle<TextAsset> handle = Addressables.LoadAssetAsync<TextAsset>(AddressableKey);
                yield return handle;

                if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null)
                {
                    _csvParseError = new FileNotFoundException(
                        $"Addressable '{AddressableKey}' could not be loaded as a TextAsset.", AddressableKey);
                    yield break;
                }

                _csvSourceDescription = $"addressable '{AddressableKey}'";
                try
                {
                    (_csvPoints, _csvValues, _csvVelocities) = ParseCsv(handle.Result.text, AddressableKey);
                }
                catch (Exception e)
                {
                    _csvParseError = e;
                }
                yield break;
            }

            if (!File.Exists(FilePath))
            {
                _csvParseError = new FileNotFoundException($"File not found: {FilePath}", FilePath);
                yield break;
            }

            _csvSourceDescription = FilePath;
            try
            {
                (_csvPoints, _csvValues, _csvVelocities) = ParseCsv();
            }
            catch (Exception e)
            {
                _csvParseError = e;
            }
        }

        // Filters the raw CSV rows down to ones whose velocity magnitude is
        // at least MinVelocitySpeed -- each surviving row becomes its own
        // degenerate single-point "cell" (no averaging/downsampling), so
        // VtkUnstructuredGridRenderer's Velocity-glyph rendering (built for
        // real .vtk cell data) gets a manageable, undistorted set of cells to
        // draw instead of one per raw CSV row. Field names match
        // VtkUnstructuredGridRenderer's own TemperatureField/PressureField/
        // VelocityField defaults, so a caller doesn't need to reconfigure it.
        // Pressure has no CSV equivalent, so every cell gets a uniform
        // placeholder (glyph size ends up uniform instead of pressure-scaled)
        // rather than requiring VtkUnstructuredGridRenderer.Set to make its
        // Pressure lookup optional.
        VtkUnstructuredGridData BuildVelocityGridData(
            Vector3[] points, float[] temperatures, Vector3[] velocities, string sourceDescription)
        {
            if (points.Length == 0)
                throw new InvalidDataException($"No points found in {sourceDescription}.");

            // sqrMagnitude comparison avoids a sqrt per row.
            float minSpeedSqr = MinVelocitySpeed * MinVelocitySpeed;

            var filteredPoints = new List<Vector3>();
            var filteredTemps = new List<float>();
            var filteredVels = new List<Vector3>();

            for (int i = 0; i < points.Length; i++)
            {
                if (velocities[i].sqrMagnitude < minSpeedSqr)
                    continue;

                filteredPoints.Add(points[i]);
                filteredTemps.Add(temperatures[i]);
                filteredVels.Add(velocities[i]);
            }

            int cellCount = filteredPoints.Count;
            var cells = new VtkCell[cellCount];
            for (int i = 0; i < cellCount; i++)
                cells[i] = new VtkCell { Type = 1 /* VTK_VERTEX */, PointIndices = new[] { i } };

            return new VtkUnstructuredGridData
            {
                Title = $"{sourceDescription} (velocity glyphs, {cellCount}/{points.Length} cells, " +
                        $"min speed {MinVelocitySpeed} m/s)",
                Points = filteredPoints.ToArray(),
                Cells = cells,
                CellScalars = new Dictionary<string, float[]>
                {
                    ["Temperature(C)"] = filteredTemps.ToArray(),
                    ["Pressure(Pa)"] = UniformArray(cellCount, 1f)
                },
                CellVectors = new Dictionary<string, Vector3[]>
                {
                    ["Velocity(m/s)"] = filteredVels.ToArray()
                }
            };
        }

        static float[] UniformArray(int count, float value)
        {
            var array = new float[count];
            for (int i = 0; i < count; i++)
                array[i] = value;
            return array;
        }

        // Disk entry point: FilePath, streamed rather than loaded whole (a CSV
        // export can be large).
        (Vector3[] points, float[] values, Vector3[] velocities) ParseCsv()
        {
            using var reader = new StreamReader(FilePath, Encoding.UTF8, true, StreamBufferSize);
            return ParseCsv(reader, FilePath);
        }

        // Addressable entry point: content is already fully in memory (a
        // TextAsset's .text), so there's no file/stream to open -- just wrap
        // it in a StringReader and run the same line-by-line parse below.
        (Vector3[] points, float[] values, Vector3[] velocities) ParseCsv(string content, string sourceName)
        {
            using var reader = new StringReader(content);
            return ParseCsv(reader, sourceName);
        }

        (Vector3[] points, float[] values, Vector3[] velocities) ParseCsv(TextReader reader, string sourceName)
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

            // CFD exports store Temperature in Kelvin ("Temperature (K)"),
            // not Celsius -- convert at parse time so every downstream
            // consumer (Texture3D voxelization, BuildVelocityGridData's
            // CellScalars["Temperature(C)"], TestUI's "°C" clip-range
            // display) sees actual Celsius instead of silently working with
            // Kelvin under a Celsius-implying name. Detected from the
            // MATCHED column's own header text, not hardcoded to
            // CsvValueColumn's default "Temperature" -- CsvValueColumn can
            // be repointed at a non-temperature column (e.g. "RH (%)"),
            // which must never get this conversion.
            bool valueIsKelvin = headers[valueIndex].IndexOf("(K)", StringComparison.OrdinalIgnoreCase) >= 0;

            // Velocity[i]/[j]/[k] are optional -- present in flow-field
            // exports (e.g. Test_Room_16000.csv) but not boundary-condition
            // ones (e.g. Summer1_Boundary.csv). Only looked for at all when
            // IncludeVelocity is set, since it's extra work a Texture3D-only
            // caller never uses; if IncludeVelocity is set but the columns
            // simply aren't in this file, wantVelocity just ends up false --
            // BuildData(OnProcessBufferData) reports that clearly on its own.
            int velIIndex = -1, velJIndex = -1, velKIndex = -1;
            bool wantVelocity = IncludeVelocity;
            if (wantVelocity)
            {
                velIIndex = FindColumn(headers, "Velocity[i]");
                velJIndex = FindColumn(headers, "Velocity[j]");
                velKIndex = FindColumn(headers, "Velocity[k]");
                wantVelocity = velIIndex >= 0 && velJIndex >= 0 && velKIndex >= 0;
            }

            int maxIndex = Mathf.Max(Mathf.Max(valueIndex, xIndex), Mathf.Max(yIndex, zIndex));
            if (wantVelocity)
                maxIndex = Mathf.Max(maxIndex, Mathf.Max(velIIndex, Mathf.Max(velJIndex, velKIndex)));

            var pointList = new List<Vector3>();
            var valueList = new List<float>();
            var velocityList = wantVelocity ? new List<Vector3>() : null;

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

                float value = ParseFloat(tokens[valueIndex]);
                if (valueIsKelvin)
                    value -= KelvinToCelsiusOffset;
                valueList.Add(value);

                if (wantVelocity)
                {
                    Vector3 velocity = new Vector3(
                        ParseFloat(tokens[velIIndex]), ParseFloat(tokens[velJIndex]), ParseFloat(tokens[velKIndex]));
                    velocityList.Add(WorldUp.ToUnity(velocity));
                }
            }

            Debug.Log($"[VtkFrameReader] Read {pointList.Count} points from '{sourceName}' " +
                      $"(value column '{headers[valueIndex].Trim()}'{(valueIsKelvin ? ", K->C converted" : "")}" +
                      $"{(wantVelocity ? ", with velocity" : "")})");

            return (pointList.ToArray(), valueList.ToArray(), wantVelocity ? velocityList.ToArray() : null);
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
        // Normalizes against the fixed [TemperatureRangeMin, TemperatureRangeMax]
        // calibration range, not this file's own min/max -- see
        // TemperatureRangeMin's doc comment for why. Also returns that same
        // fixed range (not the file's actual min/max) -- BuildCsvTexture
        // caches it on ValueMin/ValueMax, which VtkFrameRenderer.
        // SetLutTemperatureRange uses to convert a real Celsius value into
        // this texture's normalized 0..1 space, so it needs the range the
        // texture was ACTUALLY normalized against, not the narrower range
        // this one file happens to span.
        static (Texture3D texture, float valueMin, float valueMax) ToTexture3D(Vector3[] points, float[] values, Bounds bounds)
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
                // resX/resY/resZ == 1 (a degenerate/near-zero axis -- see
                // ComputeResolution) skips the division: with size.x == 0
                // (exactly, once a CFD "plane cut" export's near-identical
                // X values round to the same float) (p.x - min.x) / size.x
                // is 0/0 == NaN, which would otherwise scatter every point
                // into whatever fixed slice (int)NaN happens to cast to.
                int vx = resX == 1 ? 0 : Mathf.Clamp((int)((p.x - min.x) / size.x * resX), 0, resX - 1);
                int vy = resY == 1 ? 0 : Mathf.Clamp((int)((p.y - min.y) / size.y * resY), 0, resY - 1);
                int vz = resZ == 1 ? 0 : Mathf.Clamp((int)((p.z - min.z) / size.z * resZ), 0, resZ - 1);
                int index = vx + vy * resX + vz * resX * resY;

                float value = values[i];
                voxelSum[index] += value;
                voxelHits[index]++;

                minValue = Mathf.Min(minValue, value);
                maxValue = Mathf.Max(maxValue, value);
            }

            // Clamped, not left to run past 0/1: a value outside
            // [TemperatureRangeMin, TemperatureRangeMax] (out-of-calibration
            // data) would otherwise land past the LUT's own [0, 1] texture
            // edge -- clamping pins it to that edge's color instead of
            // sampling garbage.
            LogIfOutOfCalibrationRange(minValue, maxValue, "3D volume");

            float calibrationRange = TemperatureRangeMax - TemperatureRangeMin;
            var colors = new Color[voxelCountTotal];
            for (int i = 0; i < voxelCountTotal; i++)
            {
                if (voxelHits[i] == 0)
                {
                    colors[i] = Color.clear;
                    continue;
                }
                float normalized = Mathf.Clamp01((voxelSum[i] / voxelHits[i] - TemperatureRangeMin) / calibrationRange);
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

            return (texture, TemperatureRangeMin, TemperatureRangeMax);
        }

        // Warns rather than throws/clamps silently: data outside the fixed
        // calibration range still renders (clamped to the LUT's nearest
        // edge color above), but it's worth knowing about since it means
        // TemperatureRangeMin/Max no longer actually cover this file's data.
        static void LogIfOutOfCalibrationRange(float minValue, float maxValue, string what)
        {
            if (minValue < TemperatureRangeMin || maxValue > TemperatureRangeMax)
            {
                Debug.LogWarning($"[VtkFrameReader] {what} data range [{minValue:F1}, {maxValue:F1}] falls outside the " +
                                  $"fixed calibration range [{TemperatureRangeMin:F1}, {TemperatureRangeMax:F1}] -- " +
                                  "out-of-range values will clamp to the LUT's nearest edge color.");
            }
        }

        // A CFD "plane cut" export (e.g. Summer1_X1.csv/X2.csv, where every
        // row shares the same X) has no real spatial extent along that axis
        // at all -- once such near-identical values round to the same
        // float, bounds.size on that axis comes out exactly 0. Clamping
        // that up to MinResolution (the naive Mathf.Clamp below) would still
        // try to divide by that 0 size per-point in ToTexture3D (0/0 ==
        // NaN), scattering every single point into whatever one fixed slice
        // (int)NaN happens to cast to and leaving the other MinResolution-1
        // slices completely empty -- a thin sliver of real data pinned to
        // one edge of the padded box, with the rest empty, which is why the
        // raymarched result looked different depending on which side of
        // that axis the camera was on (one direction hits the sliver almost
        // immediately, the other crosses mostly-empty space first). Below
        // DegenerateAxisSize, treat the axis as having exactly ONE voxel
        // instead -- there's no real resolution to resolve there, so every
        // point correctly lands in that single slice, which then fills the
        // box's entire (physically thin) extent uniformly. (Separate from,
        // but related to, VtkFrameRenderer.MinAxisSize, which pads the
        // rendered box's world-space thickness so its Transform scale is
        // never truly zero -- this is about the texture's own voxel grid.)
        const float DegenerateAxisSize = 1e-5f;

        // Derives a near-cubic-voxel resolution from the data itself, same as
        // VtkUnstructuredGridReader.ComputeResolution: voxel edge length = cube
        // root of (bounds volume / sample count), clamped to a sane texture size range.
        static (int x, int y, int z) ComputeResolution(int sampleCount, Vector3 size)
        {
            float volume = Mathf.Max(size.x * size.y * size.z, Mathf.Epsilon);
            float voxelEdge = Mathf.Pow(volume / Mathf.Max(sampleCount, 1), 1f / 3f);

            return (
                ComputeAxisResolution(size.x, voxelEdge),
                ComputeAxisResolution(size.y, voxelEdge),
                ComputeAxisResolution(size.z, voxelEdge));
        }

        static int ComputeAxisResolution(float axisSize, float voxelEdge)
        {
            if (axisSize < DegenerateAxisSize)
                return 1;
            return Mathf.Clamp(Mathf.RoundToInt(axisSize / voxelEdge), MinResolution, MaxResolution);
        }

        // Same bucket-and-average voxelization as ToTexture3D, but over just
        // the two axes that actually vary -- degenerateAxis (found via
        // FindDegenerateAxis) is dropped entirely rather than padded/clamped
        // into a 1-voxel-thick third dimension. (U, V) are the remaining two
        // axes in X/Y/Z order, e.g. X degenerate -> (Y, Z); Z degenerate ->
        // (X, Y). Vector3's indexer (v[axis]) reads whichever component
        // uAxis/vAxis names without a per-axis switch.
        static (Texture2D texture, float valueMin, float valueMax) Build2DTexture(
            Vector3[] points, float[] values, Bounds bounds, string sourceDescription)
        {
            if (points.Length == 0)
                throw new InvalidDataException($"No points found in {sourceDescription}.");

            int degenerateAxis = FindDegenerateAxis(bounds.size, sourceDescription);
            int uAxis = degenerateAxis == 0 ? 1 : 0;
            int vAxis = degenerateAxis == 2 ? 1 : 2;

            Vector3 min = bounds.min;
            Vector3 size = bounds.size;
            float sizeU = size[uAxis];
            float sizeV = size[vAxis];

            (int resU, int resV) = Compute2DResolution(points.Length, sizeU, sizeV);

            int voxelCountTotal = resU * resV;
            var voxelSum = new float[voxelCountTotal];
            var voxelHits = new int[voxelCountTotal];

            float minValue = float.MaxValue;
            float maxValue = float.MinValue;

            for (int i = 0; i < points.Length; i++)
            {
                Vector3 p = points[i];
                int vu = Mathf.Clamp((int)((p[uAxis] - min[uAxis]) / sizeU * resU), 0, resU - 1);
                int vv = Mathf.Clamp((int)((p[vAxis] - min[vAxis]) / sizeV * resV), 0, resV - 1);
                int index = vu + vv * resU;

                float value = values[i];
                voxelSum[index] += value;
                voxelHits[index]++;

                minValue = Mathf.Min(minValue, value);
                maxValue = Mathf.Max(maxValue, value);
            }

            // A scattered CFD point cloud is rarely uniformly dense across
            // its bounds (denser near walls/features, sparser elsewhere),
            // so Compute2DResolution's average-density sizing still leaves
            // some cells with zero hits even though the resolution is
            // "right" overall -- left blank, those show up as visible holes
            // in what should read as one continuous field. FillEmptyCells
            // replaces each one with its nearest actually-sampled
            // neighbor's value (full occupancy, not faded) via a
            // multi-source flood fill, so the slice looks like a real CFD
            // contour plot instead of a sparse point scatter with gaps.
            var cellValue = new float[voxelCountTotal];
            var hasValue = new bool[voxelCountTotal];
            for (int i = 0; i < voxelCountTotal; i++)
            {
                if (voxelHits[i] == 0)
                    continue;
                cellValue[i] = voxelSum[i] / voxelHits[i];
                hasValue[i] = true;
            }
            FillEmptyCells(cellValue, hasValue, resU, resV);

            // Same fixed [TemperatureRangeMin, TemperatureRangeMax]
            // calibration range as ToTexture3D, not this file's own
            // min/max -- see TemperatureRangeMin's doc comment for why.
            LogIfOutOfCalibrationRange(minValue, maxValue, "2D slice");

            float calibrationRange = TemperatureRangeMax - TemperatureRangeMin;
            var colors = new Color[voxelCountTotal];
            for (int i = 0; i < voxelCountTotal; i++)
            {
                float normalized = Mathf.Clamp01((cellValue[i] - TemperatureRangeMin) / calibrationRange);
                colors[i] = new Color(normalized, normalized, normalized, 1f);
            }

            // Same RGBAHalf/.r=value/.a=occupancy convention as ToTexture3D
            // (VolumeSlicePlane.shader's sampling reads r/a the same way) --
            // occupancy (.a/g) is uniformly 1 here since FillEmptyCells
            // leaves nothing unfilled (barring a genuinely empty grid,
            // already ruled out by the points.Length == 0 check above).
            var texture = new Texture2D(resU, resV, TextureFormat.RGBAHalf, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            texture.SetPixels(colors);
            texture.Apply();

            return (texture, TemperatureRangeMin, TemperatureRangeMax);
        }

        // Multi-source BFS flood fill: seeds the queue with every cell that
        // already hasValue, then spreads each one outward (4-connected) to
        // its not-yet-valued neighbors one grid step at a time. Every cell
        // ends up with the value of whichever originally-sampled cell is
        // NEAREST to it in grid steps -- O(resU*resV) total since each cell
        // is enqueued/dequeued exactly once, regardless of how many empty
        // cells surround a given sample.
        static void FillEmptyCells(float[] cellValue, bool[] hasValue, int resU, int resV)
        {
            var queue = new Queue<int>();
            for (int i = 0; i < hasValue.Length; i++)
            {
                if (hasValue[i])
                    queue.Enqueue(i);
            }

            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                int u = index % resU;
                int v = index / resU;
                float value = cellValue[index];

                TryPropagate(u - 1, v, resU, resV, value, cellValue, hasValue, queue);
                TryPropagate(u + 1, v, resU, resV, value, cellValue, hasValue, queue);
                TryPropagate(u, v - 1, resU, resV, value, cellValue, hasValue, queue);
                TryPropagate(u, v + 1, resU, resV, value, cellValue, hasValue, queue);
            }
        }

        static void TryPropagate(
            int u, int v, int resU, int resV, float value, float[] cellValue, bool[] hasValue, Queue<int> queue)
        {
            if (u < 0 || u >= resU || v < 0 || v >= resV)
                return;

            int index = u + v * resU;
            if (hasValue[index])
                return;

            cellValue[index] = value;
            hasValue[index] = true;
            queue.Enqueue(index);
        }

        // Confirms bounds has EXACTLY one degenerate axis (see
        // DegenerateAxisSize) -- a genuine single 2D slice -- and returns
        // which one (0=X, 1=Y, 2=Z). Throws for a real 3D volume (zero
        // degenerate axes) or a line/point cloud (more than one), since
        // neither is representable as a single 2D texture.
        static int FindDegenerateAxis(Vector3 size, string sourceDescription)
        {
            bool xDeg = size.x < DegenerateAxisSize;
            bool yDeg = size.y < DegenerateAxisSize;
            bool zDeg = size.z < DegenerateAxisSize;

            int degenerateCount = (xDeg ? 1 : 0) + (yDeg ? 1 : 0) + (zDeg ? 1 : 0);
            if (degenerateCount != 1)
            {
                throw new InvalidDataException(
                    $"'{sourceDescription}' isn't a single 2D slice -- expected exactly one axis with no " +
                    $"real spatial extent, found {degenerateCount} (bounds size {size}). Use " +
                    $"BuildData(OnProcessTex3DData) instead for a genuine 3D volume.");
            }

            return xDeg ? 0 : (yDeg ? 1 : 2);
        }

        // Same idea as ComputeResolution, but for an area instead of a
        // volume: voxel edge length = square root of (bounds area / sample
        // count), clamped to the same sane texture size range.
        static (int u, int v) Compute2DResolution(int sampleCount, float sizeU, float sizeV)
        {
            float area = Mathf.Max(sizeU * sizeV, Mathf.Epsilon);
            float voxelEdge = Mathf.Sqrt(area / Mathf.Max(sampleCount, 1));

            int resU = Mathf.Clamp(Mathf.RoundToInt(sizeU / voxelEdge), MinResolution, MaxResolution);
            int resV = Mathf.Clamp(Mathf.RoundToInt(sizeV / voxelEdge), MinResolution, MaxResolution);
            return (resU, resV);
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
