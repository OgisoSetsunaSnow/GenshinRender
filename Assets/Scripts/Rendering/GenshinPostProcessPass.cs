using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace GenshinToon.Rendering
{
    /// <summary>
    /// 自定义 Bloom + ColorGrading，走 Unity 6 的 RenderGraph API。
    ///
    /// 和参考项目（URP 14）的区别：
    /// URP 14 用 Execute(ScriptableRenderContext, ref RenderingData) + CommandBuffer + RTHandle 手动管理；
    /// Unity 6 的 RenderGraph 里 RT 由 renderGraph 统一分配回收，每个 pass 只声明"读什么、写什么"。
    /// </summary>
    public class GenshinPostProcessPass : ScriptableRenderPass
    {
        public enum Pass
        {
            Blit = 0,
            BloomPrefilter = 1,
            BloomHorizontalBlur1x = 2,
            BloomHorizontalBlur2x = 3,
            BloomVerticalBlur1x = 4,
            BloomVerticalBlur2x = 5,
            BloomUpsample = 6,
            ColorGrading = 7
        }

        private const int k_Iterations = 4;

        private readonly Material _material;

        private static readonly int s_BlitTexture   = Shader.PropertyToID("_BlitTexture");
        private static readonly int s_BlitScaleBias = Shader.PropertyToID("_BlitScaleBias");
        private static readonly int s_BlitTexelSize = Shader.PropertyToID("_BlitTexture_TexelSize");
        private static readonly int s_BloomA        = Shader.PropertyToID("_BloomTextureA");
        private static readonly int s_BloomB        = Shader.PropertyToID("_BloomTextureB");
        private static readonly int s_BloomC        = Shader.PropertyToID("_BloomTextureC");
        private static readonly int s_BloomD        = Shader.PropertyToID("_BloomTextureD");

        private static readonly MaterialPropertyBlock s_Mpb = new MaterialPropertyBlock();

        private class PassData
        {
            public Material material;
            public int passIndex;
            public TextureHandle source;
            public TextureHandle bloomA;
            public TextureHandle bloomB;
            public TextureHandle bloomC;
            public TextureHandle bloomD;
            public Vector4 texelSize;
        }

        public GenshinPostProcessPass(Material material, RenderPassEvent evt)
        {
            _material = material;
            renderPassEvent = evt;
            profilingSampler = new ProfilingSampler(nameof(GenshinPostProcessPass));
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            if (cameraData.cameraType == CameraType.Preview || cameraData.cameraType == CameraType.Reflection)
                return;

            // 已经渲染到 backbuffer 时不能再往里叠加，否则会覆盖掉后处理前的颜色
            if (resourceData.isActiveTargetBackBuffer)
                return;

            VolumeStack stack = VolumeManager.instance.stack;
            BloomVolume bloom = stack.GetComponent<BloomVolume>();
            ColorGradingVolume grading = stack.GetComponent<ColorGradingVolume>();

            bool useBloom = bloom != null && bloom.IsActive();
            bool useGrading = grading != null && grading.IsActive();
            if (!useBloom && !useGrading) return;

            SetupMaterial(bloom, grading, cameraData, useBloom);

            TextureHandle source = resourceData.activeColorTexture;
            TextureHandle bloomResult = TextureHandle.nullHandle;

            if (useBloom)
                bloomResult = BuildBloomChain(renderGraph, source, bloom, cameraData);

            TextureDesc desc = renderGraph.GetTextureDesc(source);
            desc.name = "_GenshinPostProcessTarget";
            desc.clearBuffer = false;
            desc.msaaSamples = MSAASamples.None;
            TextureHandle destination = renderGraph.CreateTexture(desc);

            AddToonBlit(renderGraph, source, destination, bloomResult,
                TextureHandle.nullHandle, TextureHandle.nullHandle, TextureHandle.nullHandle,
                (int)Pass.ColorGrading, "Genshin Color Grading");

            // 把结果交还给 URP，后续 pass 会用它当相机颜色
            resourceData.cameraColor = destination;
        }

        private TextureHandle BuildBloomChain(RenderGraph renderGraph, TextureHandle source,
            BloomVolume bloom, UniversalCameraData cameraData)
        {
            TextureDesc baseDesc = renderGraph.GetTextureDesc(source);
            // GetTextureDesc 返回的可能是 Scale 模式，此时 width/height 会被忽略，
            // 必须显式改成 Explicit 才能让逐级降采样真正生效
            baseDesc.sizeMode = TextureSizeMode.Explicit;
            baseDesc.clearBuffer = false;
            baseDesc.msaaSamples = MSAASamples.None;
            baseDesc.filterMode = FilterMode.Bilinear;
            baseDesc.wrapMode = TextureWrapMode.Clamp;
            baseDesc.useMipMap = false;

            int baseW = Mathf.Max(1, Mathf.RoundToInt(cameraData.cameraTargetDescriptor.width * bloom.downSampleScale.value));
            int baseH = Mathf.Max(1, Mathf.RoundToInt(cameraData.cameraTargetDescriptor.height * bloom.downSampleScale.value));
            int w = baseW;
            int h = baseH;

            var texA = new TextureHandle[k_Iterations];
            var texB = new TextureHandle[k_Iterations];

            for (int i = 0; i < k_Iterations; i++)
            {
                TextureDesc d = baseDesc;
                d.width = w;
                d.height = h;

                d.name = $"_GenshinBloomA{i}";
                texA[i] = renderGraph.CreateTexture(d);
                d.name = $"_GenshinBloomB{i}";
                texB[i] = renderGraph.CreateTexture(d);

                w = Mathf.Max(w / 2, 1);
                h = Mathf.Max(h / 2, 1);
            }

            // 1. 提取高光并降采样
            AddToonBlit(renderGraph, source, texA[0], TextureHandle.nullHandle,
                TextureHandle.nullHandle, TextureHandle.nullHandle, TextureHandle.nullHandle,
                (int)Pass.BloomPrefilter, "Bloom Prefilter");

            // 2. 第一级模糊
            AddToonBlit(renderGraph, texA[0], texB[0], TextureHandle.nullHandle,
                TextureHandle.nullHandle, TextureHandle.nullHandle, TextureHandle.nullHandle,
                (int)Pass.BloomHorizontalBlur1x, "Bloom Blur H1");
            AddToonBlit(renderGraph, texB[0], texA[0], TextureHandle.nullHandle,
                TextureHandle.nullHandle, TextureHandle.nullHandle, TextureHandle.nullHandle,
                (int)Pass.BloomVerticalBlur1x, "Bloom Blur V1");

            // 3. 逐级降采样模糊，半径加倍让低频更宽
            for (int i = 1; i < k_Iterations; i++)
            {
                AddToonBlit(renderGraph, texA[i - 1], texB[i], TextureHandle.nullHandle,
                    TextureHandle.nullHandle, TextureHandle.nullHandle, TextureHandle.nullHandle,
                    (int)Pass.BloomHorizontalBlur2x, "Bloom Blur H2");
                AddToonBlit(renderGraph, texB[i], texA[i], TextureHandle.nullHandle,
                    TextureHandle.nullHandle, TextureHandle.nullHandle, TextureHandle.nullHandle,
                    (int)Pass.BloomVerticalBlur1x, "Bloom Blur V1");
            }

            // 4. 四级加权合并（输出到独立纹理，避免读写同一个 handle）
            TextureDesc resultDesc = baseDesc;
            resultDesc.width = baseW;
            resultDesc.height = baseH;
            resultDesc.name = "_GenshinBloomResult";
            TextureHandle bloomResult = renderGraph.CreateTexture(resultDesc);

            AddToonBlit(renderGraph, texA[0], bloomResult, texA[0], texA[1], texA[2], texA[3],
                (int)Pass.BloomUpsample, "Bloom Upsample");

            return bloomResult;
        }

        /// <summary>
        /// 一次全屏绘制。bloomA~D 只有 Upsample 那一趟会用到，其余传 nullHandle。
        /// </summary>
        private void AddToonBlit(RenderGraph renderGraph, TextureHandle source, TextureHandle destination,
            TextureHandle bloomA, TextureHandle bloomB, TextureHandle bloomC, TextureHandle bloomD,
            int passIndex, string passName)
        {
            using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out PassData data, profilingSampler))
            {
                TextureDesc srcDesc = renderGraph.GetTextureDesc(source);

                data.material = _material;
                data.passIndex = passIndex;
                data.source = source;
                data.bloomA = bloomA;
                data.bloomB = bloomB;
                data.bloomC = bloomC;
                data.bloomD = bloomD;
                data.texelSize = new Vector4(
                    1f / Mathf.Max(1, srcDesc.width),
                    1f / Mathf.Max(1, srcDesc.height),
                    srcDesc.width,
                    srcDesc.height);

                builder.UseTexture(source, AccessFlags.Read);
                if (bloomA.IsValid()) builder.UseTexture(bloomA, AccessFlags.Read);
                if (bloomB.IsValid()) builder.UseTexture(bloomB, AccessFlags.Read);
                if (bloomC.IsValid()) builder.UseTexture(bloomC, AccessFlags.Read);
                if (bloomD.IsValid()) builder.UseTexture(bloomD, AccessFlags.Read);

                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((PassData d, RasterGraphContext ctx) => RenderBlit(d, ctx));
            }
        }

        private static void RenderBlit(PassData data, RasterGraphContext ctx)
        {
            MaterialPropertyBlock mpb = s_Mpb;
            mpb.Clear();

            mpb.SetTexture(s_BlitTexture, data.source);
            mpb.SetVector(s_BlitScaleBias, new Vector4(1f, 1f, 0f, 0f));
            mpb.SetVector(s_BlitTexelSize, data.texelSize);

            if (data.bloomA.IsValid()) mpb.SetTexture(s_BloomA, data.bloomA);
            if (data.bloomB.IsValid()) mpb.SetTexture(s_BloomB, data.bloomB);
            if (data.bloomC.IsValid()) mpb.SetTexture(s_BloomC, data.bloomC);
            if (data.bloomD.IsValid()) mpb.SetTexture(s_BloomD, data.bloomD);

            ctx.cmd.DrawProcedural(Matrix4x4.identity, data.material, data.passIndex,
                MeshTopology.Triangles, 3, 1, mpb);
        }

        private void SetupMaterial(BloomVolume bloom, ColorGradingVolume grading,
            UniversalCameraData cameraData, bool useBloom)
        {
            if (useBloom)
            {
                if (bloom.mode.value == BloomMode.Color)
                {
                    _material.EnableKeyword("_BLOOM_COLOR");
                    _material.DisableKeyword("_BLOOM_BRIGHTNESS");
                }
                else
                {
                    _material.DisableKeyword("_BLOOM_COLOR");
                    _material.EnableKeyword("_BLOOM_BRIGHTNESS");
                }

                float screenFactor = cameraData.cameraTargetDescriptor.height / 1080f;
                _material.SetFloat("_BloomThreshold", bloom.threshold.value);
                _material.SetFloat("_BloomIntensity", bloom.intensity.value);
                _material.SetVector("_BloomWeights", bloom.weights.value);
                _material.SetColor("_BloomColor", bloom.color.value);
                _material.SetFloat("_BlurRadius", bloom.blurRadius.value * bloom.downSampleScale.value * screenFactor);
            }
            else
            {
                _material.DisableKeyword("_BLOOM_COLOR");
                _material.DisableKeyword("_BLOOM_BRIGHTNESS");
            }

            if (grading != null)
            {
                if (grading.useTonemapping.value) _material.EnableKeyword("_TONEMAPPING");
                else _material.DisableKeyword("_TONEMAPPING");

                _material.SetFloat("_Exposure", grading.exposure.value);
                _material.SetFloat("_Contrast", 1f + grading.contrast.value / 100f);
                _material.SetFloat("_Saturation", 1f + grading.saturation.value / 100f);
            }
            else
            {
                _material.DisableKeyword("_TONEMAPPING");
                _material.SetFloat("_Exposure", 1f);
                _material.SetFloat("_Contrast", 1f);
                _material.SetFloat("_Saturation", 1f);
            }
        }
    }
}
