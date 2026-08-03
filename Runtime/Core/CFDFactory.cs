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

        // Instances _CFDVolume/GridRenderer (pre-wired with a Material +
        // InstanceMesh) at worldPosition/rotation, then starts loading
        // dataPath's unstructured-grid snapshot into it — VtkUnstructuredGridRenderer
        // draws it as an instanced point/glyph cloud (Graphics.RenderMeshIndirect
        // / RenderPrimitivesIndirect). Returned immediately; Set() runs once
        // the async load completes.
        public VtkUnstructuredGridRenderer CreateGridRenderer(string dataPath, Vector3 worldPosition, Quaternion rotation)
        {
            GameObject instance = InstantiatePrefab(_instancedModelRenderer, worldPosition, rotation);
            var gridRenderer = instance.GetComponent<VtkUnstructuredGridRenderer>();
            if (gridRenderer == null)
                throw new MissingComponentException($"{_instancedModelRenderer} prefab has no {nameof(VtkUnstructuredGridRenderer)}.");

            void OnBufferReady(DataConverter.Progress progress, VtkUnstructuredGridData buffer)
            {
                if (progress.Status == DataConverter.eStatus.SUCCESS)
                    gridRenderer.Set(buffer);
            }

            instance.AddComponent<DataConvertManager>().GetMap<VtkUnstructuredGridReader>(OnBufferReady, dataPath);
            return gridRenderer;
        }

        // Instances _CFDVolume/VolumePlayer (pre-wired with a Material + child
        // Cube as TargetCube) at worldPosition/rotation, then starts loading
        // dataPath's voxelized time sequence into it — VtkFrameSequencePlayer
        // animates it as a raymarched volume (VolumeRenderer.shader). Returned
        // immediately; Set() runs once the async load completes.
        public VtkFrameSequencePlayer CreateVolumePlayer(string dataPath, Vector3 worldPosition, Quaternion rotation)
        {
            GameObject instance = InstantiatePrefab(_raymarchVolumePlayer, worldPosition, rotation);
            var player = instance.GetComponent<VtkFrameSequencePlayer>();
            if (player == null)
                throw new MissingComponentException($"{_raymarchVolumePlayer} prefab has no {nameof(VtkFrameSequencePlayer)}.");

            void OnSequenceReady(DataConverter.Progress progress, VtkFrameSequenceData sequence)
            {
                if (progress.Status == DataConverter.eStatus.SUCCESS)
                    player.Set(sequence);
            }

            instance.AddComponent<DataConvertManager>().GetMap<VtkFrameSequenceReader>(OnSequenceReady, dataPath);
            return player;
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