using UnityEngine;

namespace Monospark
{
    // Runtime OnGUI test panel: spawns renderers/players through CFDFactory,
    // then drives them purely through IRenderStateControl (visibility +
    // material floats) rather than their concrete types, exercising the same
    // decoupled control surface a real UI would use.
    public class TestUI : MonoBehaviour
    {
        [Header("Grid Renderer (instanced)")]
        public string GridRendererDataPath;
        public Vector3 GridRendererPosition;
        public Vector3 GridRendererEulerRotation;

        [Header("Volume Player (raymarch)")]
        public string VolumePlayerDataPath;
        public Vector3 VolumePlayerPosition;
        public Vector3 VolumePlayerEulerRotation;

        IRenderStateControl _gridRenderer;
        IRenderStateControl _volumePlayer;
        bool _gridRendererVisible = true;
        bool _volumePlayerVisible = true;
        float _gridClipMin;
        float _gridClipMax = 1f;
        float _volumeClipMin;
        float _volumeClipMax = 1f;

        void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, 10, 320, 420), GUI.skin.box);

            GUILayout.Label("CFD Test UI");
            GUILayout.Space(6);

            DrawGridRendererSection();
            GUILayout.Space(12);
            DrawVolumePlayerSection();

            GUILayout.EndArea();
        }

        void DrawGridRendererSection()
        {
            GUILayout.Label("Grid Renderer (instanced)");

            if (GUILayout.Button("Create"))
            {
                if (CFDFactory.Instance == null)
                {
                    Debug.LogError("[TestUI] No CFDFactory in scene.");
                }
                else
                {
                    DestroyIfExists(_gridRenderer);
                    _gridRenderer = CFDFactory.Instance.CreateGridRenderer(
                        Application.dataPath + GridRendererDataPath, GridRendererPosition, Quaternion.Euler(GridRendererEulerRotation));
                    _gridRendererVisible = true;
                }
            }

            DrawVisibilityToggle(_gridRenderer, ref _gridRendererVisible);
            DrawClipRangeControls(_gridRenderer, ref _gridClipMin, ref _gridClipMax);
        }

        void DrawVolumePlayerSection()
        {
            GUILayout.Label("Volume Player (raymarch)");

            if (GUILayout.Button("Create"))
            {
                if (CFDFactory.Instance == null)
                {
                    Debug.LogError("[TestUI] No CFDFactory in scene.");
                }
                else
                {
                    DestroyIfExists(_volumePlayer);
                    _volumePlayer = CFDFactory.Instance.CreateVolumePlayer(
                        Application.dataPath + VolumePlayerDataPath, VolumePlayerPosition, Quaternion.Euler(VolumePlayerEulerRotation));
                    _volumePlayerVisible = true;
                }
            }

            DrawVisibilityToggle(_volumePlayer, ref _volumePlayerVisible);
            DrawClipRangeControls(_volumePlayer, ref _volumeClipMin, ref _volumeClipMax);
        }

        static void DrawVisibilityToggle(IRenderStateControl control, ref bool isVisible)
        {
            GUI.enabled = control != null;
            if (GUILayout.Button(isVisible ? "Hide" : "Show"))
            {
                isVisible = !isVisible;
                control?.SetVisibility(isVisible);
            }
            GUI.enabled = true;
        }

        static void DrawClipRangeControls(IRenderStateControl control, ref float clipMin, ref float clipMax)
        {
            GUI.enabled = control != null;

            GUILayout.Label($"Clip Min: {clipMin:F2}");
            float newMin = GUILayout.HorizontalSlider(clipMin, 0f, 1f);
            if (!Mathf.Approximately(newMin, clipMin))
            {
                clipMin = newMin;
                control?.SetMaterialFloat("_ClipMin", clipMin);
            }

            GUILayout.Label($"Clip Max: {clipMax:F2}");
            float newMax = GUILayout.HorizontalSlider(clipMax, 0f, 1f);
            if (!Mathf.Approximately(newMax, clipMax))
            {
                clipMax = newMax;
                control?.SetMaterialFloat("_ClipMax", clipMax);
            }

            GUI.enabled = true;
        }

        // Re-clicking Create otherwise leaks the previous instance's GameObject.
        static void DestroyIfExists(IRenderStateControl control)
        {
            if (control is Component component)
                Destroy(component.gameObject);
        }
    }
}