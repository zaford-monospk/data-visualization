using UnityEngine;

namespace Monospark
{
    // Animates a VtkFrameSequenceData: each time playback advances to a new
    // source frame (paced by VtkFrameSequenceData.Fps, independent of render
    // framerate), rebuilds a persistent Texture3D in place (RGBAHalf:
    // .r = normalized temperature, .a = voxel occupancy — same convention as
    // VtkUnstructuredGridReader.ToTexture3D) and pushes it onto Material's
    // _Volume property. SetInterpolation switches on a second Texture3D
    // (_VolumeNext) plus a per-render-frame _FrameBlend factor, for smooth
    // playback via VolumeRenderer_Interpolate.shader specifically — off by
    // default (cheap: one texture, rebuilt only ~Fps times/sec), since the
    // extra texture only helps when the assigned shader actually reads it.
    // Material is instanced per-component in Awake (see _materialInstance),
    // not used directly -- otherwise every player sharing the same prefab's
    // Material asset would stomp on each other's texture/clip range/shader.
    public class VtkFrameSequencePlayer : MonoBehaviour , IRenderStateControl
    {
        static readonly int VolumeId = Shader.PropertyToID("_Volume");
        static readonly int VolumeNextId = Shader.PropertyToID("_VolumeNext");
        static readonly int FrameBlendId = Shader.PropertyToID("_FrameBlend");

        public Material Material;
        public Transform TargetCube; // optional: scaled to match voxel dims, as TestAction does for the static case
        public bool Loop = true;
        public float PlaybackSpeed = 1f;

        VtkFrameSequenceData _sequence;
        Texture3D _textureCurrent;
        Texture3D _textureNext; // only allocated/maintained while interpolation is on
        Color[] _colorBuffer;
        float _elapsed;
        int _currentFrame = -1;
        bool _interpolate;

        // Tracked separately from the public Material field so OnDestroy only
        // ever destroys the instance THIS component created -- not whatever a
        // caller might reassign the field to later.
        Material _materialInstance;

        void Awake()
        {
            // Per-instance copy, not the shared prefab/scene Material asset
            // directly -- see the class doc comment.
            if (Material != null)
            {
                _materialInstance = new Material(Material);
                Material = _materialInstance;
            }
        }

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
        // vs _BL vs _v2 vs _Interpolate). Property values already set on Material
        // (like _Volume, which every variant shares the same name for) carry over
        // automatically — Unity keeps a Material's property sheet independent of
        // its active shader.
        public void SetShader(Shader shader)
        {
            if (Material != null && shader != null)
                Material.shader = shader;
        }

        public Shader GetShader()
        {
            return Material != null ? Material.shader : null;
        }

        // Turning this on builds/maintains a second Texture3D (the next source
        // frame) and a per-render-frame blend factor, for shaders that lerp
        // between them (VolumeRenderer_Interpolate.shader). Turning it off
        // resets the blend factor to 0 so an interpolate-capable shader falls
        // back to showing _textureCurrent alone rather than a frozen stale mix.
        public void SetInterpolation(bool enabled)
        {
            _interpolate = enabled;

            if (!enabled)
            {
                if (Material != null)
                    Material.SetFloat(FrameBlendId, 0f);
                return;
            }

            if (_sequence == null || _sequence.Frames.Length == 0)
                return;

            UploadNextFrame(); // populate _VolumeNext immediately rather than waiting for the next frame tick
        }

        public void Set(VtkFrameSequenceData sequence)
        {
            _sequence = sequence;
            _elapsed = 0f;
            _currentFrame = -1;

            if (_textureNext != null)
            {
                Destroy(_textureNext);
                _textureNext = null;
            }

            Vector3Int dims = sequence.Dims;
            _textureCurrent = CreateTexture(dims);
            _colorBuffer = new Color[dims.x * dims.y * dims.z];

            if (Material != null)
                Material.SetTexture(VolumeId, _textureCurrent);
            if (TargetCube != null)
                TargetCube.localScale = new Vector3(dims.x, dims.y, dims.z) * _sequence.VoxelSize;

            if (sequence.Frames.Length > 0)
                ShowFrame(0);
        }

        static Texture3D CreateTexture(Vector3Int dims)
        {
            return new Texture3D(dims.x, dims.y, dims.z, TextureFormat.RGBAHalf, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
        }

        void EnsureNextTexture()
        {
            if (_textureNext != null || _sequence == null)
                return;

            _textureNext = CreateTexture(_sequence.Dims);
            if (Material != null)
                Material.SetTexture(VolumeNextId, _textureNext);
        }

        void Update()
        {
            if (_sequence == null || _sequence.Frames.Length == 0)
                return;

            _elapsed += Time.deltaTime * PlaybackSpeed;

            int frameCount = _sequence.Frames.Length;
            float t = _elapsed * _sequence.Fps;
            int frame = Mathf.FloorToInt(t);

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

            // Fractional progress between _currentFrame and its successor —
            // updated every render frame, unlike the textures themselves, so
            // playback interpolates smoothly between the ~1/Fps-spaced source
            // frames instead of jump-cutting at each frame boundary.
            if (_interpolate && Material != null)
                Material.SetFloat(FrameBlendId, Mathf.Repeat(t, 1f));
        }

        void ShowFrame(int frameIndex)
        {
            _currentFrame = frameIndex;
            UploadFrame(_sequence.Frames[frameIndex], _textureCurrent);

            if (_interpolate)
                UploadNextFrame();
        }

        void UploadNextFrame()
        {
            EnsureNextTexture();

            int frameCount = _sequence.Frames.Length;
            int nextIndex = Loop
                ? (_currentFrame + 1) % frameCount
                : Mathf.Min(_currentFrame + 1, frameCount - 1);

            UploadFrame(_sequence.Frames[nextIndex], _textureNext);
        }

        void UploadFrame(VoxelTemperatureFrame frame, Texture3D texture)
        {
            for (int i = 0; i < _colorBuffer.Length; i++)
            {
                _colorBuffer[i] = frame.TryGetTemperature01(i, out float normalized)
                    ? new Color(normalized, normalized, normalized, 1f)
                    : Color.clear;
            }

            texture.SetPixels(_colorBuffer);
            texture.Apply();
        }

        void OnDestroy()
        {
            if (_textureCurrent != null)
                Destroy(_textureCurrent);
            if (_textureNext != null)
                Destroy(_textureNext);
            // Instances created via `new Material(...)` in Awake aren't
            // scene/project assets -- Unity won't reclaim them on its own
            // when this GameObject is destroyed, so this would otherwise
            // leak one Material per instance for the rest of the session.
            if (_materialInstance != null)
                Destroy(_materialInstance);
        }
    }
}