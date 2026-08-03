using UnityEngine;

namespace Monospark
{
    // Animates a VtkFrameSequenceData: each time playback advances to a new
    // source frame (paced by VtkFrameSequenceData.Fps, independent of render
    // framerate), rebuilds a single persistent Texture3D in place (RGBAHalf:
    // .r = normalized temperature, .a = voxel occupancy — same convention as
    // VtkUnstructuredGridReader.ToTexture3D) and pushes it onto Material's
    // _Volume property, so it's compatible as-is with VolumeRenderer.shader.
    public class VtkFrameSequencePlayer : MonoBehaviour , IRenderStateControl
    {
        static readonly int VolumeId = Shader.PropertyToID("_Volume");

        public Material Material;
        public Transform TargetCube; // optional: scaled to match voxel dims, as TestAction does for the static case
        public bool Loop = true;
        public float PlaybackSpeed = 1f;

        VtkFrameSequenceData _sequence;
        Texture3D _texture;
        Color[] _colorBuffer;
        float _elapsed;
        int _currentFrame = -1;

        // TargetCube is what's actually drawn (a normal MeshRenderer), so
        // visibility toggles its Renderer directly — playback keeps running
        // underneath (the texture stays current for whenever it's shown again).
        public void SetVisibility(bool isVisible)
        {
            if (TargetCube != null && TargetCube.TryGetComponent<Renderer>(out var cubeRenderer))
                cubeRenderer.enabled = isVisible;
        }

        // Returns the property's previous value, so callers can restore it later.
        public float SetMaterialFloat(string property, float value)
        {
            if (Material == null)
                return 0f;

            float previous = Material.GetFloat(property);
            Material.SetFloat(property, value);
            return previous;
        }

        public float GetMaterialFloat(string property)
        {
            return Material != null ? Material.GetFloat(property) : 0f;
        }

        // Swaps which shader variant this Material uses (e.g. VolumeRenderer.shader
        // vs _BL vs _v2). Property values already set on Material (like _Volume,
        // which every variant shares the same name for) carry over automatically —
        // Unity keeps a Material's property sheet independent of its active shader.
        public void SetShader(Shader shader)
        {
            if (Material != null && shader != null)
                Material.shader = shader;
        }

        public Shader GetShader()
        {
            return Material != null ? Material.shader : null;
        }

        public void Set(VtkFrameSequenceData sequence)
        {
            _sequence = sequence;
            _elapsed = 0f;
            _currentFrame = -1;

            Vector3Int dims = sequence.Dims;
            _texture = new Texture3D(dims.x, dims.y, dims.z, TextureFormat.RGBAHalf, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            _colorBuffer = new Color[dims.x * dims.y * dims.z];

            if (Material != null)
                Material.SetTexture(VolumeId, _texture);
            if (TargetCube != null)
                TargetCube.localScale = new Vector3(dims.x, dims.y, dims.z)*_sequence.VoxelSize;

            if (sequence.Frames.Length > 0)
                ShowFrame(0);
        }

        void Update()
        {
            if (_sequence == null || _sequence.Frames.Length == 0)
                return;

            _elapsed += Time.deltaTime * PlaybackSpeed;

            int frameCount = _sequence.Frames.Length;
            int frame = Mathf.FloorToInt(_elapsed * _sequence.Fps);

            if (Loop)
            {
                frame %= frameCount;
                if (frame < 0)
                    frame += frameCount; // in case PlaybackSpeed is negative
            }
            else
            {
                frame = Mathf.Clamp(frame, 0, frameCount - 1);
            }

            if (frame != _currentFrame)
                ShowFrame(frame);
        }

        void ShowFrame(int frameIndex)
        {
            _currentFrame = frameIndex;
            VoxelTemperatureFrame frame = _sequence.Frames[frameIndex];

            for (int i = 0; i < _colorBuffer.Length; i++)
            {
                _colorBuffer[i] = frame.TryGetTemperature01(i, out float normalized)
                    ? new Color(normalized, normalized, normalized, 1f)
                    : Color.clear;
            }

            _texture.SetPixels(_colorBuffer);
            _texture.Apply();
        }

        void OnDestroy()
        {
            if (_texture != null)
                Destroy(_texture);
        }
    }
}