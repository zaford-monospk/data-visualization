Shader "Custom/InstancedPointRender"
{
    Properties
    {
        _TemperatureLUT("Temp LUT",2D) = "white" {}
        _MinPointSize("Min Point Size", Float) = 0.02
        _MaxPointSize("Max Point Size", Float) = 0.1
        _AlphaCutoff("Alpha Cutoff", Range(0, 1)) = 0.1
        _GlobalAlpha("Alpha",Range(0,1)) = 0.5
        _ClipMin("Clip Range Min", Range(0, 1)) = 0
        _ClipMax("Clip Range Max", Range(0, 1)) = 1
        _VelocityClipMin("Velocity Clip Min", Range(0, 1)) = 0
        _VelocityClipMax("Velocity Clip Max", Range(0, 1)) = 1
        _GlowColor("Glow Color", Color) = (0.4, 0.9, 1, 1)
        _GlowSpeed("Glow Speed", Float) = 2
        _GlowFrequency("Glow Frequency", Float) = 4
        _GlowSharpness("Glow Sharpness", Range(1, 32)) = 8
        _GlowIntensity("Glow Intensity", Float) = 1.5
    }

    SubShader
    {
        // Draws InstanceMesh (e.g. a small cone/arrow — pick something with a
        // visible "front" along local +Z, a sphere/cube won't show orientation)
        // once per cell via Graphics.RenderMeshIndirect. VtkUnstructuredGridRenderer
        // uploads one {float3 position, float temperature, float3 velocity,
        // float pressure} per cell into _CellBuffer, indexed here with
        // SV_InstanceID: Temperature -> color (sampled from _TemperatureLUT,
        // u = normalized temperature, v = 0.5), Pressure -> size, Velocity ->
        // orientation (mesh local +Z axis points along the velocity direction).
        // A glow band also travels along the mesh's local +Y (its own authored
        // "up", not the velocity-oriented +Z) over time (_GlowSpeed/_GlowFrequency/
        // _GlowSharpness/_GlowIntensity/_GlowColor) — see frag()'s wave calculation.
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Tags { "LightMode" = "UniversalForward" }

            // Blended transparency; fragments below _AlphaCutoff are discarded
            // outright (clip) rather than just blended near-invisible, so they
            // don't cost overdraw or leave faint fringing.
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct CellInstance
            {
                float3 position;
                float temperature; // normalized 0..1
                float3 velocity;   // raw velocity, direction only is used
                float pressure;    // normalized 0..1
            };

            StructuredBuffer<CellInstance> _CellBuffer;

            TEXTURE2D(_TemperatureLUT);
            SAMPLER(sampler_TemperatureLUT);

            CBUFFER_START(UnityPerMaterial)
                float _MinPointSize;
                float _MaxPointSize;
                float _AlphaCutoff;
                float _ClipMin;
                float _ClipMax;
                float _VelocityClipMin;
                float _VelocityClipMax;
                half4 _GlowColor;
                float _GlowSpeed;
                float _GlowFrequency;
                float _GlowSharpness;
                float _GlowIntensity;
            CBUFFER_END
            float _GlobalAlpha;

            // Per-renderer (Material.SetMatrix from VtkUnstructuredGridRenderer.Update,
            // only recomputed there when its Transform actually moves) — the
            // whole cell buffer is stored local/centered, this places it in
            // the world. Kept outside UnityPerMaterial, same reasoning as _GlobalAlpha.
            float4x4 _ObjectToWorld;

            // Per-renderer raw speed range (Material.SetFloat from Update), so
            // length(cell.velocity) — which arrives unnormalized, unlike
            // Temperature/Pressure — can be normalized to 0..1 here for
            // _VelocityClipMin/Max, the same range Temperature's clip uses.
            float _SpeedMin;
            float _SpeedMax;

            struct Attributes
            {
                float4 positionOS : POSITION;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                half4 color : TEXCOORD0;
                // Raw mesh-local Y (before pointSize scale / world placement),
                // i.e. position along the mesh's own authored "up" axis.
                // Interpolated per-pixel in frag for the traveling glow band —
                // a low-poly cone only has two vertex rings (base + apex), so
                // a per-vertex wave would just flip between two flat values.
                float localY : TEXCOORD1;
            };

            // Rotates localDir so local +Z lands on `forward`; local X/Y fan out
            // into an arbitrary (but consistent) perpendicular basis, which is
            // fine for glyphs that are rotationally symmetric around their axis
            // (cones, arrows) — swap for a proper up-vector if yours isn't.
            float3 OrientToVelocity(float3 localDir, float3 forward)
            {
                float3 upHint = abs(forward.y) > 0.99 ? float3(1, 0, 0) : float3(0, 1, 0);
                float3 right = normalize(cross(upHint, forward));
                float3 up = cross(forward, right);
                return localDir.x * right + localDir.y * up + localDir.z * forward;
            }

            Varyings vert(Attributes IN)
            {
                CellInstance cell = _CellBuffer[IN.instanceID];

                float speed = length(cell.velocity);
                float3 forward = speed > 1e-5 ? cell.velocity / speed : float3(0, 0, 1);

                float pointSize = lerp(_MinPointSize, _MaxPointSize, saturate(cell.pressure));
                float3 localOffset = OrientToVelocity(IN.positionOS.xyz * pointSize, forward);

                // cell.position is local (centered on the data's own bounds,
                // set in VtkUnstructuredGridRenderer.Set) — combine with the
                // glyph's own local offset, then place the whole thing in the
                // world with one matrix, so moving/rotating/scaling this
                // renderer's Transform moves the entire point cloud together.
                float3 localPos = cell.position + localOffset;
                float3 positionWS = mul(_ObjectToWorld, float4(localPos, 1)).xyz;

                // Vertex stage can't take implicit derivatives, so this is an
                // explicit-LOD lookup (LOD 0 — the LUT has no mips anyway).
                float2 lutUV = float2(saturate(cell.temperature), 0.5);
                half4 lutColor = SAMPLE_TEXTURE2D_LOD(_TemperatureLUT, sampler_TemperatureLUT, lutUV, 0);

                // Value-range filters: glyphs outside [_ClipMin, _ClipMax]
                // (temperature) or [_VelocityClipMin, _VelocityClipMax] (speed,
                // normalized via _SpeedMin/_SpeedMax same as Temperature/Pressure
                // are on the CPU) get alpha 0, so frag's existing _AlphaCutoff
                // clip discards them — one instance = one glyph here, so there's
                // no per-sample accumulation to preserve the way VolumeRenderer's
                // raymarch has.
                half inTempRange = step(_ClipMin, cell.temperature) * step(cell.temperature, _ClipMax);

                float speedNorm = saturate((speed - _SpeedMin) / max(_SpeedMax - _SpeedMin, 1e-5));
                half inSpeedRange = step(_VelocityClipMin, speedNorm) * step(speedNorm, _VelocityClipMax);

                half inRange = inTempRange * inSpeedRange;

                Varyings OUT;
                OUT.positionHCS = TransformWorldToHClip(positionWS);
                OUT.color = lutColor;
                OUT.color.a = _GlobalAlpha * inRange;
                OUT.localY = IN.positionOS.y;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                clip(IN.color.a - _AlphaCutoff);

                // Traveling glow band along the mesh's own local +Y (its
                // authored "up", independent of OrientToVelocity's rotation)
                // — phase decreasing with time makes a fixed band (constant
                // phase) correspond to increasing Y as time passes, i.e. the
                // glow visibly travels upward along the mesh.
                float phase = IN.localY * _GlowFrequency - _Time.y * _GlowSpeed;
                float wave = pow(saturate(sin(phase * 6.2831853) * 0.5 + 0.5), _GlowSharpness);

                half3 finalColor = IN.color.rgb + _GlowColor.rgb * wave * _GlowIntensity;
                return half4(finalColor, IN.color.a);
            }
            ENDHLSL
        }
    }
}