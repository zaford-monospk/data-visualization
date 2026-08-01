Shader "Custom/VolumeRenderer"
{
    Properties
    {
        [MainTexture] _Volume("Volume", 3D) = "white" {}
        _ColorCold("Cold Color", Color) = (0, 0.2, 1, 1)
        _ColorHot("Hot Color", Color) = (1, 0.2, 0, 1)
        _DensityMultiplier("Density Multiplier", Range(0, 10)) = 1
        _StepCount("Step Count", Range(8, 256)) = 64
        _AlphaCutoff("Alpha Cutoff", Range(0, 1)) = 0.99
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
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE3D(_Volume);
            SAMPLER(sampler_Volume);

            CBUFFER_START(UnityPerMaterial)
                half4 _ColorCold;
                half4 _ColorHot;
                float _DensityMultiplier;
                float _StepCount;
                float _AlphaCutoff;
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
                float3 invRayDirOS = 1.0 / rayDirOS;

                float2 boxDst = RayBoxDst(rayOriginOS, invRayDirOS);
                float dstToBox = boxDst.x;
                float dstInsideBox = boxDst.y;

                int steps = max(1, (int)_StepCount);
                float stepSize = dstInsideBox / steps;

                // Jitter the ray start per-pixel to turn step-size banding into noise.
                float jitter = Hash13(IN.positionHCS.xyz) * stepSize;
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
                    half density = saturate(volumeSample.x * volumeSample.y * _DensityMultiplier);

                    half3 sampleColor = lerp(_ColorCold.rgb, _ColorHot.rgb, volumeSample.x);
                    half sampleAlpha = density * (1.0 - accumulatedAlpha);

                    accumulatedColor += sampleColor * sampleAlpha;
                    accumulatedAlpha += sampleAlpha;

                    samplePos += rayDirOS * stepSize;
                }

                return half4(accumulatedColor, accumulatedAlpha);
            }
            ENDHLSL
        }
    }
}