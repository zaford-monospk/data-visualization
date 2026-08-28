using UnityEngine;

namespace Monospark
{
    // Displays a single static Texture3D as a raymarched volume (VolumeRenderer.shader)
    // -- the non-animated counterpart to VtkFrameSequencePlayer: no Update()
    // loop, no frame stepping, no interpolation state, just Material._Volume +
    // TargetCube scaled to the texture's dims, same as TestAction's static case.
    // Optionally also drives TargetPlane (VolumeSlicePlane.shader) as an
    // independent cross-section view of the same Texture3D -- free to be
    // positioned/rotated anywhere by the caller, kept in sync every frame via
    // SliceMaterial's _VolumeWorldToLocal (see LateUpdate). TargetCube/Material
    // and TargetPlane/SliceMaterial are both optional and independent: either,
    // both, or neither can be visible at once. Material/SliceMaterial are
    // instanced per-component in Awake (see _materialInstance/_sliceMaterialInstance)
    // rather than used directly -- otherwise every renderer sharing the same
    // prefab's Material asset would stomp on each other's texture/clip
    // range/shader.
    public class VtkFrameRenderer : MonoBehaviour, IRenderStateControl
    {
        static readonly int VolumeId = Shader.PropertyToID("_Volume");
        static readonly int VolumeWorldToLocalId = Shader.PropertyToID("_VolumeWorldToLocal");
        static readonly int LutStartTemperatureId = Shader.PropertyToID("_LutStartTemperature");
        static readonly int LutEndTemperatureId = Shader.PropertyToID("_LutEndTemperature");

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

        // Optional cross-section slicer: a flat plane (any mesh/transform,
        // not required to be parented under or aligned with TargetCube) that
        // shows a single slice of the same Texture3D via VolumeSlicePlane.shader.
        // SliceMaterial is a separate Material from Material/TargetCube's, not
        // a shader swap on the same one -- both can be visible/tuned at once
        // (e.g. a raymarched cube plus a cutaway slice through it).
        public Transform TargetPlane;
        public Material SliceMaterial;

        // Multiplies the texture's raw voxel-grid resolution (e.g. 64x64x64)
        // when Set() isn't given a real-world size -- using the voxel count
        // directly as world units is wildly oversized, this default at least
        // lands in a reasonable ballpark.
        public float FallbackScale = 0.1f;

        // Floor applied to each axis of TargetCube.localScale in Set() -- a
        // source that's inherently planar (e.g. a CFD "X1"/"X2" plane-cut CSV
        // export, where every row shares the same X) reports a worldSize of
        // ~0 along that axis. A truly (near-)zero-scale Transform makes
        // TargetCube.worldToLocalMatrix singular, which breaks TargetPlane's
        // _VolumeWorldToLocal reprojection (NaN/Inf math -> nothing renders
        // on the slice plane) even though the raymarched cube itself still
        // looks fine (just an imperceptibly thin box) -- so this needs
        // clamping regardless of whether TargetPlane is even in use.
        public float MinAxisSize = 0.05f;

        Texture3D _texture;

        // Raw (Celsius) value range the currently-displayed Texture3D was
        // normalized against -- set by Set() (from VtkFrameReader.ValueMin/
        // ValueMax) so SetLutTemperatureRange can convert a real Celsius
        // value into the shader's normalized 0..1 _LutStartTemperature/
        // _LutEndTemperature space. _valueMax defaults to 1 (not 0) so the
        // range is never zero-width before Set() has run even once.
        float _valueMin;
        float _valueMax = 1f;

        // Tracked separately from the public Material/SliceMaterial fields so
        // OnDestroy only ever destroys the instance THIS component created --
        // not whatever a caller might reassign those fields to later.
        Material _materialInstance;
        Material _sliceMaterialInstance;

        void Awake()
        {
            // Per-instance copies, not the shared prefab/scene Material asset
            // directly: Material.SetTexture/SetFloat/shader= below all mutate
            // whatever Material this field points at, so two GameObjects
            // referencing the same Material asset (e.g. two instances of the
            // same prefab) would otherwise stomp on each other's texture,
            // clip range, and shader every time either one calls Set()/
            // SetMaterialFloat()/SetShader(). Cloning the field alone isn't
            // enough, though -- TargetCube/TargetPlane's MeshRenderer was
            // wired to the ORIGINAL asset in the prefab/Inspector, so it has
            // to be re-pointed at the clone explicitly, or the mesh keeps
            // showing the untouched original while every SetTexture/SetFloat
            // call lands on a clone nothing is actually rendering with.
            if (Material != null)
            {
                _materialInstance = new Material(Material);
                Material = _materialInstance;
                if (TargetCube != null && TargetCube.TryGetComponent<Renderer>(out var cubeRenderer))
                    cubeRenderer.sharedMaterial = _materialInstance;
            }
            if (SliceMaterial != null)
            {
                _sliceMaterialInstance = new Material(SliceMaterial);
                SliceMaterial = _sliceMaterialInstance;
                if (TargetPlane != null && TargetPlane.TryGetComponent<Renderer>(out var planeRenderer))
                    planeRenderer.sharedMaterial = _sliceMaterialInstance;
            }

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

        // TargetPlane and TargetCube can move independently (the plane is a
        // free-form slicer, not parented under the cube), so the matrix that
        // reprojects the plane's fragments into the volume's local space has
        // to be refreshed every frame rather than once. LateUpdate (not
        // Update) so this reads TargetCube's transform after anything else
        // that might move it this frame has already run.
        void LateUpdate()
        {
            if (SliceMaterial != null && TargetCube != null)
                SliceMaterial.SetMatrix(VolumeWorldToLocalId, TargetCube.worldToLocalMatrix);
        }

        // TargetCube is what's actually drawn (a normal MeshRenderer), so
        // visibility toggles its Renderer directly.
        public void SetVisibility(bool isVisible)
        {
            if (TargetCube != null && TargetCube.TryGetComponent<Renderer>(out var cubeRenderer))
                cubeRenderer.enabled = isVisible;
        }

        // Independent of SetVisibility/TargetCube -- TargetPlane is an
        // optional, separately-toggleable view of the same data, not part of
        // IRenderStateControl's single visibility flag.
        public void SetSliceVisibility(bool isVisible)
        {
            if (TargetPlane != null && TargetPlane.TryGetComponent<Renderer>(out var planeRenderer))
                planeRenderer.enabled = isVisible;
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
        // resolution scaled down by FallbackScale. valueRange is the raw
        // (Celsius) min/max the source's Texture3D was normalized against
        // (VtkFrameReader.ValueMin/ValueMax) -- required for
        // SetLutTemperatureRange's Celsius -> normalized conversion to be
        // correct; pass null/omit only if that method will never be called
        // on this instance.
        public void Set(Texture3D texture, Vector3? worldSize = null, (float min, float max)? valueRange = null)
        {
            if (_texture != null && _texture != texture)
                Destroy(_texture);

            _texture = texture;

            if (valueRange.HasValue)
            {
                _valueMin = valueRange.Value.min;
                _valueMax = valueRange.Value.max;
            }

            if (Material != null)
                Material.SetTexture(VolumeId, _texture);
            if (SliceMaterial != null)
                SliceMaterial.SetTexture(VolumeId, _texture);
            if (TargetCube != null && _texture != null)
            {
                Vector3 size = worldSize ??
                    new Vector3(_texture.width, _texture.height, _texture.depth) * FallbackScale;

                // See MinAxisSize's doc comment -- a plane-cut source reports
                // ~0 on one axis, which would otherwise make this Transform's
                // scale (and therefore worldToLocalMatrix) singular.
                size.x = Mathf.Max(size.x, MinAxisSize);
                size.y = Mathf.Max(size.y, MinAxisSize);
                size.z = Mathf.Max(size.z, MinAxisSize);

                TargetCube.localScale = size;
            }
        }

        // Sets the shader's _LutStartTemperature/_LutEndTemperature (on both
        // Material and SliceMaterial, so the raymarched cube and the slice
        // plane stay in sync) from real Celsius values instead of the
        // shader's raw normalized 0..1 space -- converted using the value
        // range Set() was given (VtkFrameReader.ValueMin/ValueMax). E.g.
        // SetLutTemperatureRange(18f, 26f) makes 18°C map to the LUT's start
        // and 26°C to its end, regardless of the data's actual full range.
        public void SetLutTemperatureRange(float startCelsius, float endCelsius)
        {
            float span = Mathf.Max(_valueMax - _valueMin, Mathf.Epsilon);
            float start01 = Mathf.Clamp01((startCelsius - _valueMin) / span);
            float end01 = Mathf.Clamp01((endCelsius - _valueMin) / span);

            if (Material != null)
            {
                Material.SetFloat(LutStartTemperatureId, start01);
                Material.SetFloat(LutEndTemperatureId, end01);
            }
            if (SliceMaterial != null)
            {
                SliceMaterial.SetFloat(LutStartTemperatureId, start01);
                SliceMaterial.SetFloat(LutEndTemperatureId, end01);
            }
        }

        void OnDestroy()
        {
            if (_texture != null)
                Destroy(_texture);
            // Instances created via `new Material(...)` in Awake aren't
            // scene/project assets -- Unity won't reclaim them on its own
            // when this GameObject is destroyed, so this would otherwise
            // leak one Material per instance for the rest of the session.
            if (_materialInstance != null)
                Destroy(_materialInstance);
            if (_sliceMaterialInstance != null)
                Destroy(_sliceMaterialInstance);
        }
    }
}
