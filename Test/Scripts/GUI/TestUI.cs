using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Monospark
{
    // Runtime OnGUI test panel: spawns renderers/players through CFDFactory,
    // then drives them purely through IRenderStateControl (visibility +
    // material floats) rather than their concrete types, exercising the same
    // decoupled control surface a real UI would use.
    public class TestUI : MonoBehaviour
    {
        enum PathMode
        {
            Assets,          // Application.dataPath + a "/Resources/..."-style suffix
            StreamingAssets, // Application.streamingAssetsPath + suffix
            Disk             // an absolute path anywhere on disk, outside the project
        }

        static readonly string[] PathModeLabels = { "Assets", "StreamingAssets", "Disk" };

        [Header("Grid Renderer (instanced)")]
        public Vector3 GridRendererPosition;
        public Vector3 GridRendererEulerRotation;

        [Header("Volume Player (raymarch)")]
        public Vector3 VolumePlayerPosition;
        public Vector3 VolumePlayerEulerRotation;
        public Shader VolumeShaderOriginal;
        public Shader VolumeShaderInterpolate;

        // Not Inspector-exposed: these are edited live via the GUI's own text
        // field, so a stale/wrong Inspector value could never end up silently
        // in use — the field always reflects exactly what's on screen.
        string _gridRendererDataPath = "";
        string _volumePlayerDataPath = "";
        PathMode _gridPathMode;
        PathMode _volumePathMode;

        IRenderStateControl _gridRenderer;
        IRenderStateControl _volumePlayer;
        bool _gridRendererVisible = true;
        bool _volumePlayerVisible = true;
        bool _gridRendererLoading;
        bool _volumePlayerLoading;
        string _gridPathError;
        string _volumePathError;
        float _gridClipMin;
        float _gridClipMax = 1f;
        float _gridVelocityClipMin;
        float _gridVelocityClipMax = 1f;
        float _volumeClipMin;
        float _volumeClipMax = 1f;
        bool _volumeInterpolate;
        bool _collapsed;

        const float PanelWidth = 380f;
        const float PanelHeight = 820f;
        const float HeaderHeight = 30f;

        static GUIStyle _errorStyle;

        void OnGUI()
        {
            float height = _collapsed ? HeaderHeight : PanelHeight;
            GUILayout.BeginArea(new Rect(10, 10, PanelWidth, height), GUI.skin.box);

            GUILayout.BeginHorizontal();
            GUILayout.Label("CFD Test UI");
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(_collapsed ? "+" : "-", GUILayout.Width(24)))
                _collapsed = !_collapsed;
            GUILayout.EndHorizontal();

            if (!_collapsed)
            {
                GUILayout.Space(6);
                DrawGridRendererSection();
                GUILayout.Space(12);
                DrawVolumePlayerSection();
            }

            GUILayout.EndArea();
        }

        void DrawGridRendererSection()
        {
            GUILayout.Label("Grid Renderer (instanced)" + (_gridRendererLoading ? " (loading...)" : ""));

            _gridRendererDataPath = GUILayout.TextField(_gridRendererDataPath);
            _gridPathMode = (PathMode)GUILayout.SelectionGrid((int)_gridPathMode, PathModeLabels, PathModeLabels.Length);

            if (_gridPathMode == PathMode.Disk)
                DrawBrowseButton(isFolder: false, "Select VTK File", "vtk", path => _gridRendererDataPath = path);

            GUI.enabled = !_gridRendererLoading;
            if (GUILayout.Button("Create"))
            {
                if (CFDFactory.Instance == null)
                {
                    Debug.LogError("[TestUI] No CFDFactory in scene.");
                }
                else if (!TryResolvePath(_gridRendererDataPath, _gridPathMode, isDirectory: false,
                             out string resolvedPath, out _gridPathError))
                {
                    Debug.LogWarning($"[TestUI] {_gridPathError}");
                }
                else
                {
                    DestroyIfExists(_gridRenderer);
                    _gridRendererLoading = true;
                    _gridRenderer = CFDFactory.Instance.CreateGridRenderer(
                        resolvedPath, GridRendererPosition, Quaternion.Euler(GridRendererEulerRotation),
                        _ => _gridRendererLoading = false);
                    _gridRendererVisible = true;
                    // Read the prefab-configured material's actual current values
                    // rather than assuming the slider defaults (0/1) match them.
                    _gridClipMin = _gridRenderer.GetMaterialFloat("_ClipMin");
                    _gridClipMax = _gridRenderer.GetMaterialFloat("_ClipMax");
                    _gridVelocityClipMin = _gridRenderer.GetMaterialFloat("_VelocityClipMin");
                    _gridVelocityClipMax = _gridRenderer.GetMaterialFloat("_VelocityClipMax");
                }
            }
            GUI.enabled = true;

            if (!string.IsNullOrEmpty(_gridPathError))
                GUILayout.Label(_gridPathError, ErrorStyle());

            DrawVisibilityToggle(_gridRenderer, ref _gridRendererVisible, _gridRendererLoading);
            GUILayout.Label("Temperature");
            DrawClipRangeControls(_gridRenderer, "_ClipMin", "_ClipMax", ref _gridClipMin, ref _gridClipMax, 100f, "°C");
            GUILayout.Label("Velocity"); // grid renderer only — VolumeRenderer.shader has no per-sample velocity
            DrawClipRangeControls(_gridRenderer, "_VelocityClipMin", "_VelocityClipMax", ref _gridVelocityClipMin, ref _gridVelocityClipMax);
        }

        void DrawVolumePlayerSection()
        {
            GUILayout.Label("Volume Player (raymarch)" + (_volumePlayerLoading ? " (loading...)" : ""));

            _volumePlayerDataPath = GUILayout.TextField(_volumePlayerDataPath);

            PathMode newVolumePathMode = (PathMode)GUILayout.SelectionGrid((int)_volumePathMode, PathModeLabels, PathModeLabels.Length);
            if (newVolumePathMode != _volumePathMode)
            {
                _volumePathMode = newVolumePathMode;
                if (_volumePathMode == PathMode.StreamingAssets)
                    _volumePlayerDataPath = "/CFDDatas/Timestep/0";
            }

            if (_volumePathMode == PathMode.Disk)
                DrawBrowseButton(isFolder: true, "Select Frame Sequence Folder", null, path => _volumePlayerDataPath = path);

            GUI.enabled = !_volumePlayerLoading;
            if (GUILayout.Button("Create"))
            {
                if (CFDFactory.Instance == null)
                {
                    Debug.LogError("[TestUI] No CFDFactory in scene.");
                }
                // VtkFrameSequenceReader.FilePath is a directory (frames.raw +
                // frames_meta.json live inside it), unlike the grid renderer's
                // single-file path — hence isDirectory: true here.
                else if (!TryResolvePath(_volumePlayerDataPath, _volumePathMode, isDirectory: true,
                             out string resolvedPath, out _volumePathError))
                {
                    Debug.LogWarning($"[TestUI] {_volumePathError}");
                }
                else
                {
                    DestroyIfExists(_volumePlayer);
                    _volumePlayerLoading = true;
                    _volumePlayer = CFDFactory.Instance.CreateVolumePlayer(
                        resolvedPath, VolumePlayerPosition, Quaternion.Euler(VolumePlayerEulerRotation),
                        _ => _volumePlayerLoading = false);
                    _volumePlayerVisible = true;
                    // Read the prefab-configured material's actual current values
                    // rather than assuming the slider defaults (0/1) match them.
                    _volumeClipMin = _volumePlayer.GetMaterialFloat("_ClipMin");
                    _volumeClipMax = _volumePlayer.GetMaterialFloat("_ClipMax");
                    _volumeInterpolate = false; // a freshly created player always starts non-interpolating
                }
            }
            GUI.enabled = true;

            if (!string.IsNullOrEmpty(_volumePathError))
                GUILayout.Label(_volumePathError, ErrorStyle());

            DrawVisibilityToggle(_volumePlayer, ref _volumePlayerVisible, _volumePlayerLoading);

            GUI.enabled = _volumePlayer != null;
            if (GUILayout.Button(_volumeInterpolate ? "Interpolate: On" : "Interpolate: Off"))
            {
                _volumeInterpolate = !_volumeInterpolate;

                // Only VolumeRenderer_Interpolate.shader reads _VolumeNext/_FrameBlend
                // at all, so the toggle drives both the shader and the feature together.
                Shader shader = _volumeInterpolate ? VolumeShaderInterpolate : VolumeShaderOriginal;
                if (shader == null)
                    Debug.LogWarning($"[TestUI] Volume Shader {(_volumeInterpolate ? "Interpolate" : "Original")} isn't assigned in the Inspector — shader not changed.");

                _volumePlayer?.SetShader(shader);
                _volumePlayer?.SetInterpolation(_volumeInterpolate);
            }
            GUI.enabled = true;

            DrawClipRangeControls(_volumePlayer, "_ClipMin", "_ClipMax", ref _volumeClipMin, ref _volumeClipMax, 100f, "°C");
        }

        // Editor-only: there's no cross-platform runtime file/folder picker
        // without a native plugin, and this panel is an Editor testing tool.
        // Compiles out entirely in player builds.
        static void DrawBrowseButton(bool isFolder, string title, string extension, System.Action<string> onPicked)
        {
#if UNITY_EDITOR
            if (GUILayout.Button("Browse..."))
            {
                string picked = isFolder
                    ? EditorUtility.OpenFolderPanel(title, Application.dataPath, "")
                    : EditorUtility.OpenFilePanel(title, Application.dataPath, extension);

                if (!string.IsNullOrEmpty(picked))
                    onPicked(picked);
            }
#endif
        }

        // Assets/StreamingAssets modes resolve pathSuffix against a project
        // base folder (same platform caveat as DataConverter.InitFromStreamingAssets:
        // works on Desktop/Editor/iOS, not Android/WebGL); Disk mode treats it as
        // an already-absolute path picked via Browse (or typed) and uses it as-is.
        // Either way, checks the resolved path actually exists before the caller
        // hands it to CFDFactory, rather than finding out from a failed async load.
        static bool TryResolvePath(string pathSuffix, PathMode mode, bool isDirectory, out string resolvedPath, out string error)
        {
            switch (mode)
            {
                case PathMode.StreamingAssets:
                    resolvedPath = Path.Combine(Application.streamingAssetsPath, pathSuffix.TrimStart('/', '\\'));
                    break;
                case PathMode.Disk:
                    resolvedPath = pathSuffix;
                    break;
                default:
                    resolvedPath = Application.dataPath + pathSuffix;
                    break;
            }

            bool exists = isDirectory ? Directory.Exists(resolvedPath) : File.Exists(resolvedPath);
            error = exists ? null : $"{(isDirectory ? "Directory" : "File")} not found:\n{resolvedPath}";
            return exists;
        }

        static GUIStyle ErrorStyle()
        {
            if (_errorStyle == null)
                _errorStyle = new GUIStyle(GUI.skin.label) { normal = { textColor = Color.red }, wordWrap = true };
            return _errorStyle;
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