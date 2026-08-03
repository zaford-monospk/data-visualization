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
        bool _gridRendererLoading;
        bool _volumePlayerLoading;
        float _gridClipMin;
        float _gridClipMax = 1f;
        float _gridVelocityClipMin;
        float _gridVelocityClipMax = 1f;
        float _volumeClipMin;
        float _volumeClipMax = 1f;

        void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, 10, 380, 640), GUI.skin.box);

            GUILayout.Label("CFD Test UI");
            GUILayout.Space(6);

            DrawGridRendererSection();
            GUILayout.Space(12);
            DrawVolumePlayerSection();

            GUILayout.EndArea();
        }

        void DrawGridRendererSection()
        {
            GUILayout.Label("Grid Renderer (instanced)" + (_gridRendererLoading ? " (loading...)" : ""));

            GUI.enabled = !_gridRendererLoading;
            if (GUILayout.Button("Create"))
            {
                if (CFDFactory.Instance == null)
                {
                    Debug.LogError("[TestUI] No CFDFactory in scene.");
                }
                else
                {
                    DestroyIfExists(_gridRenderer);
                    _gridRendererLoading = true;
                    _gridRenderer = CFDFactory.Instance.CreateGridRenderer(
                        Application.dataPath + GridRendererDataPath, GridRendererPosition, Quaternion.Euler(GridRendererEulerRotation),
                        _ => _gridRendererLoading = false);
                    _gridRendererVisible = true;
                }
            }
            GUI.enabled = true;

            DrawVisibilityToggle(_gridRenderer, ref _gridRendererVisible, _gridRendererLoading);
            GUILayout.Label("Temperature");
            DrawClipRangeControls(_gridRenderer, "_ClipMin", "_ClipMax", ref _gridClipMin, ref _gridClipMax, 100f, "°C");
            GUILayout.Label("Velocity"); // grid renderer only — VolumeRenderer.shader has no per-sample velocity
            DrawClipRangeControls(_gridRenderer, "_VelocityClipMin", "_VelocityClipMax", ref _gridVelocityClipMin, ref _gridVelocityClipMax);
        }

        void DrawVolumePlayerSection()
        {
            GUILayout.Label("Volume Player (raymarch)" + (_volumePlayerLoading ? " (loading...)" : ""));

            GUI.enabled = !_volumePlayerLoading;
            if (GUILayout.Button("Create"))
            {
                if (CFDFactory.Instance == null)
                {
                    Debug.LogError("[TestUI] No CFDFactory in scene.");
                }
                else
                {
                    DestroyIfExists(_volumePlayer);
                    _volumePlayerLoading = true;
                    _volumePlayer = CFDFactory.Instance.CreateVolumePlayer(
                        Application.dataPath + VolumePlayerDataPath, VolumePlayerPosition, Quaternion.Euler(VolumePlayerEulerRotation),
                        _ => _volumePlayerLoading = false);
                    _volumePlayerVisible = true;
                }
            }
            GUI.enabled = true;

            DrawVisibilityToggle(_volumePlayer, ref _volumePlayerVisible, _volumePlayerLoading);
            DrawClipRangeControls(_volumePlayer, "_ClipMin", "_ClipMax", ref _volumeClipMin, ref _volumeClipMax, 100f, "°C");
        }

        static void DrawVisibilityToggle(IRenderStateControl control, ref bool isVisible, bool loading)
        {
            GUI.enabled = control != null && !loading;
            if (GUILayout.Button(isVisible ? "Hide" : "Show"))
            {
                isVisible = !isVisible;
                control?.SetVisibility(isVisible);
            }
            GUI.enabled = true;
        }

        // Sliders always carry/send the raw normalized 0..1 value (what the
        // shader actually expects) — displayScale/displayUnit only affect the
        // label text, e.g. temperature shown as value*100 with a "°C" suffix.
        static void DrawClipRangeControls(
            IRenderStateControl control, string minProperty, string maxProperty, ref float clipMin, ref float clipMax,
            float displayScale = 1f, string displayUnit = "")
        {
            GUI.enabled = control != null;

            GUILayout.Label($"Min: {clipMin * displayScale:F1}{displayUnit}");
            float newMin = GUILayout.HorizontalSlider(clipMin, 0f, 1f);
            if (!Mathf.Approximately(newMin, clipMin))
            {
                clipMin = newMin;
                control?.SetMaterialFloat(minProperty, clipMin);
            }

            GUILayout.Label($"Max: {clipMax * displayScale:F1}{displayUnit}");
            float newMax = GUILayout.HorizontalSlider(clipMax, 0f, 1f);
            if (!Mathf.Approximately(newMax, clipMax))
            {
                clipMax = newMax;
                control?.SetMaterialFloat(maxProperty, clipMax);
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