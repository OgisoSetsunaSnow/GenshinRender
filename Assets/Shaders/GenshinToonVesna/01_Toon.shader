Shader "GenshinToonVesna/01_Toon"
{
    Properties
    {
        _BaseMap  ("漫反射贴图", 2D) = "white" {}
        _RampMap  ("阴影渐变图", 2D) = "white" {}
        _RampRow  ("取第几行 0.17/0.50/0.83", Range(0,1)) = 0.5
        [Toggle] _RampVertical ("竖向取样 MMD式", Float) = 0
        _Cutoff   ("裁剪阈值", Range(0,1)) = 0.5
        [Header(Face)]
        _FaceLightMap     ("脸部光照图 SDF", 2D) = "white" {}
        _FaceDirection    ("脸部朝向 世界空间", Vector) = (0,0,1,0)
        _FaceShadowOffset ("脸部阴影偏移", Range(-0.5,0.5)) = 0
        [Toggle] _IsFace  ("这是脸", Float) = 0
        [Toggle] _Unlit ("不参与光照", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode"="UniversalForward" }
            Cull Off

            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                float3 positionOS  : TEXCOORD2;
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_RampMap);
            SAMPLER(sampler_RampMap);
            TEXTURE2D(_FaceLightMap);
            SAMPLER(sampler_FaceLightMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float _RampRow;
                float _RampVertical;
                float _Cutoff;
                float4 _FaceDirection;
                float  _FaceShadowOffset;
                float  _IsFace;
                float _Unlit;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.normalWS=TransformObjectToWorldNormal(IN.normalOS);
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.positionOS = IN.positionOS.xyz;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 baseCol = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);
                clip(baseCol.a-_Cutoff);
                half3 N=normalize(IN.normalWS);
                Light mainLight=GetMainLight();
                half3 ramp;
                if(_Unlit>0.5){
                    ramp=half3(1,1,1);
                }
                else if(_IsFace>0.5){
                    half3 F=normalize(half3(_FaceDirection.x,0.0,_FaceDirection.z));
                    half3 L=normalize(half3(mainLight.direction.x,0.0,mainLight.direction.z));
                    half FDotL=dot(F,L);
                    half FCrossL=cross(F,L).y;
                    half2 sdfUV=IN.uv;
                    sdfUV.x=lerp(sdfUV.x,1.0-sdfUV.x,step(0.0,FCrossL));
                    half sdf=SAMPLE_TEXTURE2D(_FaceLightMap,sampler_FaceLightMap,sdfUV).r;
                    half faceLit=step(-0.5*FDotL+0.5+_FaceShadowOffset,sdf);
                    half3 shadeColor=SAMPLE_TEXTURE2D(_RampMap,sampler_RampMap,float2(0.5,0.0)).rgb;
                    ramp=lerp(shadeColor,half3(1,1,1),faceLit);
                }
                else{
                    half NdotL =dot(N,mainLight.direction);
                    half halfLambert=NdotL*0.5+0.5;
                    half shadow=halfLambert;
                    half2 rampUV=(_RampVertical>0.5)?float2(0.5,shadow):float2(shadow,_RampRow);
                    ramp=SAMPLE_TEXTURE2D(_RampMap,sampler_RampMap,rampUV).rgb;
                }
                half3 color=baseCol.rgb*ramp*mainLight.color;
                return half4(color,1.0);
            }
            ENDHLSL
        }
    }
}
