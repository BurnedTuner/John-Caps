Shader "John Caps/Cap Inverted Hull"
{
    Properties
    {
        _OutlineColor("Outline Color", Color) = (0, 1, 1, 1)
        _OutlineWidth("Outline Width", Float) = 0.035
        _VerticalScale("Vertical Scale", Range(0, 1)) = 1.0
        _BottomCutoff("Bottom Cutoff", Range(0, 1)) = 1.0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "InvertedHull"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            Cull Front
            ZTest LEqual
            ZWrite On

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex OutlineVertex
            #pragma fragment OutlineFragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _OutlineColor;
                float _OutlineWidth;
                float _VerticalScale;
                float _BottomCutoff;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float3 OutlineSafeNormalize(float3 value)
            {
                float lengthSquared = dot(value, value);
                return lengthSquared > 1e-8 ? value * rsqrt(lengthSquared) : 0.0;
            }

            Varyings OutlineVertex(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionOS = input.positionOS.xyz;

                float3 radialOS = float3(positionOS.x, 0.0, positionOS.z);
                float3 verticalOS = float3(0.0, sign(positionOS.y), 0.0);
                float3 radialWS = OutlineSafeNormalize(
                    TransformObjectToWorldDir(OutlineSafeNormalize(radialOS)));
                float3 verticalWS = OutlineSafeNormalize(TransformObjectToWorldDir(verticalOS));

                float3 positionWS = TransformObjectToWorld(positionOS);

                // _VerticalScale reduces the vertical expansion for ALL vertices
                // (both top and bottom), making the outline shorter.
                // _BottomCutoff additionally suppresses the bottom (downward)
                // expansion so it doesn't sink below the floor.
                //   isBottom = 1 for bottom vertices, 0 for top.
                float isBottom = max(0.0, -sign(positionOS.y));
                float verticalAmount = _VerticalScale * (1.0 - isBottom * _BottomCutoff);

                float3 expansion = radialWS + verticalWS * verticalAmount;

                positionWS += expansion * max(_OutlineWidth, 0.0);
                output.positionCS = TransformWorldToHClip(positionWS);
                return output;
            }

            half4 OutlineFragment(Varyings input) : SV_Target
            {
                return _OutlineColor;
            }
            ENDHLSL
        }
    }
}
