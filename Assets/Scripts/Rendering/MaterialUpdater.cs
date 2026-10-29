using System.Collections.Generic;
using UnityEngine;

namespace GenshinToon.Rendering
{
    /// <summary>
    /// 每帧用头骨的世界旋转更新脸部朝向 _FaceDirection。
    ///
    /// 为什么需要：shader 里脸部 SDF 靠 _FaceDirection 判断"脸朝哪"，
    /// 这个值如果是固定世界向量，角色一转身脸部阴影就会反向
    /// （身体用 normalWS，会跟着物体转，所以只有脸会反）。
    ///
    /// 三个必须处理的坑：
    /// 1. MaterialPropertyBlock 不序列化，重开工程后是空的 → 必须每帧重写
    /// 2. [ExecuteAlways] 的 Update 在编辑模式不是每帧调用（只在 Scene 重绘时）
    ///    → 需要注册 EditorApplication.update
    /// 3. Biped 骨骼的 forward 可能指向 +X / +Y，不能用 (0,0,1) 硬算
    ///    → 用 InverseTransformDirection(root.forward) 反算局部方向
    ///
    /// 用 MaterialPropertyBlock 而不是 renderer.material：
    /// 后者在编辑模式下会给每个 Renderer 实例化材质，泄漏到场景里。
    /// </summary>
    [ExecuteAlways]
    public class MaterialUpdater : MonoBehaviour
    {
        [Tooltip("头部骨骼节点，一般是 Head")]
        public GameObject m_HeadBone;

        [Tooltip("相对头骨的朝向。用 UpdateFaceDirectionFromRoot 自动算，不用手填")]
        public Vector3 m_HeadDirection = Vector3.forward;

        [Tooltip("需要更新 _FaceDirection 的脸部 Renderer")]
        public List<SkinnedMeshRenderer> m_FaceRenderers = new List<SkinnedMeshRenderer>();

        private static readonly int s_FaceDirectionId = Shader.PropertyToID("_FaceDirection");
        private MaterialPropertyBlock m_Mpb;
        private Quaternion m_LastRootRotation;
        private Vector3 m_LastApplied;
        private bool m_Initialized;

        private void OnEnable()
        {
            // 重新启用时强制重算：场景重载后 MPB 是空的
            m_Initialized = false;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update += EditorTick;
#endif
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorTick;
#endif
        }

        private void OnValidate()
        {
            m_Initialized = false;
        }

        private void Update()
        {
            if (Application.isPlaying) Tick();
        }

#if UNITY_EDITOR
        private void EditorTick()
        {
            if (!Application.isPlaying) Tick();
        }
#endif

        private void Tick()
        {
            if (m_HeadBone == null) return;

            // 角色根节点转向变了，骨骼局部朝向必须重算
            if (!m_Initialized || Quaternion.Angle(transform.rotation, m_LastRootRotation) > 0.01f)
            {
                m_LastRootRotation = transform.rotation;
                m_Initialized = true;
                UpdateFaceDirectionFromRoot(transform);
                return;
            }

            UpdateFaceDirection();
        }

        public void UpdateFaceDirection()
        {
            if (m_FaceRenderers == null || m_HeadBone == null) return;

            Vector3 direction = m_HeadBone.transform.rotation * m_HeadDirection;
            Apply(direction);
        }

        /// <summary>
        /// 用角色根节点的朝向反算骨骼局部方向，不依赖具体骨骼的轴向。
        /// Biped 骨骼的 forward 经常指向 +X（甚至 +Y），直接填 (0,0,1) 会算错。
        /// </summary>
        public void UpdateFaceDirectionFromRoot(Transform root)
        {
            if (m_HeadBone == null || root == null) return;

            Vector3 faceWorld = root.forward;
            faceWorld.y = 0f;
            if (faceWorld.sqrMagnitude < 1e-6f) return;   // 根节点朝上，无意义
            faceWorld.Normalize();

            m_HeadDirection = m_HeadBone.transform.InverseTransformDirection(faceWorld);
            m_LastApplied = faceWorld;                     // 让 Apply 的判重正确
            Apply(faceWorld, true);
        }

        private void Apply(Vector3 direction)
        {
            Apply(direction, false);
        }

        private void Apply(Vector3 direction, bool force)
        {
            if (m_FaceRenderers == null || m_FaceRenderers.Count == 0) return;

            if (m_Mpb == null) m_Mpb = new MaterialPropertyBlock();

            // 值没变就不重复写，避免编辑模式下每帧刷新
            if (!force && (direction - m_LastApplied).sqrMagnitude < 1e-6f) return;
            m_LastApplied = direction;

            for (int i = 0; i < m_FaceRenderers.Count; i++)
            {
                SkinnedMeshRenderer renderer = m_FaceRenderers[i];
                if (renderer == null) continue;

                renderer.GetPropertyBlock(m_Mpb);
                m_Mpb.SetVector(s_FaceDirectionId, direction);
                renderer.SetPropertyBlock(m_Mpb);
            }
        }
    }
}
