# GenshinRender

Unity 6 + URP 17 的二次元卡通渲染工程。移植自 [NahidaRenderProject]（Unity 2022 + URP 14），
并把 URP 14 的 `Execute` 后处理链路重写成了 URP 17 的 `RecordRenderGraph`。

## 环境要求

| | 版本 |
|---|---|
| Unity | **6000.6.7f1**（必须一致，URP 版本绑定） |
| 渲染管线 | URP 17.6.0（由 `Packages/manifest.json` 自动拉取） |
| 色彩空间 | Linear |
| Graphics API | D3D12（默认），D3D11 也验证过画面一致 |

用 Unity Hub 添加本目录即可。首次打开会重新导入资源、编译 shader，需要几分钟。

## 打开后先做什么

1. 打开场景 `Assets/Scenes/CharRender.unity`
2. 菜单 `Tools / Genshin Toon / 放置角色 / 纳西妲（参考项目）`
   —— 会自动实例化模型、替换材质、摆好相机和主光
3. 确认 `Assets/Settings/PC_RPAsset.asset` 上的 **Volume Profile 字段为空**

> 第 3 步很关键。URP 17 的 URP Asset 自带一个 `m_VolumeProfile` 字段，
> 新建工程时会默认挂上 `SampleSceneProfile`（含 Tonemapping + Bloom + Vignette）。
> 它独立于场景里的 Volume 组件，**禁用场景 Volume 也关不掉它**，会一直叠在画面上。
> 本工程已置空。如果画面发灰、发暗、边缘糊，先查这里。

## 目录结构

```
Assets/
  Shaders/
    GenshinToon/        改配到 URP 17 的 Toon shader（Odette / Vesna 用）
    GenshinToonLegacy/  原版 shader，逐字节未改（纳西妲对照用）
    GenshinLesson/      教学用，从白模一步步搭 toon 的中间版本
    PostProcessing/     Bloom + ColorGrading
  Scripts/
    Rendering/          RenderGraph 后处理、Volume 组件、MaterialUpdater
  Editor/               材质批量转换、场景布置、贴图导入规则
  Characters/           Odette / Vesna / Nahida 的模型与贴图
  Settings/             URP Asset、Renderer、后处理 Profile
  Textures/             背景图、默认 ramp / lightmap
  Reference/            参考工程的原始配置与材质（不参与运行）
Docs/
  GenshinToon学习笔记.md      原理、参数速查、踩过的坑
  画质差异排查报告.md          2022 → Unity 6 迁移的完整排查记录
```

## 常用菜单

`Tools / Genshin Toon/` 下：

| 菜单 | 作用 |
|---|---|
| 1. 转换全部角色材质 | 把 `Assets/Characters` 下的材质批量切到 Toon shader（会跳过 Nahida） |
| 3. 还原备份材质 | 从 `_mat_backup` 还原 |
| 4/放置角色 | 实例化模型、替换材质、摆相机和主光 |
| 5. 安装后处理到 PC Renderer | 把后处理 Feature 挂到 Renderer 上 |
| 6. 在当前场景创建后处理 Volume | 建一个 Global Volume 并绑定 Profile |

## 已知限制

- **纳西妲用的是原版 shader + 原神真实 LightMap**，Odette / Vesna 是 MMD 模型，
  没有 LightMap / ShadowRamp / FaceLightMap，共用一张中性默认图，效果不可能完全一致。
- **场景是单相机**，参考工程是三相机栈（`Base(mask=0)` + `Back(FOV60, layer6)` + `Main(FOV50)`）。
  因此 SMAA 的作用范围不同，锐度指标有 4~6% 残留差异，肉眼不可辨。
- 脸部 SDF 只对纳西妲生效；MMD 模型没有对应的 SDF 贴图。

## 素材版权

`Assets/Characters/Nahida/` 下的模型、贴图来自《原神》（miHoYo），
Odette / Vesna 来自 MMD 模型。仅用于渲染技术学习，**不要用于商业用途**。
