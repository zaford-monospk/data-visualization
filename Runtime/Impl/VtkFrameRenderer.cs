using UnityEngine;

namespace Monospark
{
    // Displays a single static Texture3D as a raymarched volume (VolumeRenderer.shader)
    // -- the non-animated counterpart to VtkFrameSequencePlayer: no Update()
    // loop, no frame stepping, no interpolation state, just Material._Volume +
    // TargetCube scaled to the texture's dims, same as TestAction's static case.
    public class VtkFrameRenderer : MonoBehaviour, IRenderStateControl
    {
        static readonly int VolumeId = Shader.PropertyToID("_Volume");

#if UNITY_WEBGL
        // VolumeRenderer.shader's scene-depth occlusion clip reads back
        // unreliable depth on (at least some) WebGL2/ANGLE GPUs, which made
        // the volume render as if hidden behind every opaque object on screen
        // instead of in front of it -- see VolumeRenderer_WebGL.shader's
        // header comment. #if UNITY_WEBGL (not a runtime Application.platform
        // check) so this also swaps while testing in the Editor with WebGL as
        // the active Build Target, not only in an actual exported build.
        const string WebGLShaderName = "Custom/VolumeRenderer_WebGL";
#endif

        public Material Material;
        public Transform TargetCube; // optional: scaled to worldSize, or texture dims * FallbackScale if worldSize is unknown

        // Multiplies the texture's raw voxel-grid resolution (e.g. 64x64x64)
        // when Set() isn't given a real-world size -- using the voxel count
        // directly as world units is wildly oversized, this default at least
        // lands in a reasonable ballpark.
        public float FallbackScale = 0.1f;

        Texture3D _texture;

        void Awake()
        {
#if UNITY_WEBGL
            if (Material == null)
                return;

            Shader webglShader = Shader.Find(WebGLShaderName);
            if (webglShader == null)
            {
                Debug.LogWarning($"[VtkFrameRenderer] Shader '{WebGLShaderName}' not found -- staying on " +
                                  $"'{Material.shader.name}'. Make sure VolumeRenderer_WebGL.shader is included in the build.");
                return;
            }

            SetShader(webglShader);
#endif
        }

        // TargetCube is what's actually drawn (a normal MeshRenderer), so
        // visibility toggles its Renderer directly.
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
        // vs _BL). Property values already set on Material (like _Volume, which
        // every variant shares the same name for) carry over automatically --
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

        // No-op: there's only ever one texture here, so there's no second
        // frame to interpolate toward (that's VolumeRenderer_Interpolate.shader
        // + VtkFrameSequencePlayer's job). Implemented only to satisfy
        // IRenderStateControl, so TestUI-style callers can still drive this
        // renderer purely through the interface without a type check.
        public void SetInterpolation(bool enabled)
        {
        }

        // texture is already fully built (VtkFrameReader.BuildData(OnProcessTex3DData)
        // decodes straight to a Texture3D) -- this just displays it, no
        // per-frame rebuilding the way VtkFrameSequencePlayer.ShowFrame does.
        // Destroys whatever texture this renderer was previously showing
        // (its own, not one still owned by someone else) so repeated Set()
        // calls don't leak a Texture3D per call.
        // worldSize is the source data's real-world extent (VtkFrameReader.DataSize,
        // when its source file had usable bounds) -- pass null/omit when
        // that isn't known, which falls back to the texture's own voxel-grid
        // resolution scaled down by FallbackScale.
        public void Set(Texture3D texture, Vector3? worldSize = null)
        {
            if (_texture != null && _texture != texture)
                Destroy(_texture);

            _texture = texture;

            if (Material != null)
                Material.SetTexture(VolumeId, _texture);
            if (TargetCube != null && _texture != null)
            {
                TargetCube.localScale = worldSize ??
                    new Vector3(_texture.width, _texture.height, _texture.depth) * FallbackScale;
            }
        }

        void OnDestroy()
        {
            if (_texture != null)
                Destroy(_texture);
        }
    }
}
