Shader "Custom/VolumeRenderer_Interpolate"
{
    Properties
    {
        [MainTexture] _Volume("Volume", 3D) = "white" {}
        _TemperatureLUT("Temp LUT", 2D) = "white" {}
        _DensityMultiplier("Density Multiplier", Range(0, 10)) = 1
        _ExtinctionScale("Extinction Scale", Range(0, 500)) = 100
        _StepCount("Step Count", Range(8, 256)) = 64
        _AlphaCutoff("Alpha Cutoff", Range(0, 1)) = 0.99
        _ClipMin("Clip Range Min", Range(0, 1)) = 0
        _ClipMax("Clip Range Max", Range(0, 1)) = 1
        _ClipSoftness("Clip Softness", Range(0.0001, 0.1)) = 0.01
    }

    SubShader
    {
        // Applied to a unit Cube (Unity's built-in Cube mesh spans -0.5..0.5
        // per axis in object space) — that box is what _Volume is raymarched
        // through, object-space position doubling as the 0..1 texture UV.
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Tags { "LightMode" = "UniversalForward" }

            // Cull Front (not Back): we rasterize the box's far side so the
            // fragment position is always the ray's exit point, which keeps
            // this correct even when the camera is inside the cube.
            Cull Front
            ZWrite Off
            // Always (not the default LEqual): with no ZTest, a single opaque
            // object embedded inside the volume (e.g. a rack) would fail the
            // depth test against the box's back face and blank out the whole
            // ray at that pixel, not just the portion behind it. This makes
            // the volume draw over everything in its screen footprint instead.
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE3D(_Volume);
            SAMPLER(sampler_Volume);
            TEXTURE2D(_TemperatureLUT);
            SAMPLER(sampler_TemperatureLUT);

            CBUFFER_START(UnityPerMaterial)
                float _DensityMultiplier;
                float _ExtinctionScale;
                float _StepCount;
                float _AlphaCutoff;
                float _ClipMin;
                float _ClipMax;
                float _ClipSoftness;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionOS : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionOS = IN.positionOS.xyz;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            // Slab ray/box test against the unit cube [-0.5, 0.5]^3 the volume
            // is mapped onto. Returns (distance to box, distance through box).
            // Caller must pass a safe (non-zero-component) inverse ray direction.
            float2 RayBoxDst(float3 rayOriginOS, float3 invRayDirOS)
            {
                float3 t0 = (-0.5 - rayOriginOS) * invRayDirOS;
                float3 t1 = (0.5 - rayOriginOS) * invRayDirOS;
                float3 tMin = min(t0, t1);
                float3 tMax = max(t0, t1);

                float dstA = max(max(tMin.x, tMin.y), tMin.z);
                float dstB = min(min(tMax.x, tMax.y), tMax.z);

                float dstToBox = max(dstA, 0);
                float dstInsideBox = max(dstB - dstToBox, 0);
                return float2(dstToBox, dstInsideBox);
            }

            float Hash13(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 rayOriginOS = TransformWorldToObject(_WorldSpaceCameraPos).xyz;
                float3 rayDirOS = normalize(IN.positionOS - rayOriginOS);

                // 1.0 / rayDirOS directly would divide by zero whenever a
                // component is exactly 0 (a ray perfectly aligned with an
                // axis plane) — nudge only those components to a tiny epsilon
                // of the correct sign, without relying on sign(0) (which is 0,
                // not ±1, and would silently zero the epsilon back out).
                float3 safeRayDir;
                safeRayDir.x = abs(rayDirOS.x) < 1e-6
                    ? (rayDirOS.x < 0.0 ? -1e-6 : 1e-6)
                    : rayDirOS.x;
                safeRayDir.y = abs(rayDirOS.y) < 1e-6
                    ? (rayDirOS.y < 0.0 ? -1e-6 : 1e-6)
                    : rayDirOS.y;
                safeRayDir.z = abs(rayDirOS.z) < 1e-6
                    ? (rayDirOS.z < 0.0 ? -1e-6 : 1e-6)
                    : rayDirOS.z;
                float3 invRayDirOS = 1.0 / safeRayDir;

                float2 boxDst = RayBoxDst(rayOriginOS, invRayDirOS);
                float dstToBox = boxDst.x;
                float dstInsideBox = boxDst.y;

                // Ray doesn't pass through the box at all — nothing to march.
                if (dstInsideBox <= 0.0)
                    return 0;

                // Stop the march at whatever opaque scene geometry already sits
                // in the depth buffer at this pixel — this is what lets an
                // opaque object composite correctly with the volume in both
                // directions: something in front of the whole box shrinks
                // dstInsideBox to ~0 (so it reads as occluded, even under
                // ZTest Always above), and something embedded inside the box
                // (e.g. a rack) still lets the volume render up to it instead
                // of the whole ray being hidden or painted over.
                // GetNormalizedScreenSpaceUV (not IN.positionHCS.xy / _ScreenParams)
                // accounts for URP render scaling; a raw _ScreenParams divide can
                // sample the wrong texel when Render Scale != 1.
                float2 screenUV = GetNormalizedScreenSpaceUV(IN.positionHCS);
                float rawDepth = SampleSceneDepth(screenUV);

                // The depth buffer's "nothing drawn here" clear value depends on
                // UNITY_REVERSED_Z (near=1/far=0 vs near=0/far=1) — only attempt
                // the clip when there's an actual opaque surface, not skybox/empty.
                bool hasOpaqueSurface;
                #if UNITY_REVERSED_Z
                    hasOpaqueSurface = rawDepth > 0.00001;
                #else
                    hasOpaqueSurface = rawDepth < 0.99999;
                #endif

                if (hasOpaqueSurface)
                {
                    // Non-reversed-Z (OpenGL-like) platforms sample depth in a
                    // 0..1 range that still needs mapping to clip-space depth.
                    #if !UNITY_REVERSED_Z
                        rawDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, rawDepth);
                    #endif

                    float3 scenePosWS = ComputeWorldSpacePosition(screenUV, rawDepth, UNITY_MATRIX_I_VP);

                    // Transform the opaque surface's POSITION into object space
                    // and project it onto the ray via dot(), instead of computing
                    // a world-space distance and converting it with a scale
                    // factor — length(TransformObjectToWorldDir(v)) is USELESS
                    // for this: that function normalizes its result by default,
                    // so it always returns ~1 regardless of the cube's actual
                    // scale. Working entirely in object space sidesteps any
                    // world/object unit conversion, correct under any scale.
                    float3 scenePosOS = TransformWorldToObject(scenePosWS);
                    float sceneDstOS = dot(scenePosOS - rayOriginOS, rayDirOS);

                    dstInsideBox = min(dstInsideBox, max(sceneDstOS - dstToBox, 0.0));
                }

                // Opaque geometry sits at or before the volume's entry — nothing to march.
                if (dstInsideBox <= 0.0)
                    return 0;

                int steps = max(1, (int)_StepCount);
                float stepSize = dstInsideBox / steps;

                // Jitter the ray start per-pixel (Hash13, 0..1) instead of a
                // fixed step-center offset — averages to the same position
                // (mean 0.5 * stepSize) but turns step-boundary banding
                // ("sliced" look) into noise instead of visible bands/shells.
                float jitter = Hash13(IN.positionHCS.xyz) * stepSize;
                float3 samplePos = rayOriginOS + rayDirOS * (dstToBox + jitter);

                float3 accumulatedColor = 0;
                float accumulatedAlpha = 0;

                for (int i = 0; i < steps && accumulatedAlpha < _AlphaCutoff; i++)
                {
                    float3 uv = samplePos + 0.5;

                    // Outside the texture's 0..1 UV range: nothing to sample here.
                    if (any(uv < 0.0) || any(uv > 1.0))
                    {
                        samplePos += rayDirOS * stepSize;
                        continue;
                    }

                    // Explicit LOD (not SAMPLE_TEXTURE3D): implicit-gradient sampling
                    // needs uniform control flow across a pixel quad, which a
                    // data-dependent raymarch loop can't provide.
                    // r = normalized scalar value, a = voxel occupancy/validity.
                    float2 volumeSample = SAMPLE_TEXTURE3D_LOD(_Volume, sampler_Volume, uv, 0).ra;
                    float scalar = volumeSample.x;
                    float occupancy = volumeSample.y;

                    // Soft value-range filter: instead of a hard step() cutoff,
                    // smoothstep ramps rangeMask to 0 over _ClipSoftness on
                    // either side of [_ClipMin, _ClipMax], so samples near the
                    // boundary fade out rather than popping off abruptly.
                    float minMask = smoothstep(_ClipMin - _ClipSoftness, _ClipMin + _ClipSoftness, scalar);
                    float maxMask = 1.0 - smoothstep(_ClipMax - _ClipSoftness, _ClipMax + _ClipSoftness, scalar);
                    float rangeMask = minMask * maxMask;

                    // The scalar itself only drives color (via the LUT) and the
                    // range mask — it does NOT multiply into extinction, so
                    // density reflects "is there valid, in-range data here"
                    // rather than being brighter/thicker for higher scalar values.
                    float extinction = occupancy * rangeMask * _DensityMultiplier;

                    // u = normalized scalar value, v = 0.5 (LUT used as a 1D ramp).
                    float3 sampleColor = SAMPLE_TEXTURE2D_LOD(
                        _TemperatureLUT, sampler_TemperatureLUT, float2(scalar, 0.5), 0).rgb;

                    // Absorption-based alpha (Beer-Lambert per step) instead of
                    // a direct density-to-alpha mapping: opacity now converges
                    // with distance travelled through the volume rather than
                    // with step count, so changing _StepCount changes quality,
                    // not how opaque the volume looks.
                    float rawAlpha = 1.0 - exp(-extinction * stepSize * _ExtinctionScale);
                    float contribution = rawAlpha * (1.0 - accumulatedAlpha);

                    accumulatedColor += sampleColor * contribution;
                    accumulatedAlpha += contribution;

                    samplePos += rayDirOS * stepSize;
                }

                return half4(accumulatedColor, accumulatedAlpha);
            }
            ENDHLSL
        }
    }
}