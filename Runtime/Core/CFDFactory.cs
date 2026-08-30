using System;
using UnityEngine;

namespace Monospark
{
    public class CFDFactory : MonoBehaviour
    {
        private static CFDFactory _instance;
        public static CFDFactory Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new GameObject("CFDFactory").AddComponent<CFDFactory>();
                }
                return _instance;
            }
        }

        private readonly string _instancedModelRenderer = "_CFDVolume/GridRenderer";
        private readonly string _raymarchVolumePlayer = "_CFDVolume/VolumePlayer";
        private readonly string _raymarchVolumeStatic = "_CFDVolume/VolumeStatic";

        // Instances _CFDVolume/GridRenderer (pre-wired with a Material +
        // InstanceMesh) at worldPosition/rotation, then starts loading
        // dataPath's unstructured-grid snapshot into it — VtkUnstructuredGridRenderer
        // draws it as an instanced point/glyph cloud (Graphics.RenderMeshIndirect
        // / RenderPrimitivesIndirect). Returned immediately; Set() runs once
        // the async load completes. onComplete (true = success, false = error)
        // fires once on that terminal status — e.g. for a caller to re-enable
        // a Create button it disabled while this was loading.
        public VtkUnstructuredGridRenderer CreateGridRenderer(
            string dataPath, Vector3 worldPosition, Quaternion rotation, Action<bool> onComplete = null,
            WorldUpAxis worldUp = WorldUpAxis.Y, DataPathMode pathMode = DataPathMode.Disk)
        {
            GameObject instance = InstantiatePrefab(_instancedModelRenderer, worldPosition, rotation);
            var gridRenderer = instance.GetComponent<VtkUnstructuredGridRenderer>();
            if (gridRenderer == null)
                throw new MissingComponentException($"{_instancedModelRenderer} prefab has no {nameof(VtkUnstructuredGridRenderer)}.");

            void OnBufferReady(DataConverter.Progress progress, VtkUnstructuredGridData buffer)
            {
                switch (progress.Status)
                {
                    case DataConverter.eStatus.SUCCESS:
                        gridRenderer.Set(buffer);
                        onComplete?.Invoke(true);
                        break;
                    case DataConverter.eStatus.ERROR:
                        onComplete?.Invoke(false);
                        break;
                }
            }

            // Constructed directly (not GetMap<VtkUnstructuredGridReader>) so
            // WorldUp can be set before Init/BuildData run.
            var reader = new VtkUnstructuredGridReader { WorldUp = worldUp };
            instance.AddComponent<DataConvertManager>().GetMap(OnBufferReady, dataPath, reader, pathMode);
            return gridRenderer;
        }

        // Same as CreateGridRenderer with pathMode: DataPathMode.StreamingAssets
        // (see DataConverter.InitFromStreamingAssets for the platform caveat:
        // works on Desktop/Editor/iOS, not Android/WebGL, since the reader
        // underneath uses plain File I/O). Kept as a convenience overload.
        public VtkUnstructuredGridRenderer CreateGridRendererFromStreamingAssets(
            string relativePath, Vector3 worldPosition, Quaternion rotation, Action<bool> onComplete = null,
            WorldUpAxis worldUp = WorldUpAxis.Y)
        {
            return CreateGridRenderer(relativePath, worldPosition, rotation, onComplete, worldUp, DataPathMode.StreamingAssets);
        }

        // Same visual result as CreateGridRenderer (instanced point/glyph
        // cloud via VtkUnstructuredGridRenderer, colored by Temperature,
        // oriented by Velocity), but sourced from a CSV point cloud (via
        // VtkFrameReader.IncludeVelocity) instead of a .vtk file's real cell
        // connectivity -- for CFD exports that only ship as CSV (e.g.
        // Test_Room_16000.csv). dataPath's CSV must have Velocity[i]/[j]/[k]
        // columns -- not every export has them (boundary-condition exports
        // typically don't); BuildData reports that clearly if they're
        // missing. minVelocitySpeed filters out rows slower than that (m/s)
        // instead of spatially downsampling -- see VtkFrameReader.MinVelocitySpeed
        // -- raise it to cut down a CSV that's mostly near-stagnant rows.
        public VtkUnstructuredGridRenderer CreateVelocityGridFromCsv(
            string dataPath, Vector3 worldPosition, Quaternion rotation, Action<bool> onComplete = null,
            WorldUpAxis worldUp = WorldUpAxis.Y, float minVelocitySpeed = 0f, DataPathMode pathMode = DataPathMode.Disk)
        {
            GameObject instance = InstantiatePrefab(_instancedModelRenderer, worldPosition, rotation);
            var gridRenderer = instance.GetComponent<VtkUnstructuredGridRenderer>();
            if (gridRenderer == null)
                throw new MissingComponentException($"{_instancedModelRenderer} prefab has no {nameof(VtkUnstructuredGridRenderer)}.");

            void OnBufferReady(DataConverter.Progress progress, VtkUnstructuredGridData buffer)
            {
                switch (progress.Status)
                {
                    case DataConverter.eStatus.SUCCESS:
                        gridRenderer.Set(buffer);
                        onComplete?.Invoke(true);
                        break;
                    case DataConverter.eStatus.ERROR:
                        onComplete?.Invoke(false);
                        break;
                }
            }

            var reader = new VtkFrameReader { WorldUp = worldUp, IncludeVelocity = true, MinVelocitySpeed = minVelocitySpeed };
            instance.AddComponent<DataConvertManager>().GetMap(OnBufferReady, dataPath, reader, pathMode);
            return gridRenderer;
        }

        // Instances _CFDVolume/VolumePlayer (pre-wired with a Material + child
        // Cube as TargetCube) at worldPosition/rotation, then starts loading
        // dataPath's voxelized time sequence into it — VtkFrameSequencePlayer
        // animates it as a raymarched volume (VolumeRenderer.shader). Returned
        // immediately; Set() runs once the async load completes. onComplete
        // (true = success, false = error) fires once on that terminal status.
        public VtkFrameSequencePlayer CreateVolumePlayer(
            string dataPath, Vector3 worldPosition, Quaternion rotation, Action<bool> onComplete = null,
            DataPathMode pathMode = DataPathMode.Disk)
        {
            GameObject instance = InstantiatePrefab(_raymarchVolumePlayer, worldPosition, rotation);
            var player = instance.GetComponent<VtkFrameSequencePlayer>();
            if (player == null)
                throw new MissingComponentException($"{_raymarchVolumePlayer} prefab has no {nameof(VtkFrameSequencePlayer)}.");

            void OnSequenceReady(DataConverter.Progress progress, VtkFrameSequenceData sequence)
            {
                switch (progress.Status)
                {
                    case DataConverter.eStatus.SUCCESS:
                        player.Set(sequence);
                        onComplete?.Invoke(true);
                        break;
                    case DataConverter.eStatus.ERROR:
                        onComplete?.Invoke(false);
                        break;
                }
            }

            instance.AddComponent<DataConvertManager>().GetMap<VtkFrameSequenceReader>(OnSequenceReady, dataPath, pathMode);
            return player;
        }

        // Same as CreateVolumePlayer with pathMode: DataPathMode.StreamingAssets
        // (see DataConverter.InitFromStreamingAssets for the platform caveat:
        // works on Desktop/Editor/iOS, not Android/WebGL, since the reader
        // underneath uses plain File I/O). Kept as a convenience overload.
        public VtkFrameSequencePlayer CreateVolumePlayerFromStreamingAssets(
            string relativePath, Vector3 worldPosition, Quaternion rotation, Action<bool> onComplete = null)
        {
            return CreateVolumePlayer(relativePath, worldPosition, rotation, onComplete, DataPathMode.StreamingAssets);
        }

        // Instances _CFDVolume/VolumeStatic (pre-wired with a Material + child
        // Cube as TargetCube) at worldPosition/rotation, then starts loading
        // dataPath's single static snapshot file (.vtk or .csv -- see
        // VtkFrameReader) into it -- VtkFrameRenderer displays it as a single
        // static raymarched volume (VolumeRenderer.shader), no playback.
        // Returned immediately; Set() runs once the async load completes.
        // onComplete (true = success, false = error) fires once on that
        // terminal status. opaque is applied immediately (VtkFrameRenderer.
        // SetOpaque doesn't depend on the texture having loaded) -- forces
        // alpha to 1 wherever the raymarch hits any in-range data, instead
        // of the default density-based fade. info picks which of a
        // multi-field CFD CSV export's columns (Temperature, Velocity
        // magnitude, PMV, RH -- see eDataType) to voxelize and the fixed
        // calibration range it's normalized against (VtkFrameReader.Info) --
        // null (the default) falls back to Temperature/[0, 100]°C, this
        // reader's original behavior. Ignored for a .vtk source, which only
        // ever reads FieldName's single field.
        public VtkFrameRenderer CreateVolumeStatic(
            string dataPath, Vector3 worldPosition, Quaternion rotation, Action<bool> onComplete = null,
            WorldUpAxis worldUp = WorldUpAxis.Y, DataPathMode pathMode = DataPathMode.Disk, bool opaque = false,
            InfoTypes info = null)
        {
            GameObject instance = InstantiatePrefab(_raymarchVolumeStatic, worldPosition, rotation);
            var renderer = instance.GetComponent<VtkFrameRenderer>();
            if (renderer == null)
                throw new MissingComponentException($"{_raymarchVolumeStatic} prefab has no {nameof(VtkFrameRenderer)}.");

            renderer.SetOpaque(opaque);

            // Constructed directly (not GetMap<VtkFrameReader>) so WorldUp can
            // be set before Init/BuildData run, and DataSize/ValueMin/ValueMax
            // are still readable off this instance after BuildData's callback
            // fires -- reader.DataSize is the source file's real-world extent
            // when it had usable bounds (.vtk POINTS / .csv X-Y-Z), which
            // sizes TargetCube far more meaningfully than its voxel-grid
            // resolution; reader.ValueMin/ValueMax is the fixed calibration
            // range (info.LUTStarts/LUTEnds) the Texture3D was normalized
            // against, passed to renderer.Set so SetLutTemperatureRange can
            // later convert a real value in that same range.
            var reader = new VtkFrameReader { WorldUp = worldUp };
            if (info != null)
                reader.Info = info;

            void OnTextureReady(DataConverter.Progress progress, Texture3D texture)
            {
                switch (progress.Status)
                {
                    case DataConverter.eStatus.SUCCESS:
                        renderer.Set(texture, reader.DataSize, (reader.ValueMin, reader.ValueMax));
                        onComplete?.Invoke(true);
                        break;
                    case DataConverter.eStatus.ERROR:
                        onComplete?.Invoke(false);
                        break;
                }
            }

            instance.AddComponent<DataConvertManager>().GetMap(OnTextureReady, dataPath, reader, pathMode);
            return renderer;
        }

        // Same as CreateVolumeStatic with pathMode: DataPathMode.StreamingAssets
        // (see DataConverter.InitFromStreamingAssets for the platform caveat:
        // works on Desktop/Editor/iOS, not Android/WebGL, since the reader
        // underneath uses plain File I/O). Kept as a convenience overload --
        // for an Addressable source (DataPathMode.Addressable, which
        // VtkFrameReader alone currently supports), call CreateVolumeStatic
        // directly with dataPath set to the Addressable's address/key.
        public VtkFrameRenderer CreateVolumeStaticFromStreamingAssets(
            string relativePath, Vector3 worldPosition, Quaternion rotation, Action<bool> onComplete = null,
            WorldUpAxis worldUp = WorldUpAxis.Y, bool opaque = false, InfoTypes info = null)
        {
            return CreateVolumeStatic(relativePath, worldPosition, rotation, onComplete, worldUp, DataPathMode.StreamingAssets, opaque, info);
        }

        // Same _CFDVolume/VolumeStatic prefab as CreateVolumeStatic, but
        // drives TargetPlane/SliceMaterial directly via
        // VtkFrameRenderer.SetSlice2D instead of TargetCube/Material's
        // raymarched Texture3D path -- for a CSV that's ALREADY effectively
        // a single 2D slice (e.g. a CFD "X1"/"X2" plane-cut export, see
        // VtkFrameReader.BuildData(OnProcessTex2DData)). onComplete(false)
        // fires if the CSV isn't genuinely planar (not exactly one
        // degenerate axis) as well as on any other load failure.
        // TargetCube/Material are left completely untouched by this call --
        // only the slice plane shows this data. No opaque parameter here
        // (unlike CreateVolumeStatic): VolumeSlicePlane.shader is always
        // fully opaque wherever it draws at all now (it clip()s away
        // out-of-range/unfilled pixels instead of fading them) -- see the
        // shader's own header comment. info picks which of a multi-field
        // CFD CSV export's columns (Temperature, Velocity magnitude, PMV,
        // RH -- see eDataType) to voxelize and the fixed calibration range
        // it's normalized against (VtkFrameReader.Info) -- null (the
        // default) falls back to Temperature/[0, 100]°C.
        public VtkFrameRenderer CreateSlice2DFromCsv(
            string dataPath, Vector3 worldPosition, Quaternion rotation, Action<bool> onComplete = null,
            WorldUpAxis worldUp = WorldUpAxis.Y, DataPathMode pathMode = DataPathMode.Disk, InfoTypes info = null)
        {
            GameObject instance = InstantiatePrefab(_raymarchVolumeStatic, worldPosition, rotation);
            var renderer = instance.GetComponent<VtkFrameRenderer>();
            if (renderer == null)
                throw new MissingComponentException($"{_raymarchVolumeStatic} prefab has no {nameof(VtkFrameRenderer)}.");

            var reader = new VtkFrameReader { WorldUp = worldUp };
            if (info != null)
                reader.Info = info;

            void OnTexture2DReady(DataConverter.Progress progress, Texture2D texture)
            {
                switch (progress.Status)
                {
                    case DataConverter.eStatus.SUCCESS:
                        renderer.SetSlice2D(texture);
                        onComplete?.Invoke(true);
                        break;
                    case DataConverter.eStatus.ERROR:
                        onComplete?.Invoke(false);
                        break;
                }
            }

            instance.AddComponent<DataConvertManager>().GetMap(OnTexture2DReady, dataPath, reader, pathMode);
            return renderer;
        }

        // Keeps the LUT sample position from ever landing exactly on 0 or 1,
        // same convention/reason as the shaders' own LutEdgeInset (see
        // VolumeSlicePlane.shader) -- sampling AT either edge hits an
        // ambiguous boundary position of the LUT texture.
        const float LutEdgeInset = 0.001f;

        // Unlike CreateVolumeStatic/CreateSlice2DFromCsv, this doesn't spawn
        // any renderer/prefab at all -- there's nothing to place in the
        // world here, just a texture. It reads dataPath the same way
        // CreateSlice2DFromCsv does (CSV-only, exactly one degenerate axis
        // -- see VtkFrameReader.BuildData(OnProcessTex2DData)), but instead
        // of handing the raw normalized-value texture to VolumeSlicePlane.
        // shader for per-frame LUT sampling, it bakes the final color into
        // every pixel itself, once, on the CPU: recovers each pixel's real
        // value from the raw texture (normalized against info's fixed
        // calibration range -- VtkFrameReader.Info), remaps it into
        // [lutDisplayMin, lutDisplayMax] (same start/end convention as
        // VtkFrameRenderer.SetLutTemperatureRange -- independent of info's
        // calibration range, so the LUT can zoom into a narrower band of
        // it), and samples lutTexture at that U. The result is a plain
        // color Texture2D any ordinary shader (a Standard/Lit/Unlit
        // material's albedo, for instance) can use directly, with no custom
        // shader or per-frame LUT logic required. lutTexture must have Read/
        // Write enabled (GetPixelBilinear needs CPU-side pixel access).
        // info picks which of a multi-field CFD CSV export's columns
        // (Temperature, Velocity magnitude, PMV, RH -- see eDataType) to
        // voxelize -- null (the default) falls back to Temperature/[0,
        // 100]°C, in which case lutDisplayMin/Max are Celsius; for any other
        // DataType they're in whatever unit that column uses instead. See
        // VtkFrameReader.Info's doc comment for why voxelization uses a
        // fixed calibration range at all, distinct from this display range.
        // onComplete receives the baked texture on success, or null if the
        // CSV isn't genuinely planar (or any other load failure) -- there's
        // no "false" case to report otherwise, unlike the bool onComplete
        // callbacks above, since the texture itself IS the result.
        public void CreateSimpleTexture2DFromCsv(
            string dataPath, Texture2D lutTexture, float lutDisplayMin, float lutDisplayMax,
            Action<Texture2D> onComplete, WorldUpAxis worldUp = WorldUpAxis.Y, DataPathMode pathMode = DataPathMode.Disk,
            InfoTypes info = null)
        {
            if (lutTexture == null)
                throw new ArgumentNullException(nameof(lutTexture));

            var reader = new VtkFrameReader { WorldUp = worldUp };
            if (info != null)
                reader.Info = info;

            void OnTexture2DReady(DataConverter.Progress progress, Texture2D rawTexture)
            {
                switch (progress.Status)
                {
                    case DataConverter.eStatus.SUCCESS:
                        Texture2D baked = BakeSimpleTexture2D(
                            rawTexture, lutTexture, reader.ValueMin, reader.ValueMax,
                            lutDisplayMin, lutDisplayMax);
                        Destroy(rawTexture); // only the baked copy is handed back -- the raw normalized texture was scratch data
                        onComplete?.Invoke(baked);
                        break;
                    case DataConverter.eStatus.ERROR:
                        onComplete?.Invoke(null);
                        break;
                }
            }

            // No GameObject/prefab to host a DataConvertManager on (nothing
            // is instantiated here) -- DataConvertManager.GetMap is just a
            // thin InitFromPath + BuildData wrapper anyway, so call the
            // reader directly instead.
            reader.InitFromPath(dataPath, pathMode);
            reader.BuildData((DataConverter.OnProcessTex2DData)OnTexture2DReady);
        }

        // r is normalized 0..1 against the reader's own fixed calibration
        // range [rawValueMin, rawValueMax] (VtkFrameReader.Info.LUTStarts/
        // LUTEnds -- see VtkFrameReader.Build2DTexture) -- recovers the real
        // value from it, then remaps that into the LUT's own, independently
        // chosen [lutDisplayMin, lutDisplayMax] range before sampling,
        // clamping out-of-range values to the nearest end's color rather
        // than wrapping/extrapolating past it.
        static Texture2D BakeSimpleTexture2D(
            Texture2D rawTexture, Texture2D lutTexture, float rawValueMin, float rawValueMax,
            float lutDisplayMin, float lutDisplayMax)
        {
            Color[] rawPixels = rawTexture.GetPixels();
            var bakedPixels = new Color[rawPixels.Length];

            float rawRange = Mathf.Max(rawValueMax - rawValueMin, Mathf.Epsilon);
            float lutRange = Mathf.Max(lutDisplayMax - lutDisplayMin, Mathf.Epsilon);

            for (int i = 0; i < rawPixels.Length; i++)
            {
                float value = rawValueMin + rawPixels[i].r * rawRange;

                float u01 = Mathf.Clamp01((value - lutDisplayMin) / lutRange);
                float u = Mathf.Lerp(LutEdgeInset, 1f - LutEdgeInset, u01);

                bakedPixels[i] = lutTexture.GetPixelBilinear(u, 0.5f);
            }

            var baked = new Texture2D(rawTexture.width, rawTexture.height, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            baked.SetPixels(bakedPixels);
            baked.Apply();
            return baked;
        }

        static GameObject InstantiatePrefab(string resourcePath, Vector3 worldPosition, Quaternion rotation)
        {
            var prefab = Resources.Load<GameObject>(resourcePath);
            if (prefab == null)
                throw new MissingReferenceException($"No prefab found at Resources/{resourcePath}.");
            return Instantiate(prefab, worldPosition, rotation);
        }
    }
}