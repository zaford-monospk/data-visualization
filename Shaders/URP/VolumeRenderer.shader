Shader "Custom/VolumeRenderer"
{
    Properties
    {
        [MainTexture] _Volume("Volume", 3D) = "white" {}
        _TemperatureLUT("Temp LUT", 2D) = "white" {}
        _DensityMultiplier("Density Multiplier", Range(0, 10)) = 1
        _StepCount("Step Count", Range(8, 256)) = 64
        _AlphaCutoff("Alpha Cutoff", Range(0, 1)) = 0.99
        _ClipMin("Clip Range Min", Range(0, 1)) = 0
        _ClipMax("Clip Range Max", Range(0, 1)) = 1
        // 0 = smooth gradient (the original behavior, so existing materials
        // that predate this property are unaffected). > 0 quantizes the LUT
        // lookup into that many discrete color bands instead.
        _ColorSteps("Color Steps (0 = smooth)", Range(0, 64)) = 0
        // Scales the per-pixel ray-start jitter that turns raymarch step
        // banding into noise instead. 1 = original behavior (full jitter);
        // lower toward 0 for a more solid/filled look at the cost of visible
        // banding if _StepCount is too low to hide it otherwise.
        _JitterStrength("Jitter Strength (0 = filled, banding)", Range(0, 1)) = 1
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
                float _StepCount;
                float _AlphaCutoff;
                float _ClipMin;
                float _ClipMax;
                float _ColorSteps;
                float _JitterStrength;
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

            // Standard interleaved-gradient-noise dither (same trick HDRP's
            // volumetric fog uses): still breaks up raymarch step banding
            // into noise like a plain per-pixel hash would, but the pattern
            // it produces reads as far less "static"/dizzy at the same
            // _StepCount, since it isn't independent white noise.
            float InterleavedGradientNoise(float2 pixelCoord)
            {
                const float3 magic = float3(0.06711056, 0.00583715, 52.9829189);
                return frac(magic.z * frac(dot(pixelCoord, magic.xy)));
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 rayOriginOS = TransformWorldToObject(_WorldSpaceCameraPos).xyz;
                float3 rayDirOS = normalize(IN.positionOS - rayOriginOS);
                float3 invRayDirOS = 1.0 / rayDirOS;

                float2 boxDst = RayBoxDst(rayOriginOS, invRayDirOS);
                float dstToBox = boxDst.x;
                float dstInsideBox = boxDst.y;

                // Stop the march at whatever opaque scene geometry already sits
                // in the depth buffer at this pixel — otherwise an object
                // embedded in the volume (e.g. a rack) would either blank out
                // the whole ray at that pixel (default ZTest) or get painted
                // over entirely (ZTest Always, used here).
                float2 screenUV = GetNormalizedScreenSpaceUV(IN.positionHCS);
                float rawDepth = SampleSceneDepth(screenUV);

                // The depth buffer's "nothing drawn here" clear value depends on
                // UNITY_REVERSED_Z — only attempt the clip when there's an
                // actual opaque surface, not skybox/empty.
                bool hasOpaqueSurface;
                #if UNITY_REVERSED_Z
                    hasOpaqueSurface = rawDepth > 0.00001;
                #else
                    hasOpaqueSurface = rawDepth < 0.99999;
                #endif

                if (hasOpaqueSurface)
                {
                    #if !UNITY_REVERSED_Z
                        rawDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, rawDepth);
                    #endif

                    float3 scenePosWS = ComputeWorldSpacePosition(screenUV, rawDepth, UNITY_MATRIX_I_VP);

                    // Transform the opaque surface's position into object space
                    // and project it onto the ray via dot() — stays entirely in
                    // object-space units, so it's correct under any (including
                    // non-uniform) scale without needing a world/object
                    // conversion factor.
                    float3 scenePosOS = TransformWorldToObject(scenePosWS);
                    float sceneDstOS = dot(scenePosOS - rayOriginOS, rayDirOS);

                    dstInsideBox = min(dstInsideBox, max(sceneDstOS - dstToBox, 0.0));
                }

                if (dstInsideBox <= 0.0)
                    return 0;

                int steps = max(1, (int)_StepCount);
                float stepSize = dstInsideBox / steps;

                // Jitter the ray start per-pixel to turn step-size banding into
                // noise, scaled by _JitterStrength (1 = full, 0 = none -- a
                // fully solid/filled look, trading back in visible banding if
                // _StepCount is too low to hide it otherwise).
                float jitter = InterleavedGradientNoise(IN.positionHCS.xy) * stepSize * _JitterStrength;
                float3 samplePos = rayOriginOS + rayDirOS * (dstToBox + jitter);

                half3 accumulatedColor = 0;
                half accumulatedAlpha = 0;

                for (int i = 0; i < steps && accumulatedAlpha < _AlphaCutoff; i++)
                {
                    float3 uv = samplePos + 0.5;
                    // Explicit LOD (not SAMPLE_TEXTURE3D): implicit-gradient sampling
                    // needs uniform control flow across a pixel quad, which a
                    // data-dependent raymarch loop can't provide.
                    // r = normalized scalar value, a = voxel occupancy (0 where no data landed).
                    half2 volumeSample = SAMPLE_TEXTURE3D_LOD(_Volume, sampler_Volume, uv, 0).ra;

                    // Value-range filter: samples outside [_ClipMin, _ClipMax]
                    // contribute no density, so they're skipped rather than
                    // blended in — isolates a band of the data instead of
                    // discarding the whole ray (which would hide in-range
                    // samples that happen to share a ray with out-of-range ones).
                    half inRange = step(_ClipMin, volumeSample.x) * step(volumeSample.x, _ClipMax);
                    half density = saturate(volumeSample.x * volumeSample.y * _DensityMultiplier) * inRange;

                    // _ColorSteps > 0 quantizes the LUT lookup into that many
                    // equal-width bins (discrete color bands) instead of a
                    // smooth gradient -- each bin samples the LUT at its
                    // center, so the band boundary is exact regardless of the
                    // LUT texture's own filter mode.
                    float lutU = volumeSample.x;
                    if (_ColorSteps > 0.5)
                    {
                        float bin = min(floor(lutU * _ColorSteps), _ColorSteps - 1);
                        lutU = (bin + 0.5) / _ColorSteps;
                    }

                    // u = normalized scalar value, v = 0.5 (LUT used as a 1D ramp).
                    half3 sampleColor = SAMPLE_TEXTURE2D_LOD(
                        _TemperatureLUT, sampler_TemperatureLUT, float2(lutU, 0.5), 0).rgb;
                    half sampleAlpha = density * (1.0 - accumulatedAlpha);

                    // Replace, don't add: the first (closest) sample with any
                    // density locks in the color for this ray, instead of
                    // summing every sample's color together (which washes
                    // into an over-bright blended mess as steps increase).
                    // Marching is front-to-back, so "accumulatedAlpha is
                    // still exactly 0" means no sample has contributed yet --
                    // i.e. this one is the closest.
                    if (accumulatedAlpha <= 0.0)
                        accumulatedColor = sampleColor;

                    accumulatedAlpha += sampleAlpha;

                    samplePos += rayDirOS * stepSize;
                }

                return half4(accumulatedColor, accumulatedAlpha);
            }
            ENDHLSL
        }
    }
}