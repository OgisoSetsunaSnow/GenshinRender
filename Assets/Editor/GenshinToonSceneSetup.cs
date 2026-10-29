using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GenshinToon.Editor
{
    /// <summary>
    /// 把角色放进当前场景，并自动把 Renderer 上的材质换成 Materials 目录下的外部材质。
    ///
    /// 为什么要手动换材质：FBX 导入时如果没配 externalObjects，用的会是内嵌材质，
    /// 那种材质不指向我们的 shader 和贴图，显示出来就是错的。
    /// </summary>
    public static class GenshinToonSceneSetup
    {
        [MenuItem("Tools/Genshin Toon/放置角色/纳西妲（参考项目）", false, 40)]
        private static void PlaceNahida() => PlaceCharacter("Nahida", "Avatar_Loli_Catalyst_Nahida.fbx");

        private static void PlaceCharacter(string charName, string fbxName)
        {
            string path = $"Assets/Characters/{charName}/{fbxName}";
            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (model == null)
            {
                Debug.LogError($"[GenshinToon] 找不到模型：{path}");
                return;
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            instance.transform.position = Vector3.zero;
            Undo.RegisterCreatedObjectUndo(instance, "Place Character");

            int replaced = AssignExternalMaterials(instance, $"Assets/Characters/{charName}/Materials");
            HideUtilityLayers(instance);

            Renderer[] renderers = instance.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                Debug.LogWarning("[GenshinToon] 角色没有 Renderer");
                return;
            }

            Bounds bounds = renderers[0].bounds;
            foreach (Renderer r in renderers) bounds.Encapsulate(r.bounds);

            float height = Mathf.Max(bounds.size.y, 0.1f);
            Vector3 center = bounds.center;

            Camera cam = Object.FindAnyObjectByType<Camera>();
            if (cam != null)
            {
                Undo.RecordObject(cam.transform, "Place Character");
                cam.transform.position = center + new Vector3(0f, height * 0.05f, -height * 1.8f);
                cam.transform.LookAt(center + Vector3.up * height * 0.05f);
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 100f;
            }

            Light[] lights = Object.FindObjectsByType<Light>();
            foreach (Light light in lights)
            {
                if (light.type != LightType.Directional) continue;
                Undo.RecordObject(light.transform, "Place Character");
                light.transform.rotation = Quaternion.Euler(35f, 145f, 0f);
                light.intensity = 1f;
                light.color = new Color(1f, 0.97f, 0.92f);
                break;
            }

            Selection.activeGameObject = instance;
            SceneView.lastActiveSceneView?.FrameSelected();
            Debug.Log($"[GenshinToon] 已放置 {model.name}，高度 {height:F2}，替换材质 {replaced} 个");
        }

        /// <summary>
        /// 禁用模型自带的辅助层。
        ///
        /// EyeStar 是瞳孔上的星形高光面片（原神里给技能/特写用的），
        /// EffectMesh 是特效面片。这两个网格会直接被当成实体渲染，
        /// 在瞳孔上盖出一个金色十字星，看起来像渲染错误。
        ///
        /// 参考工程里这两个都是 activeSelf=False，这里保持一致。
        /// </summary>
        private static void HideUtilityLayers(GameObject instance)
        {
            string[] names = { "EyeStar", "EffectMesh" };

            foreach (Transform t in instance.GetComponentsInChildren<Transform>(true))
            {
                for (int i = 0; i < names.Length; i++)
                {
                    if (t.name != names[i]) continue;
                    t.gameObject.SetActive(false);
                    Debug.Log($"[GenshinToon] 已禁用辅助层：{t.name}");
                    break;
                }
            }
        }

        /// <summary>
        /// 用材质名做匹配，三级回退：
        /// 1. 槽位名和外部材质完全同名（Nahida_Face → Nahida_Face）
        /// 2. 外部材质以 _槽位名 结尾（Nahida_Hair → Hair）
        /// 3. 原名匹配
        ///
        /// FBX 里的槽位可能带一长串前缀，比如 Avatar_Loli_Catalyst_Nahida_Mat_Hair，
        /// 所以先取 _Mat_ 之后的部分当槽位名。
        /// </summary>
        private static int AssignExternalMaterials(GameObject instance, string matDir)
        {
            if (!AssetDatabase.IsValidFolder(matDir)) return 0;

            var pool = new List<Material>();
            foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { matDir }))
            {
                Material m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                if (m != null) pool.Add(m);
            }
            if (pool.Count == 0) return 0;

            int count = 0;
            foreach (Renderer r in instance.GetComponentsInChildren<Renderer>(true))
            {
                Material[] mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    string original = mats[i].name;
                    string slot = ExtractSlotName(original);

                    Material match = pool.FirstOrDefault(m => m.name == original)
                                  ?? pool.FirstOrDefault(m => m.name == slot)
                                  ?? pool.FirstOrDefault(m => m.name.EndsWith("_" + slot));
                    if (match == null) continue;

                    mats[i] = match;
                    count++;
                }
                r.sharedMaterials = mats;
            }
            return count;
        }

        private static string ExtractSlotName(string materialName)
        {
            const string marker = "_Mat_";
            int idx = materialName.IndexOf(marker, System.StringComparison.Ordinal);
            if (idx >= 0) return materialName.Substring(idx + marker.Length);
            return materialName;
        }
    }
}
