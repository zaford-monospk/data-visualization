using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Monospark
{
    // Runtime OnGUI test panel: spawns a VtkFrameRenderer through CFDFactory,
    // then drives it purely through IRenderStateControl (visibility +
    // material floats) rather than its concrete type, exercising the same
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
        static readonly string[] WorldUpLabels = { "Y up", "Z up" };

        [Header("Volume Static (raymarch, single frame)")]
        public Vector3 VolumeStaticPosition;
        public Vector3 VolumeStaticEulerRotation;

        // Not Inspector-exposed: edited live via the GUI's own text field, so
        // a stale/wrong Inspector value could never end up silently in use —
        // the field always reflects exactly what's on screen.
        string _volumeStaticDataPath = "";
        PathMode _volumeStaticPathMode;

        // .vtk POINTS / .csv X-Y-Z may be authored Z-up (common for CAD/CFD
        // tooling) even though Unity is Y-up -- see VtkFrameReader.WorldUp.
        WorldUpAxis _volumeStaticWorldUp;

        IRenderStateControl _volumeStatic;
        bool _volumeStaticVisible = true;
        bool _volumeStaticLoading;
        string _volumeStaticPathError;
        float _volumeStaticClipMin;
        float _volumeStaticClipMax = 1f;
        bool _collapsed;

        const float PanelWidth = 380f;
        const float PanelHeight = 420f;
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
                DrawVolumeStaticSection();
            }

            GUILayout.EndArea();
        }

        // Through VtkFrameReader/VtkFrameRenderer -- reads a single static
        // snapshot FILE (.vtk or .csv, not a folder) and displays it as a
        // raymarched volume (VolumeRenderer.shader), no Update()-driven
        // playback, hence no Interpolate button (there's only ever one
        // texture, nothing to blend toward).
        void DrawVolumeStaticSection()
        {
            GUILayout.Label("Volume Static (raymarch, single frame)" + (_volumeStaticLoading ? " (loading...)" : ""));

            _volumeStaticDataPath = GUILayout.TextField(_volumeStaticDataPath);

            PathMode newVolumeStaticPathMode = (PathMode)GUILayout.SelectionGrid((int)_volumeStaticPathMode, PathModeLabels, PathModeLabels.Length);
            if (newVolumeStaticPathMode != _volumeStaticPathMode)
            {
                _volumeStaticPathMode = newVolumeStaticPathMode;
                if (_volumeStaticPathMode == PathMode.StreamingAssets)
                    _volumeStaticDataPath = "/CFDDatas/Original/room.vtk";
            }

            if (_volumeStaticPathMode == PathMode.Disk)
                DrawBrowseButton(isFolder: false, "Select VTK/CSV File", null, path => _volumeStaticDataPath = path);

            // Same Z-up-vs-Y-up concern noted above -- applies to .vtk POINTS
            // and .csv X/Y/Z alike (see VtkFrameReader.WorldUp).
            _volumeStaticWorldUp = (WorldUpAxis)GUILayout.SelectionGrid((int)_volumeStaticWorldUp, WorldUpLabels, WorldUpLabels.Length);

            GUI.enabled = !_volumeStaticLoading;
            if (GUILayout.Button("Create"))
            {
                if (CFDFactory.Instance == null)
                {
                    Debug.LogError("[TestUI] No CFDFactory in scene.");
                }
                // VtkFrameReader.FilePath is a single .vtk or .csv file, not
                // a directory -- hence isDirectory: false here.
                else if (!TryResolvePath(_volumeStaticDataPath, _volumeStaticPathMode, isDirectory: false,
                             out string resolvedPath, out _volumeStaticPathError))
                {
                    Debug.LogWarning($"[TestUI] {_volumeStaticPathError}");
                }
                else
                {
                    DestroyIfExists(_volumeStatic);
                    _volumeStaticLoading = true;
                    _volumeStatic = CFDFactory.Instance.CreateVolumeStatic(
                        resolvedPath, VolumeStaticPosition, Quaternion.Euler(VolumeStaticEulerRotation),
                        _ => _volumeStaticLoading = false, _volumeStaticWorldUp);
                    _volumeStaticVisible = true;
                    // Read the prefab-configured material's actual current values
                    // rather than assuming the slider defaults (0/1) match them.
                    _volumeStaticClipMin = _volumeStatic.GetMaterialFloat("_ClipMin");
                    _volumeStaticClipMax = _volumeStatic.GetMaterialFloat("_ClipMax");
                }
            }
            GUI.enabled = true;

            if (!string.IsNullOrEmpty(_volumeStaticPathError))
                GUILayout.Label(_volumeStaticPathError, ErrorStyle());

            DrawVisibilityToggle(_volumeStatic, ref _volumeStaticVisible, _volumeStaticLoading);
            DrawClipRangeControls(_volumeStatic, "_ClipMin", "_ClipMax", ref _volumeStaticClipMin, ref _volumeStaticClipMax, 100f, "°C");
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
