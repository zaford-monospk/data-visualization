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

        // Order matches eDataType's declaration order exactly (SelectionGrid
        // casts its int index straight to/from the enum).
        static readonly string[] DataTypeLabels = { "Temperature", "Velocity", "PMV", "RH" };

        // Sensible starting LUT calibration range per data type, applied
        // when the selector below changes -- otherwise switching types
        // would leave whatever range the previous type had (e.g.
        // Temperature's [0, 100]) which makes no sense for, say, PMV
        // (roughly -3..3). Purely a starting point for the LUT Start/End
        // text fields -- freely editable afterward, same as any other field
        // here.
        static (float min, float max) DefaultLutRange(eDataType dataType)
        {
            switch (dataType)
            {
                case eDataType.Velocity: return (0f, 2f);   // m/s -- typical indoor airflow; raise for a vent/exhaust-heavy CSV
                case eDataType.PMV: return (-3f, 3f);       // ASHRAE PMV's own fixed scale
                case eDataType.RH: return (0f, 100f);       // %
                default: return (0f, 100f);                 // Temperature, °C
            }
        }

        // Cosmetic unit suffix for the clip-range labels below, matching
        // each type's natural unit -- the actual numbers come from
        // rangeMin/rangeMax passed into DrawClipRangeControls, this just
        // labels them correctly.
        static string DataTypeUnit(eDataType dataType)
        {
            switch (dataType)
            {
                case eDataType.Velocity: return " m/s";
                case eDataType.PMV: return "";
                case eDataType.RH: return "%";
                default: return "°C";
            }
        }

        [Header("Volume Static (raymarch, single frame)")]
        public Vector3 VolumeStaticPosition;
        public Vector3 VolumeStaticEulerRotation;

        [Header("Volume Slice 2D (CSV, direct)")]
        public Vector3 VolumeSlice2DPosition;
        public Vector3 VolumeSlice2DEulerRotation;

        // Not Inspector-exposed: edited live via the GUI's own text field, so
        // a stale/wrong Inspector value could never end up silently in use —
        // the field always reflects exactly what's on screen.
        string _volumeStaticDataPath = "";
        string _volumeSlice2DDataPath = "";
        PathMode _volumeStaticPathMode;
        PathMode _volumeSlice2DPathMode;

        // .vtk POINTS / .csv X-Y-Z may be authored Z-up (common for CAD/CFD
        // tooling) even though Unity is Y-up -- see VtkFrameReader.WorldUp.
        WorldUpAxis _volumeStaticWorldUp;
        WorldUpAxis _volumeSlice2DWorldUp;

        // Which of a multi-field CFD CSV export's columns to voxelize (see
        // eDataType/InfoTypes) -- forwarded to CFDFactory as an InfoTypes at
        // Create. LutStart/EndText are the editable text fields (kept as
        // strings, not floats, so a TextField mid-edit -- e.g. typing "-"
        // before the rest of a negative number -- isn't stomped by
        // reformatting the float back to text every frame); LutStart/End
        // are the last successfully-parsed float, updated below whenever
        // the text parses, and are what's actually sent to CFDFactory and
        // used to scale the clip-range labels.
        eDataType _volumeStaticDataType = eDataType.Temperature;
        eDataType _volumeSlice2DDataType = eDataType.Temperature;
        string _volumeStaticLutStartText = "0";
        string _volumeStaticLutEndText = "100";
        string _volumeSlice2DLutStartText = "0";
        string _volumeSlice2DLutEndText = "100";
        float _volumeStaticLutStart = 0f;
        float _volumeStaticLutEnd = 100f;
        float _volumeSlice2DLutStart = 0f;
        float _volumeSlice2DLutEnd = 100f;

        // Concrete VtkFrameRenderer, not just IRenderStateControl -- both
        // sections now need renderer-specific methods the interface doesn't
        // have (SetOpaque here, plus SetSliceVisibility for the 2D slice
        // below), though DrawVisibilityToggle/DrawClipRangeControls below
        // still take it as an IRenderStateControl, same as before.
        VtkFrameRenderer _volumeStatic;
        VtkFrameRenderer _volumeSlice2D;
        bool _volumeStaticVisible = true;
        bool _volumeSlice2DVisible = true;
        bool _volumeStaticOpaque;
        bool _volumeStaticLoading;
        bool _volumeSlice2DLoading;
        string _volumeStaticPathError;
        string _volumeSlice2DPathError;
        float _volumeStaticClipMin;
        float _volumeStaticClipMax = 1f;
        bool _collapsed;

        const float PanelWidth = 380f;
        const float PanelHeight = 760f;
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
                GUILayout.Space(12);
                DrawVolumeSlice2DSection();
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

            // Which CSV column to voxelize (eDataType) and the fixed
            // calibration range it's normalized against (InfoTypes.LUTStarts/
            // LUTEnds) -- see the field group's own doc comment above.
            // Ignored entirely for a .vtk source (CreateVolumeStatic docs
            // this -- .vtk only ever reads FieldName's single field), so
            // this only matters when Path Mode/the picked file is a CSV.
            eDataType newVolumeStaticDataType = (eDataType)GUILayout.SelectionGrid((int)_volumeStaticDataType, DataTypeLabels, DataTypeLabels.Length);
            if (newVolumeStaticDataType != _volumeStaticDataType)
            {
                _volumeStaticDataType = newVolumeStaticDataType;
                (float defaultMin, float defaultMax) = DefaultLutRange(_volumeStaticDataType);
                _volumeStaticLutStartText = defaultMin.ToString();
                _volumeStaticLutEndText = defaultMax.ToString();
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("LUT", GUILayout.Width(30));
            _volumeStaticLutStartText = GUILayout.TextField(_volumeStaticLutStartText);
            GUILayout.Label("to", GUILayout.Width(20));
            _volumeStaticLutEndText = GUILayout.TextField(_volumeStaticLutEndText);
            GUILayout.EndHorizontal();
            if (float.TryParse(_volumeStaticLutStartText, out float parsedStaticLutStart))
                _volumeStaticLutStart = parsedStaticLutStart;
            if (float.TryParse(_volumeStaticLutEndText, out float parsedStaticLutEnd))
                _volumeStaticLutEnd = parsedStaticLutEnd;

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
                    var info = new InfoTypes { DataType = _volumeStaticDataType, LUTStarts = _volumeStaticLutStart, LUTEnds = _volumeStaticLutEnd };
                    _volumeStatic = CFDFactory.Instance.CreateVolumeStatic(
                        resolvedPath, VolumeStaticPosition, Quaternion.Euler(VolumeStaticEulerRotation),
                        _ => _volumeStaticLoading = false, _volumeStaticWorldUp, opaque: _volumeStaticOpaque, info: info);
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
            DrawClipRangeControls(_volumeStatic, "_ClipMin", "_ClipMax", ref _volumeStaticClipMin, ref _volumeStaticClipMax,
                _volumeStaticLutStart, _volumeStaticLutEnd, DataTypeUnit(_volumeStaticDataType));
            DrawOpaqueToggle(_volumeStatic, ref _volumeStaticOpaque);
        }

        // Through VtkFrameReader/VtkFrameRenderer -- reads a CSV that's
        // ALREADY effectively a single 2D slice (e.g. a CFD "X1"/"X2"
        // plane-cut export, see VtkFrameReader.BuildData(OnProcessTex2DData))
        // and displays it directly on TargetPlane via
        // VtkFrameRenderer.SetSlice2D, skipping the raymarched-cube path
        // (Volume Static, above) entirely -- Create surfaces "not a single
        // 2D slice" the same way as any other failed load. Visibility here
        // uses SetSliceVisibility (TargetPlane), not SetVisibility
        // (TargetCube, untouched by this section) -- so this needs a
        // concrete VtkFrameRenderer reference, not just IRenderStateControl.
        // No opaque toggle here (unlike Volume Static, below) --
        // VolumeSlicePlane.shader has no such property any more, it's
        // always fully opaque wherever it draws at all.
        void DrawVolumeSlice2DSection()
        {
            GUILayout.Label("Volume Slice 2D (CSV, direct)" + (_volumeSlice2DLoading ? " (loading...)" : ""));

            _volumeSlice2DDataPath = GUILayout.TextField(_volumeSlice2DDataPath);
            _volumeSlice2DPathMode = (PathMode)GUILayout.SelectionGrid((int)_volumeSlice2DPathMode, PathModeLabels, PathModeLabels.Length);

            if (_volumeSlice2DPathMode == PathMode.Disk)
                DrawBrowseButton(isFolder: false, "Select CSV File", "csv", path => _volumeSlice2DDataPath = path);

            // Same Z-up-vs-Y-up concern as Volume Static above -- applies to
            // the CSV's X/Y/Z columns (see VtkFrameReader.WorldUp).
            _volumeSlice2DWorldUp = (WorldUpAxis)GUILayout.SelectionGrid((int)_volumeSlice2DWorldUp, WorldUpLabels, WorldUpLabels.Length);

            // Same DataType/LUT range config as Volume Static above -- see
            // its own comment.
            eDataType newVolumeSlice2DDataType = (eDataType)GUILayout.SelectionGrid((int)_volumeSlice2DDataType, DataTypeLabels, DataTypeLabels.Length);
            if (newVolumeSlice2DDataType != _volumeSlice2DDataType)
            {
                _volumeSlice2DDataType = newVolumeSlice2DDataType;
                (float defaultMin, float defaultMax) = DefaultLutRange(_volumeSlice2DDataType);
                _volumeSlice2DLutStartText = defaultMin.ToString();
                _volumeSlice2DLutEndText = defaultMax.ToString();
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("LUT", GUILayout.Width(30));
            _volumeSlice2DLutStartText = GUILayout.TextField(_volumeSlice2DLutStartText);
            GUILayout.Label("to", GUILayout.Width(20));
            _volumeSlice2DLutEndText = GUILayout.TextField(_volumeSlice2DLutEndText);
            GUILayout.EndHorizontal();
            if (float.TryParse(_volumeSlice2DLutStartText, out float parsedSlice2DLutStart))
                _volumeSlice2DLutStart = parsedSlice2DLutStart;
            if (float.TryParse(_volumeSlice2DLutEndText, out float parsedSlice2DLutEnd))
                _volumeSlice2DLutEnd = parsedSlice2DLutEnd;

            GUI.enabled = !_volumeSlice2DLoading;
            if (GUILayout.Button("Create"))
            {
                if (CFDFactory.Instance == null)
                {
                    Debug.LogError("[TestUI] No CFDFactory in scene.");
                }
                // VtkFrameReader.FilePath is a single .csv file, not a
                // directory -- hence isDirectory: false here.
                else if (!TryResolvePath(_volumeSlice2DDataPath, _volumeSlice2DPathMode, isDirectory: false,
                             out string resolvedPath, out _volumeSlice2DPathError))
                {
                    Debug.LogWarning($"[TestUI] {_volumeSlice2DPathError}");
                }
                else
                {
                    DestroyIfExists(_volumeSlice2D);
                    _volumeSlice2DLoading = true;
                    var info = new InfoTypes { DataType = _volumeSlice2DDataType, LUTStarts = _volumeSlice2DLutStart, LUTEnds = _volumeSlice2DLutEnd };
                    _volumeSlice2D = CFDFactory.Instance.CreateSlice2DFromCsv(
                        resolvedPath, VolumeSlice2DPosition, Quaternion.Euler(VolumeSlice2DEulerRotation),
                        _ => _volumeSlice2DLoading = false, _volumeSlice2DWorldUp, info: info);
                    _volumeSlice2DVisible = true;
                }
            }
            GUI.enabled = true;

            if (!string.IsNullOrEmpty(_volumeSlice2DPathError))
                GUILayout.Label(_volumeSlice2DPathError, ErrorStyle());

            GUI.enabled = _volumeSlice2D != null && !_volumeSlice2DLoading;
            if (GUILayout.Button(_volumeSlice2DVisible ? "Hide" : "Show"))
            {
                _volumeSlice2DVisible = !_volumeSlice2DVisible;
                _volumeSlice2D?.SetSliceVisibility(_volumeSlice2DVisible);
            }
            GUI.enabled = true;
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

        // SetOpaque isn't on IRenderStateControl (only VtkFrameRenderer has
        // it), so this takes the concrete type rather than matching
        // DrawVisibilityToggle/DrawClipRangeControls above.
        static void DrawOpaqueToggle(VtkFrameRenderer renderer, ref bool isOpaque)
        {
            GUI.enabled = renderer != null;
            if (GUILayout.Button(isOpaque ? "Opaque: On" : "Opaque: Off"))
            {
                isOpaque = !isOpaque;
                renderer?.SetOpaque(isOpaque);
            }
            GUI.enabled = true;
        }

        // Sliders always carry/send the raw normalized 0..1 value (what the
        // shader actually expects) — rangeMin/rangeMax/unit only affect the
        // label text, lerping the 0..1 slider value into the real-world
        // range the currently-loaded data was calibrated against (see
        // InfoTypes.LUTStarts/LUTEnds) so e.g. PMV's [-3, 3] labels
        // correctly instead of assuming a temperature-shaped [0, 100].
        static void DrawClipRangeControls(
            IRenderStateControl control, string minProperty, string maxProperty, ref float clipMin, ref float clipMax,
            float rangeMin = 0f, float rangeMax = 1f, string unit = "")
        {
            GUI.enabled = control != null;

            GUILayout.Label($"Min: {Mathf.Lerp(rangeMin, rangeMax, clipMin):F1}{unit}");
            float newMin = GUILayout.HorizontalSlider(clipMin, 0f, 1f);
            if (!Mathf.Approximately(newMin, clipMin))
            {
                clipMin = newMin;
                control?.SetMaterialFloat(minProperty, clipMin);
            }

            GUILayout.Label($"Max: {Mathf.Lerp(rangeMin, rangeMax, clipMax):F1}{unit}");
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
