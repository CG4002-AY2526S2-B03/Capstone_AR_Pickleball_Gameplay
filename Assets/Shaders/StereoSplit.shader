Shader "Hidden/StereoSplit"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "StereoSplitPass"
            ZTest Always ZWrite Off Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BlitTexture);
            SAMPLER(sampler_BlitTexture);

            float _StereoUVShift;
            float _StereoDividerWidth;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                // Full-screen triangle from vertex ID
                float2 uv = float2((input.vertexID << 1) & 2, input.vertexID & 2);
                output.positionCS = float4(uv * 2.0 - 1.0, 0.0, 1.0);

                #if UNITY_UV_STARTS_AT_TOP
                output.uv = float2(uv.x, 1.0 - uv.y);
                #else
                output.uv = uv;
                #endif
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;

                // DEBUG: left half = red, right half = blue, center = black
                // Remove this block once we confirm the shader output reaches the screen
                float halfDiv = 0.01;
                if (uv.x > 0.5 - halfDiv && uv.x < 0.5 + halfDiv)
                    return half4(0, 0, 0, 1);
                if (uv.x < 0.5)
                    return half4(1, 0, 0, 1);  // LEFT = RED
                else
                    return half4(0, 0, 1, 1);  // RIGHT = BLUE
            }
            ENDHLSL
        }
    }
}
