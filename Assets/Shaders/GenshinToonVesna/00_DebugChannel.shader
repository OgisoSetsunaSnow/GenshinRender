Shader "GenshinToonVesna/00_DebugChannel"
{
    Properties
    {
        _Tex ("贴图",2D)="white"{}
        _Channel("0=原图 1=R 2=G 3=B 4=A 5=UV",Range(0,5))=0
        _Cutoff("裁剪阈值",Range(0,1))=0.5
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "DebugChannel"
            Tags{"LightMode"="UniversalForward"}
            Cull Off

            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

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

            TEXTURE2D(_Tex);
            SAMPLER(sampler_Tex);

            CBUFFER_START(UnityPerMaterial)
                float4 _Tex_ST;
                float _Channel;
                float _Cutoff;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _Tex);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                if(_Channel>4.5)
                    return half4(IN.uv.x,IN.uv.y,0.0,1.0);
                
                half4 c = SAMPLE_TEXTURE2D(_Tex, sampler_Tex, IN.uv);
                if(_Channel<0.5){
                    clip(c.a-_Cutoff);
                    return half4(c.rgb,1.0);
                }
                if(_Channel<1.5)return half4(c.rrr,1.0);
                if(_Channel<2.5)return half4(c.ggg,1.0);
                if(_Channel<3.5)return half4(c.bbb,1.0);
                return half4(c.aaa,1.0);
            }
            ENDHLSL
        }
    }
}
