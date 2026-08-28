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
        // terminal status.
        public VtkFrameRenderer CreateVolumeStatic(
            string dataPath, Vector3 worldPosition, Quaternion rotation, Action<bool> onComplete = null,
            WorldUpAxis worldUp = WorldUpAxis.Y, DataPathMode pathMode = DataPathMode.Disk)
        {
            GameObject instance = InstantiatePrefab(_raymarchVolumeStatic, worldPosition, rotation);
            var renderer = instance.GetComponent<VtkFrameRenderer>();
            if (renderer == null)
                throw new MissingComponentException($"{_raymarchVolumeStatic} prefab has no {nameof(VtkFrameRenderer)}.");

            // Constructed directly (not GetMap<VtkFrameReader>) so WorldUp can
            // be set before Init/BuildData run, and DataSize is still readable
            // off this instance after BuildData's callback fires -- reader.DataSize
            // is the source file's real-world extent when it had usable bounds
            // (.vtk POINTS / .csv X-Y-Z), which sizes TargetCube far more
            // meaningfully than its voxel-grid resolution.
            var reader = new VtkFrameReader { WorldUp = worldUp };

            void OnTextureReady(DataConverter.Progress progress, Texture3D texture)
            {
                switch (progress.Status)
                {
                    case DataConverter.eStatus.SUCCESS:
                        renderer.Set(texture, reader.DataSize);
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
            WorldUpAxis worldUp = WorldUpAxis.Y)
        {
            return CreateVolumeStatic(relativePath, worldPosition, rotation, onComplete, worldUp, DataPathMode.StreamingAssets);
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