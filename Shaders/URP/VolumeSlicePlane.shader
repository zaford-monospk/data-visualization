// Renders a single cross-section of a volume Texture3D on a flat plane mesh,
// instead of raymarching through a box (VolumeRenderer.shader) -- one texture
// sample per pixel, so there's no step count/jitter to tune and no banding or
// dither noise to hide: a slice is inherently smooth. The plane (TargetPlane)
// is free to be positioned/rotated anywhere in world space independent of the
// volume's own cube transform (TargetCube) -- each fragment's world position
// is reprojected into the volume's local -0.5..0.5 box space via
// _VolumeWorldToLocal (set from C# each frame, see VtkFrameRenderer.LateUpdate)
// before sampling, so the slice is correct from any plane angle/position.
Shader "Custom/VolumeSlicePlane"
{
    Properties
    {
        [MainTexture] _Volume("Volume", 3D) = "white" {}
        _TemperatureLUT("Temp LUT", 2D) = "white" {}
        _DensityMultiplier("Density Multiplier", Range(0, 10)) = 1
        _ClipMin("Clip Range Min", Range(0, 1)) = 0
        _ClipMax("Clip Range Max", Range(0, 1)) = 1
        // Same convention/meaning as VolumeRenderer.shader's
        // _LutStartTemperature/_LutEndTemperature: linearly remaps [start,
        // end] of the normalized value onto the LUT's [0, 1], clamping
        // anything outside that range to the nearest end's color.
        _LutStartTemperature("LUT Start Temperature", Range(0, 1)) = 0
        _LutEndTemperature("LUT End Temperature", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Tags { "LightMode" = "UniversalForward" }

            // Off (not Cull Front like the cube): a cutting plane should read
            // the same from either side, unlike the cube where only the far
            // face is ever rasterized toward the camera.
            Cull Off
            ZWrite Off
            // Always: consistent with VolumeRenderer.shader, draws over
            // opaque geometry in its screen footprint. There's no embedded-
            // object occlusion feature to reproduce here (that clips a
            // raymarch range; a slice has no range, just one sample).
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE3D(_Volume);
            SAMPLER(sampler_Volume);
            TEXTURE2D(_TemperatureLUT);
            SAMPLER(sampler_TemperatureLUT);

            // Keeps the LUT sample position from ever landing exactly on 0
            // or 1 -- see the _LutStartTemperature/_LutEndTemperature remap
            // in frag() for why.
            #define LutEdgeInset 0.001

            CBUFFER_START(UnityPerMaterial)
                float _DensityMultiplier;
                float _ClipMin;
                float _ClipMax;
                float _LutStartTemperature;
                float _LutEndTemperature;
            CBUFFER_END

            // Set every frame from C# (VtkFrameRenderer.LateUpdate) as
            // TargetCube.worldToLocalMatrix -- NOT part of UnityPerMaterial
            // since it depends on TargetCube's live transform, not anything
            // authored on the Material asset itself.
            float4x4 _VolumeWorldToLocal;

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // Reproject this fragment's world position into the volume
                // cube's local space -- the same -0.5..0.5 box
                // VolumeRenderer.shader raymarches through, so +0.5 gives the
                // same 0..1 UVW _Volume expects.
                float3 volumeLocalPos = mul(_VolumeWorldToLocal, float4(IN.positionWS, 1.0)).xyz;
                float3 uv = volumeLocalPos + 0.5;

                // Outside the volume's box -- the plane is free to extend
                // past the cube's bounds, so clip rather than sample garbage.
                if (any(uv < 0.0) || any(uv > 1.0))
                    return 0;

                // One sample, no raymarch -- no step count/jitter to tune,
                // and nothing to dither: a single-plane slice is inherently
                // smooth. r = normalized scalar value, a = voxel occupancy.
                half2 volumeSample = SAMPLE_TEXTURE3D_LOD(_Volume, sampler_Volume, uv, 0).ra;

                // Same [_ClipMin, _ClipMax] value-range filter as VolumeRenderer.shader.
                half inRange = step(_ClipMin, volumeSample.x) * step(volumeSample.x, _ClipMax);
                half density = saturate(volumeSample.x * volumeSample.y * _DensityMultiplier) * inRange;

                // Same _LutStartTemperature/_LutEndTemperature remap as
                // VolumeRenderer.shader -- max(..., 1e-5) guards the divide
                // if End is ever left <= Start rather than producing Inf/NaN.
                // The result is then inset slightly from the literal 0/1
                // edges -- sampling AT either edge hits an ambiguous
                // boundary position of the LUT texture, which can flicker
                // right at the value that exactly equals Start/End.
                float lutRange = max(_LutEndTemperature - _LutStartTemperature, 1e-5);
                float lutU01 = saturate((volumeSample.x - _LutStartTemperature) / lutRange);
                float lutU = lerp(LutEdgeInset, 1.0 - LutEdgeInset, lutU01);

                half3 sampleColor = SAMPLE_TEXTURE2D_LOD(
                    _TemperatureLUT, sampler_TemperatureLUT, float2(lutU, 0.5), 0).rgb;

                return half4(sampleColor, density);
            }
            ENDHLSL
        }
    }
}
