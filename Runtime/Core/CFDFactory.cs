using System;
using System.IO;
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
            WorldUpAxis worldUp = WorldUpAxis.Y)
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
            instance.AddComponent<DataConvertManager>().GetMap(OnBufferReady, dataPath, reader);
            return gridRenderer;
        }

        // Same as CreateGridRenderer, but relativePath is resolved against
        // Application.streamingAssetsPath (see DataConverter.InitFromStreamingAssets
        // for the same platform caveat: works on Desktop/Editor/iOS, not
        // Android/WebGL, since the reader underneath uses plain File I/O).
        public VtkUnstructuredGridRenderer CreateGridRendererFromStreamingAssets(
            string relativePath, Vector3 worldPosition, Quaternion rotation, Action<bool> onComplete = null,
            WorldUpAxis worldUp = WorldUpAxis.Y)
        {
            return CreateGridRenderer(
                Path.Combine(Application.streamingAssetsPath, relativePath), worldPosition, rotation, onComplete, worldUp);
        }

        // Instances _CFDVolume/VolumePlayer (pre-wired with a Material + child
        // Cube as TargetCube) at worldPosition/rotation, then starts loading
        // dataPath's voxelized time sequence into it — VtkFrameSequencePlayer
        // animates it as a raymarched volume (VolumeRenderer.shader). Returned
        // immediately; Set() runs once the async load completes. onComplete
        // (true = success, false = error) fires once on that terminal status.
        public VtkFrameSequencePlayer CreateVolumePlayer(
            string dataPath, Vector3 worldPosition, Quaternion rotation, Action<bool> onComplete = null)
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

            instance.AddComponent<DataConvertManager>().GetMap<VtkFrameSequenceReader>(OnSequenceReady, dataPath);
            return player;
        }

        // Same as CreateVolumePlayer, but relativePath is resolved against
        // Application.streamingAssetsPath (see DataConverter.InitFromStreamingAssets
        // for the same platform caveat: works on Desktop/Editor/iOS, not
        // Android/WebGL, since the reader underneath uses plain File I/O).
        public VtkFrameSequencePlayer CreateVolumePlayerFromStreamingAssets(
            string relativePath, Vector3 worldPosition, Quaternion rotation, Action<bool> onComplete = null)
        {
            return CreateVolumePlayer(
                Path.Combine(Application.streamingAssetsPath, relativePath), worldPosition, rotation, onComplete);
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
            WorldUpAxis worldUp = WorldUpAxis.Y)
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

            instance.AddComponent<DataConvertManager>().GetMap(OnTextureReady, dataPath, reader);
            return renderer;
        }

        // Same as CreateVolumeStatic, but relativePath is resolved against
        // Application.streamingAssetsPath (see DataConverter.InitFromStreamingAssets
        // for the same platform caveat: works on Desktop/Editor/iOS, not
        // Android/WebGL, since the reader underneath uses plain File I/O).
        public VtkFrameRenderer CreateVolumeStaticFromStreamingAssets(
            string relativePath, Vector3 worldPosition, Quaternion rotation, Action<bool> onComplete = null,
            WorldUpAxis worldUp = WorldUpAxis.Y)
        {
            return CreateVolumeStatic(
                Path.Combine(Application.streamingAssetsPath, relativePath), worldPosition, rotation, onComplete, worldUp);
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