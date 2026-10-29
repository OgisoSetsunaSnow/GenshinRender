using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using GenshinToon.Rendering;

namespace GenshinToon.Editor
{
    /// <summary>
    /// 把后处理 Feature 挂到 PC_Renderer，并在场景里建一个 Global Volume。
    /// </summary>
    public static class GenshinPostProcessInstaller
    {
        private const string RendererPath = "Assets/Settings/PC_Renderer.asset";
        private const string ProfilePath  = "Assets/Settings/GenshinPostProcessProfile.asset";

        [MenuItem("Tools/Genshin Toon/5. 安装后处理到 PC Renderer", false, 5)]
        public static void InstallFeature()
        {
            var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (data == null)
            {
                Debug.LogError("[GenshinToon] 找不到渲染器资产：" + RendererPath);
                return;
            }

            foreach (ScriptableRendererFeature f in data.rendererFeatures)
            {
                if (f is GenshinPostProcessFeature)
                {
                    Debug.Log("[GenshinToon] 后处理 Feature 已经装过了");
                    return;
                }
            }

            var feature = ScriptableObject.CreateInstance<GenshinPostProcessFeature>();
            feature.name = "Genshin Post Process";
            AssetDatabase.AddObjectToAsset(feature, data);

            data.rendererFeatures.Add(feature);
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            // 强制重导入，让 URP 重建内部的 feature 查找表
            AssetDatabase.ImportAsset(RendererPath, ImportAssetOptions.ForceUpdate);

            Debug.Log("[GenshinToon] 后处理 Feature 已添加到 " + RendererPath);
        }

        [MenuItem("Tools/Genshin Toon/6. 在当前场景创建后处理 Volume", false, 6)]
        public static void CreateVolume()
        {
            VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, ProfilePath);
                profile.Add<BloomVolume>(true);
                profile.Add<ColorGradingVolume>(true);
                AssetDatabase.SaveAssets();
                Debug.Log("[GenshinToon] 已创建 Volume Profile：" + ProfilePath);
            }

            var go = new GameObject("Genshin Post Process Volume");
            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 10f;
            volume.sharedProfile = profile;

            Undo.RegisterCreatedObjectUndo(go, "Create Genshin Volume");
            Selection.activeGameObject = go;
            EditorSceneManager.MarkSceneDirty(go.scene);

            Debug.Log("[GenshinToon] 已创建 Global Volume。Bloom 默认是 None，" +
                      "选中它之后在 Inspector 里把 Mode 改成 Color 或 Brightness 才会生效。");
        }
    }
}
