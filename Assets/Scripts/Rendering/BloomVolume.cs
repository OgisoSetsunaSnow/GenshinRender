using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GenshinToon.Rendering
{
    /// <summary>
    /// 自定义 Bloom。和 URP 内置 Bloom 的区别：
    /// 降采样后做 4 级高斯模糊，最后把 4 级按权重加权混合，所以发光更"厚"、更接近原神。
    /// </summary>
    [Serializable]
    [VolumeComponentMenu("Genshin/Bloom")]
    [SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
    public class BloomVolume : VolumeComponent, IPostProcessComponent
    {
        public VolumeParameter<BloomMode> mode = new VolumeParameter<BloomMode>();

        public MinFloatParameter threshold = new MinFloatParameter(0.7f, 0f);

        public MinFloatParameter intensity = new MinFloatParameter(1.5f, 0f);

        public Vector4Parameter weights = new Vector4Parameter(0.25f * Vector4.one);

        public ColorParameter color = new ColorParameter(Color.white);

        public MinFloatParameter blurRadius = new MinFloatParameter(2f, 0f);

        public ClampedFloatParameter downSampleScale = new ClampedFloatParameter(0.5f, 0.1f, 1f);

        public bool IsActive()
        {
            return mode.value != BloomMode.None;
        }

        public bool IsTileCompatible()
        {
            return false;
        }
    }
}
