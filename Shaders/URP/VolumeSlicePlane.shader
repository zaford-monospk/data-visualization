// Renders a single, already-2D CFD slice (e.g. a CFD "X1"/"X2" plane-cut CSV
// export -- see VtkFrameReader.BuildData(OnProcessTex2DData)) directly on a
// flat plane mesh via its own UV0, one texture sample per pixel: no
// raymarching, no 3D-volume reprojection, no per-value density fade. This
// used to also support sampling a Texture3D (_Volume) reprojected through a
// world-to-local matrix, for cutting an arbitrary plane through a real
// volume -- that mode has been removed entirely: it's what made this look
// like a soft, blended cross-section instead of the simple, hard-edged,
// correctly-occluded quad it's meant to be. Concretely:
//   - Alpha blending is gone. A pixel is either fully opaque (drawn with its
//     LUT color) or fully discarded (clip()) -- outside [_ClipMin, _ClipMax],
//     or wherever the source texture has no data (occupancy 0). No more
//     partial/faded alpha.
//   - ZWrite is back on, like any ordinary opaque/cutout surface -- alpha-
//     blended transparency (this shader's old ZWrite Off + Blend) only ever
//     sorts by per-OBJECT distance, not per-pixel depth, which is why it
//     didn't look "properly" depth tested against other geometry (or other
//     slice planes) despite ZTest being enabled. Writing real depth here
//     makes it behave exactly like a simple unlit cutout quad: correctly
//     occluded by, and correctly occluding, everything else in the scene.
Shader "Custom/VolumeSlicePlane"
{
    Properties
    {
        [MainTexture] _VolumeSlice2D("Volume Slice (2D)", 2D) = "white" {}
        _TemperatureLUT("Temp LUT", 2D) = "white" {}
        _ClipMin("Clip Range Min", Range(0, 1)) = 0
        _ClipMax("Clip Range Max", Range(0, 1)) = 1
        // Linearly remaps [start, end] of the normalized value onto the
        // LUT's [0, 1], clamping anything outside that range to the nearest
        // end's color -- same convention as VolumeRenderer.shader's
        // _LutStartTemperature/_LutEndTemperature.
        _LutStartTemperature("LUT Start Temperature", Range(0, 1)) = 0
        _LutEndTemperature("LUT End Temperature", Range(0, 1)) = 1
    }

    SubShader
    {
        // Opaque/cutout, not Transparent -- see the header comment for why:
        // this needs to write depth and be sorted with regular opaque
        // geometry, not alpha-blended back-to-front by object distance.
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Tags { "LightMode" = "UniversalForward" }

            // Off (not Cull Front like the raymarched cube): a cutting plane
            // should read the same from either side.
            Cull Off
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_VolumeSlice2D);
            SAMPLER(sampler_VolumeSlice2D);
            TEXTURE2D(_TemperatureLUT);
            SAMPLER(sampler_TemperatureLUT);

            // Keeps the LUT sample position from ever landing exactly on 0
            // or 1 -- see the _LutStartTemperature/_LutEndTemperature remap
            // in frag() for why.
            #define LutEdgeInset 0.001

            CBUFFER_START(UnityPerMaterial)
                float _ClipMin;
                float _ClipMax;
                float _LutStartTemperature;
                float _LutEndTemperature;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // r = normalized scalar value, a = voxel occupancy (0 where
                // no data landed at all -- see VtkFrameReader.Build2DTexture;
                // its FillEmptyCells hole-filling normally makes this 1
                // everywhere, but this still guards a source that skipped it).
                half2 volumeSample = SAMPLE_TEXTURE2D_LOD(_VolumeSlice2D, sampler_VolumeSlice2D, IN.uv, 0).ra;

                // Cutout instead of the old density-based alpha fade: a pixel
                // outside [_ClipMin, _ClipMax], or with no data at all, is
                // discarded outright rather than drawn faded/see-through --
                // see the header comment for why that matters for depth
                // testing. clip() takes a negative value to mean "discard".
                half inRange = step(_ClipMin, volumeSample.x) * step(volumeSample.x, _ClipMax);
                clip(min(inRange, volumeSample.y) - 0.5);

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

                half3 color = SAMPLE_TEXTURE2D_LOD(
                    _TemperatureLUT, sampler_TemperatureLUT, float2(lutU, 0.5), 0).rgb;

                // Always fully opaque here -- anything that isn't gets
                // clip()'d away above instead of drawn with partial alpha.
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }
}
