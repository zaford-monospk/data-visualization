Shader "Custom/InstancedPointRender"
{
    Properties
    {
        _ColorCold("Cold Color", Color) = (0, 0.2, 1, 1)
        _ColorHot("Hot Color", Color) = (1, 0.2, 0, 1)
        _MinPointSize("Min Point Size", Float) = 0.02
        _MaxPointSize("Max Point Size", Float) = 0.1
        _AlphaCutoff("Alpha Cutoff", Range(0, 1)) = 0.1
        _GlobalAlpha("Alpha",Range(0,1)) = 0.5
    }

    SubShader
    {
        // Draws InstanceMesh (e.g. a small cone/arrow — pick something with a
        // visible "front" along local +Z, a sphere/cube won't show orientation)
        // once per cell via Graphics.RenderMeshIndirect. VtkUnstructuredGridRenderer
        // uploads one {float3 position, float temperature, float3 velocity,
        // float pressure} per cell into _CellBuffer, indexed here with
        // SV_InstanceID: Temperature -> color + alpha (via _ColorCold/_ColorHot's
        // own alpha channels), Pressure -> size, Velocity -> orientation (mesh
        // local +Z axis points along the velocity direction).
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Tags { "LightMode" = "UniversalForward" }

            // Blended transparency; fragments below _AlphaCutoff are discarded
            // outright (clip) rather than just blended near-invisible, so they
            // don't cost overdraw or leave faint fringing.
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite On

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
            
            CBUFFER_START(UnityPerMaterial)
                half4 _ColorCold;
                half4 _ColorHot;
                float _MinPointSize;
                float _MaxPointSize;
                float _AlphaCutoff;
            CBUFFER_END
            float _GlobalAlpha;
            struct Attributes
            {
                float4 positionOS : POSITION;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                half4 color : TEXCOORD0;
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

                // cell.position is the raw VTK coordinate, used directly as world
                // space — this renderer doesn't yet apply its own GameObject
                // transform (RenderMeshIndirect is driven purely from _CellBuffer).
                float3 positionWS = cell.position + localOffset;

                Varyings OUT;
                OUT.positionHCS = TransformWorldToHClip(positionWS);
                OUT.color = lerp(_ColorCold, _ColorHot, saturate(cell.temperature));
                OUT.color.a = _GlobalAlpha;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                clip(IN.color.a - _AlphaCutoff);
                return IN.color;
            }
            ENDHLSL
        }
    }
}