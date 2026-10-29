using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GenshinToon.Rendering
{
    /// <summary>
    /// 把后处理 Pass 挂进 URP 渲染器。需要手动 Add Renderer Feature 到 PC_Renderer 资产上，
    /// 或者用菜单 Tools / Genshin Toon / 5. 安装后处理。
    /// </summary>
    public class GenshinPostProcessFeature : ScriptableRendererFeature
    {
        [SerializeField]
        private RenderPassEvent m_RenderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;

        [SerializeField]
        private Shader m_Shader;

        private Material _material;
        private GenshinPostProcessPass _pass;

        public override void Create()
        {
            if (m_Shader == null)
                m_Shader = Shader.Find("URPGenshinPostProcess");

            if (m_Shader == null)
            {
                _pass = null;
                return;
            }

            if (_material == null)
                _material = CoreUtils.CreateEngineMaterial(m_Shader);

            _pass = new GenshinPostProcessPass(_material, m_RenderPassEvent);
            _pass.ConfigureInput(ScriptableRenderPassInput.Color);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_pass == null) return;

            CameraType type = renderingData.cameraData.cameraType;
            if (type == CameraType.Preview || type == CameraType.Reflection) return;

            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_material);
            _material = null;
            _pass = null;
        }
    }
}
