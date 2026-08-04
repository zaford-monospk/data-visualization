using UnityEngine;

namespace Monospark
{
    // Separate OnGUI overlay from TestUI's panel (bottom-right corner, its own
    // component) so it stays visible regardless of whether that panel is open
    // or collapsed. Averages over UpdateInterval rather than showing a raw
    // per-frame instantaneous value, which is too jittery to read.
    public class FpsCounter : MonoBehaviour
    {
        public float UpdateInterval = 0.5f;

        float _accumulatedTime;
        int _frameCount;
        float _currentFps;

        static GUIStyle _style;

        void Update()
        {
            // Unscaled: stays meaningful even if Time.timeScale changes (paused/slow-mo).
            _accumulatedTime += Time.unscaledDeltaTime;
            _frameCount++;

            if (_accumulatedTime >= UpdateInterval)
            {
                _currentFps = _frameCount / _accumulatedTime;
                _accumulatedTime = 0f;
                _frameCount = 0;
            }
        }

        void OnGUI()
        {
            const float width = 90f;
            const float height = 24f;
            var rect = new Rect(Screen.width - width - 10f, Screen.height - height - 10f, width, height);

            GUI.Label(rect, $"{_currentFps:F1} FPS", Style());
        }

        static GUIStyle Style()
        {
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 14,
                    normal = { textColor = Color.white }
                };
            }
            return _style;
        }
    }
}