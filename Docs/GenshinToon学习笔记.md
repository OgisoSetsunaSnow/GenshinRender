# Genshin Toon 渲染笔记

参考项目：`NahidaRenderProject-main`（基于 kaze-mio 的 UnityGenshinToonShader + UnityGenshinPostProcessing）

---

## 一、整套东西其实就一个公式

```hlsl
finalColor = albedo * shadowColor + specular + rim + emission;
```

PBR 是「BRDF 积分」，卡通渲染是「**把光照结果查表**」。
阴影不是算出来的渐变，是从一张 ramp 图上**按条件取色**。

区别就在这：PBR 里 N·L 连续变化，所以过渡平滑；卡通里 N·L 被压成阶梯，所以有硬分界。

---

## 二、五个 Pass 各管什么

| Pass | LightMode | 作用 |
|---|---|---|
| Forward | UniversalForward | 主光照，所有卡通计算都在这 |
| ShadowCaster | ShadowCaster | 投阴影（复用 URP 内置实现） |
| DepthOnly | DepthOnly | 深度图（后处理、软粒子用） |
| DepthNormals | DepthNormals | 法线图（SSAO、边缘检测用） |
| Outline | SRPDefaultUnlit | 描边，URP 会在不透明之后自动画 |

后三个直接 `#include` URP 内置的 pass 文件，不用自己写——这是省事的关键。

---

## 三、LightMap 的四个通道（核心）

原神角色的 LightMap 不是给人看的贴图，是一张**四通道数据图**：

| 通道 | 含义 | 用途 |
|---|---|---|
| R | 高光强度 / 金属遮罩 | `step(0.9, R)` 判断是不是金属 |
| G | AO | 和顶点色 R 相乘，控制阴影覆盖范围 |
| B | 高光范围 | 决定高光多锐利 |
| A | **材质类型** | 决定用 ramp 的第几行、描边用第几个颜色 |

```hlsl
half aoFactor = lightMap.g * input.color.r;
half material = lerp(lightMap.a, _CustomMaterialType, _UseCustomMaterialType);
```

**MMD 模型没有这张图**，所以本项目用 `_UseCustomMaterialType = 1` 手工指定材质类型，
LightMap 用一张 `G = 0.5` 的中性图兜底。

> 为什么 G 不能是 1：`GetShadow` 里有 `step(0.9, aoFactor)`，AO 接近 1 时直接判定全受光，
> 角色会变成一张没有阴影的平涂。G = 0.5 时才等于标准半兰伯特。

---

## 四、ShadowRamp 的 UV 编码（最烧脑的地方）

```hlsl
half2 rampUV = half2(smoothstep(rangeMin, rangeMax, shadow),
                     index / 10.0 + 0.5 * day + 0.05);
```

一张 256×20 的图，横轴 = 受光程度，纵轴 = **材质类型 × 白天/夜晚**：

```
v = 0.05, 0.15, 0.25, 0.35, 0.45   →  夜晚，材质 index 0~4
v = 0.55, 0.65, 0.75, 0.85, 0.95   →  白天，材质 index 0~4
```

`index` 是材质类型映射出来的：

| material 值 | index | 含义 |
|---|---|---|
| < 0.2 | 4 | 皮肤 / 默认 |
| 0.2 ~ 0.4 | 1 | 金属 |
| 0.4 ~ 0.6 | 2 | 布料 |
| 0.6 ~ 0.8 | 0 | 头发 |
| ≥ 0.8 | 3 | 特殊（发光、丝绸） |

所以**一张图能塞 10 条不同的阴影曲线**，切换昼夜只改 `_IsDay` 一个 float。

---

## 五、为什么要 `2.0 * halfLambert`

```hlsl
half halfLambert = 0.5 * NDotL + 0.5;          // 映射到 0~1
half shadow = saturate(2.0 * halfLambert * aoFactor);
```

半兰伯特在 N·L = 0（正侧面）时值是 0.5，如果直接拿去查 ramp，
ramp 的 `rangeMin/rangeMax` 是围绕 0.5 的，会导致「侧面就已经全亮」，阴影范围太小。

乘 2 相当于把曲线拉伸，让 0.5 对应到 1.0——受光正面尽快饱和，
背光才进入阴影区，这样阴影分界才落在正确的角度上。

---

## 六、脸部 SDF

脸不用 N·L，因为脸是平的，法线几乎不变，算出来没有阴影变化。

改用一张**横向渐变图**（`_FaceLightMap`），存的是「从左边照过来时，脸部每个横向位置的明暗阈值」：

```hlsl
half FCrossL = cross(F, L).y;                       // 光在脸的左边还是右边
shadowUV.x = lerp(shadowUV.x, 1 - shadowUV.x, step(0, FCrossL));  // 在右边就翻转
half faceShadow = step(-0.5 * FDotL + 0.5 + offset, faceLightMap.r);
```

`-0.5 * FDotL + 0.5` 就是「光照角度 → 阈值」的映射：
- 正面光 FDotL = 1 → 阈值 0 → 全亮
- 侧面光 FDotL = 0 → 阈值 0.5 → 亮一半
- 背面光 FDotL = -1 → 阈值 1 → 全暗

这是原神脸部阴影的做法，优点是**阴影边缘永远是脸的形状，不会被鼻子顶出脏块**。

> 本项目默认**没开** `_IS_FACE`：MMD 模型的脸部 UV 和原神不一样，
> 直接套通用渐变会一半黑一半白。等你有原神模型的脸部 SDF 图再开。

---

## 七、描边

```hlsl
Cull Front                          // 只画背面
positionVS += width * normalVS;     // 视空间沿法线外扩
```

三个细节值得记：

1. **只在视空间扩 XY，不扩 Z**，否则描边会被深度拉扯成锯齿
2. **宽度随距离变化**：`_OutlineWidthParams = (0, 6, 0.1, 0.6)` 表示
   0~6 单位内宽度从 0.1 变到 0.6，保证远近看起来一样粗
3. **顶点色 alpha 参与宽度**：建模时可以把不想描边的地方（比如眼球）涂黑

---

## 八、参数速查

| 想要的效果 | 调什么 | 方向 |
|---|---|---|
| 阴影边界更硬 | `_ShadowSmoothness` | ↓ |
| 阴影范围更大 | `_ShadowOffset` | ↑ |
| 阴影更暗 | `_ShadowColor` | ↓（HDR，默认 1.1） |
| 角色整体更亮 | `_LightDirectionMultiplier.y` | ↑（默认 0.5） |
| 描边更粗 | `_OutlineWidth` | ↑ |
| 远处描边更细 | `_OutlineWidthParams.w` | ↓ |
| 边缘光更强 | `_RimIntensity` | ↑ |
| 皮肤描边更暖 | `_OutlineColor5` | 偏红 |

---

## 九、已知限制与后续路线

**当前版本（Stage 1）**
- 没有真 LightMap，所有材质共用一张中性图，靠 `_CustomMaterialType` 区分
- 没开脸部 SDF
- 后处理已移植（见第十节），但需要手动装 Feature + 建 Volume

**Stage 2 可做的**
1. 开脸部 SDF：把 `_IS_FACE` 打开，用原神模型的 FaceLightmap 替换默认图
2. 接法线贴图：`_NORMAL_MAP` + `Vesna/normalmap`
3. 细分材质类型：现在是按名字关键词猜的，可以手工精调每个材质的 `_CustomMaterialType`

**Stage 3（后处理已完成，见第十节）**
- 加描边平滑法线烘焙（解决硬边模型描边断裂）
- 把 Bloom 的 4 级权重做成可调的 Inspector 预设

---

## 十、后处理：从 URP 14 搬到 URP 17 踩的坑

参考项目的后处理是**自定义 Bloom + 调色**，不是 URP 自带的。
链路：`Prefilter → 4 级模糊 → 4 张权重混合 → ColorGrading`。

Bloom 之所以分 4 级，是因为单一半径的高斯模糊做不出「近处锐利、远处弥散」的辉光；
逐级降采样后按权重合并，低频铺得开、高频不会糊成一片。

### API 对照表（这张表是 Unity 6 迁移的核心）

| URP 14 / 2022.3 | URP 17 / Unity 6 |
|---|---|
| `Execute(ScriptableRenderContext, ref RenderingData)` | `RecordRenderGraph(RenderGraph, ContextContainer)` |
| 手动 `RTHandle` + `RenderingUtils.ReAllocateIfNeeded` | `renderGraph.CreateTexture(desc)`，生命周期由 graph 管 |
| `Blitter.BlitCameraTexture(cmd, ...)` | `renderGraph.AddBlitPass(...)` 或 `AddRasterRenderPass` + `DrawProcedural` |
| `renderingData.cameraData.renderer.cameraColorTargetHandle` | `resourceData.activeColorTexture` |
| 写完要自己 `SetRenderTarget` | 写回 `resourceData.cameraColor = dst` |
| `commandBuffer.SetGlobalTexture(...)` | 用 `MaterialPropertyBlock` 在 render func 里绑纹理 |
| `#pragma multi_compile _ _FORWARD_PLUS` | `#pragma multi_compile _ _CLUSTER_LIGHT_LOOP` |

### 三个最容易翻车的点

**1. 不要在 Record 阶段碰 TextureHandle 的实际纹理对象**
`record` 阶段只是在「描述」渲染图，纹理还没分配。
`(Texture)textureHandle` 这种隐式转换只能在 `SetRenderFunc` 的委托里调用。

**2. 读写同一张 RT 要小心**
`V1: B0 → A0` 之后又 `Upsample → B0`，看起来没问题，但 RenderGraph 会当成两次写入。
最稳的做法是给最终结果单独开一张纹理。

**3. 共享材质的状态在 Record 阶段设置**
URP 17 没有 `CommandBuffer` 了，`_Exposure` 这类非纹理参数直接 `material.SetFloat` 就行，
因为 Record 是单线程的图构建阶段。纹理才必须走 MPB。

### 安装后处理

1. `Tools / Genshin Toon / 5. 安装后处理到 PC Renderer`
2. `Tools / Genshin Toon / 6. 在当前场景创建后处理 Volume`
3. 选中新建的 Volume 物体，Inspector 里把 Bloom 的 Mode 从 None 改成 **Color**
4. 同时把 URP 自带 Global Volume 里的 Bloom 关掉，否则两套辉光会叠加

> Bloom 默认是 None，因为 `IsActive()` 返回 false 时整个 pass 都不会执行——
> 这样没配 Volume 的场景不会有额外开销。

---

## 十一、参考模型（纳西妲）

从参考项目整套搬过来了，放在 `Assets/Characters/Nahida/`：

- `Avatar_Loli_Catalyst_Nahida.fbx` — 原神提取版，UV 和材质槽是标准的
- `Nahida_MMD.fbx` — MMD 版，用来对比两种来源的差异
- `tex/` — 17 张贴图，含真正的 **LightMap / ShadowRamp / FaceLightmap**
- `Materials/` — 10 个材质，全部是 Material Variant，继承 `Nahida_Base`

### 为什么纳西妲用的是原版 shader

`Assets/Shaders/GenshinToonLegacy/` 是参考项目的原版，连 `.meta` 一起拷的，
GUID 保持不变（`4196783971...`），所以材质的引用一个都没断。

新版（`Genshin/URPGenshinToon`）和原版（`URPGenshinToon`）名字不同，可以共存。
好处是**你有一个标准答案可以对照**：同样的角色、同样的贴图，
一边是新 shader、一边是原版，差在哪一眼就能看出来。

### 材质匹配规则

`Tools / Genshin Toon / 放置角色 / ...` 会自动换材质，规则是：
FBX 里槽位叫 `Body`，外部材质叫 `Nahida_Body` —— 按「完全同名」或「以 `_槽位名` 结尾」匹配。

换材质这步不能省：FBX 如果没配 `externalObjects`，用的是内嵌材质，
那种材质不指向我们的 shader，拖进场景一定是粉的。

---

## 十二、实跑配置（CharRender 场景）

### 场景结构

```
CharRender
├── Main Camera          FOV 32，位置 (0, 1.15, -4.6)，看向 (0, 0.85, 0)
├── Directional Light    Euler(42, -38, 0)，强度 1.15，色温偏暖
├── Odette               x = -1.5，绕 Y 转 180°
├── 纳西妲               x = 0，   绕 Y 转 180°
├── Vesna                x = 1.5， 绕 Y 转 180°
└── Genshin Post Process Volume   Global，priority 10
```

绕 Y 转 180° 是因为 Blender 导出的 FBX 默认朝 +Z，而相机在 -Z。

### 后处理参数（当前值）

| 参数 | 值 | 作用 |
|---|---|---|
| Tonemapping | on | ACES 近似，压高光，避免白裙过曝 |
| Exposure | 0.82 | 整体亮度 |
| Contrast | 22 | 拉开明暗 |
| Saturation | 32 | 二次元需要高饱和 |
| Bloom Mode | Color | 按颜色提亮部 |
| Bloom Intensity | 0.9 | |
| Bloom Threshold | 0.85 | 只让接近纯白的地方发光 |

### 踩过的三个坑

**1. 相机的 Post Processing 是关的**
`UniversalAdditionalCameraData.renderPostProcessing = false` 时，URP 完全不跑后处理，
自定义 pass 里改的 exposure/contrast 一点效果都没有。这是最隐蔽的一个。

**2. URP Asset 的 MSAA 默认是 1**
`msaaSampleCount = 1` 等于关闭，边缘全是锯齿。改成 4 之后配合相机 FXAA 才干净。

**3. 参数幅度要够大**
contrast +8、saturation +6 这种量级在画面里几乎看不出来，
二次元要 +20 起步才有明显区别。

### 还没调的

- `_ShadowSmoothness` 现在是 0.02（很硬），如果觉得太硬可以往回加到 0.05
- 脸部 SDF 依然没开，脸部的明暗是半兰伯特算的，不如原神的 SDF 稳定
- Bloom 的 4 级权重 `(0.25, 0.25, 0.25, 0.25)` 可以按喜好调成前重后轻

---

## 十三、参考项目实测配置（从 SampleScene.unity 和 URPGenshinVolumeProfile.asset 解析）

这一节不是猜的，是逐字段读出来的原始数值。

### 场景结构

```
AvatarRoot        position (0,0,0)  rotation identity  ← 角色不转
  └ 模型实例（含 MaterialUpdater 组件）
BaseCamera        position (0, 1, 1.4)  rotation Y=180°   ← 相机转过来
MainCamera        Overlay 相机，挂在 BaseCamera 下
Background        Quad，scale (17.78, 10, 1)，pos (0,0,-1)，Y=180°
Ground            Plane
Directional Light position (0,3,0)
Genshin Volume    Global，priority 0
```

**最重要的一条**：角色保持 identity，转身的是相机。
所以我们把角色转 180° 是错的方向——脸部 `_FaceDirection` 是常量，角色一转就反。

### 灯光

| 项 | 值 |
|---|---|
| Type | Directional |
| Color | 纯白 (1,1,1) |
| Intensity | **1.0** |
| Shadows | Soft (m_Type=2) |
| Bias / NormalBias | 0.05 / 0.4 |
| 四元数 | (0.2113, 0.7849, -0.4532, 0.3670) |

### 相机

| 项 | BaseCamera | MainCamera(Overlay) |
|---|---|---|
| FOV | 50 | 50 |
| Near/Far | 0.3 / 1000 | |
| HDR | 1 | |
| MSAA | 1 (AllowMSAA) | |
| Post Processing | **1 (开)** | 1 |
| Antialiasing | **2 = SMAA** | 0 = None |
| AA Quality | 2 = High | 2 |

参考项目**没有用 MSAA，用的是 SMAA**。

### Volume（URPGenshinVolumeProfile）

Bloom:
| 参数 | 值 |
|---|---|
| mode | 1 = **Color** |
| threshold | **0.7** |
| intensity | **1.5** |
| weights | **(0.1, 0.2, 0.3, 0.4)** ← 前轻后重，不是均匀 |
| color | white |
| blurRadius | 2 |
| downSampleScale | 0.5 |

ColorGrading:
| 参数 | 值 |
|---|---|
| useTonemapping | **1 (开)** |
| exposure | **1.05** |
| contrast | **0** |
| saturation | **0** |

注意：contrast 和 saturation 都是 **0**，即完全不调整。
之前凭感觉加的 +22 / +32 是错的。

### MaterialUpdater.cs（脸部朝向的正确解法）

```csharp
Vector3 direction = m_HeadBone.transform.rotation * m_HeadDirection;
foreach (var renderer in m_FaceRenderers)
    foreach (var material in renderer.materials)
        material.SetVector("_FaceDirection", direction);
```

每帧把头骨旋转作用到 `_FaceDirection`，角色怎么转都不会反。

两个坑：
1. **Biped 骨骼的 forward 不是 +Z**。纳西妲的 `Bip001 Head` 的 `rotation * Vector3.forward` 指向 +X，
   直接填 `(0,0,1)` 会算歪。要用 `head.InverseTransformDirection(root.forward)` 反算。
2. 参考项目用 `renderer.materials`，在编辑模式下会给每个 Renderer 实例化材质并泄漏。
   改用 `MaterialPropertyBlock` 更干净。

---

## 十四、过曝的真正原因：不是参数，是缺背景

排查过程值得记下来，因为它推翻了我之前的全部猜测。

### 现象
角色边缘一圈很宽很亮的白光，白裙子糊成一片，怎么调 Bloom 参数都别扭。

### 错误做法
把 intensity 从 1.5 降到 0.45、threshold 从 0.7 提到 0.88，
用"我们的场景更亮所以参数要降"来解释——这是错的，属于拿参数掩盖结构问题。

### 真正的原因
参考项目有一块 `Background` 背板（17.8×10 的 Quad，贴原神场景图）挡住整个视野。
有背板时，Bloom 的亮部（threshold 0.7 以上）分布在整个画面上，扩散是均匀的氛围光；
纯色背景下，角色是画面里唯一的亮源，**所有 Bloom 能量都堆在角色轮廓上**，
就变成了一圈白光。

### 做法
1. 把 `Textures/Scene/20231021014638.png` 拷过来当背板贴图
2. 建 Quad（5.6×3.2），放 z=+1.6，**不要旋转**（Quad 默认面朝 +Z，相机在 -Z 侧看的是正面；
   转 180° 会被 Cull Back 剔掉，这是踩过的坑）
3. 材质用 `Universal Render Pipeline/Unlit`，Cull 设 0 保险
4. Bloom 参数**全部恢复参考值**（threshold 0.7 / intensity 1.5 / weights 0.1,0.2,0.3,0.4）

加完背板后，同样的参数直接就对，不需要任何"适配"。

### 顺带修的一处代码
`renderGraph.GetTextureDesc(source)` 返回的 desc 可能是 `TextureSizeMode.Scale`，
这种情况下设置的 `width/height` 会被忽略，逐级降采样就失效了。
显式设成 `Explicit` 更保险。

> 教训：画面不对的时候，先看场景结构（光照环境、背景、遮挡），再看参数。
> 参数是最后才动的东西。

---

## 十五、URP 17 最坑的一个变化：URP Asset 自带 Volume Profile

### 现象

把参考项目（Unity 2022 / URP 14）的模型、贴图、材质、shader、后处理参数全部一比一搬到
Unity 6 / URP 17，画面始终比参考「灰蒙蒙、发暗、模糊」：

| 区域 | 指标 | 参考 | 目标 | 比例 |
|---|---|---|---|---|
| 胸腹 | std | 65.88 | 52.90 | 0.80 |
| 胸腹 | lapVar | 5635 | 3520 | 0.62 |
| 裙摆 | lapVar | 2562 | 1326 | 0.52 |

查了材质（全量 diff 一致）、贴图导入（17 张 0 差异）、FBX 导入（25 项 0 差异）、
顶点色、光照方向、环境光强度、Graphics API、分辨率、抗锯齿、URP HLSL 依赖……
全部排除，差异依然存在。

### 根因

**URP 17 的 `UniversalRenderPipelineAsset` 上有一个 `m_VolumeProfile` 字段。
新建 URP 项目时，Unity 会自动挂一个 `SampleSceneProfile`，里面带
Tonemapping + Bloom + Vignette，全部 active。**

它**独立于场景里的 Volume 组件**：

```
场景里的 Volume 组件  ← 我们以为关掉的是这个
URP Asset 的 Volume Profile  ← 实际还在跑，而且优先级更高（作为默认值）
```

所以：

- `volume.enabled = false` → 没用
- 删掉场景里的 Volume 物体 → 没用
- 只要 URP Asset 上挂着它，Bloom 和 Tonemapping 就一直生效

### 为什么参考项目没有这个问题

URP 14 的 URP Asset 根本没有这个字段（序列化里连 `m_VolumeProfile` 都不存在）。
所以参考项目的"关 Volume"是真正的关闭。

### 怎么查

```csharp
var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
var so = new SerializedObject(asset);
var p = so.FindProperty("m_VolumeProfile");
Debug.Log(p.objectReferenceValue);   // 不是 null 就说明有隐藏后处理
```

或者直接看 `PC_RPAsset.asset` 的 YAML，搜 `m_VolumeProfile`。

### 怎么处理

两个选择：

1. **置空**（推荐，后处理完全交给场景 Volume 控制）
   ```csharp
   so.FindProperty("m_VolumeProfile").objectReferenceValue = null;
   so.ApplyModifiedProperties();
   ```
2. 保留但清空里面的组件（在 URP Asset 的 Inspector 里点开编辑）

### 修好之后

| 区域 | 指标 | 目标 | 参考 | 比例 |
|---|---|---|---|---|
| 胸腹 | mean | 163.1 | 162.5 | 1.004 |
| 胸腹 | std | 65.59 | 65.04 | 1.009 |
| 胸腹 | lapVar | 5303 | 5626 | 0.943 |
| 裙摆 | mean | 217.6 | 217.5 | 1.000 |
| 裙摆 | std | 32.97 | 33.06 | 0.997 |
| 裙摆 | lapVar | 1706 | 1778 | 0.960 |

**什么都不用调，参数全部用参考原值，画面直接对上。**

> 教训（比上一条更重要）：
> **做对照实验之前，先验证「关闭」是不是真的关闭。**
> 一个你以为关掉、实际还在跑的效果，会让后面所有的排查结论全部失效。
> 排查一整天，不如先花两分钟确认状态。

### 连带的一个坑

因为我们之前在「有隐藏 Bloom」的基础上调参补偿，攒下了一堆错误参数：
`exposure 1.16 / contrast 12 / saturation 12 / bloom intensity 0.55 / threshold 0.86 /
blurRadius 1.2 / outline 1.0`。

这些全部要还原成参考值（`1.05 / 0 / 0 / 1.5 / 0.7 / 2 / 1.6`）。
**任何"为了看起来对"而偏离参考的参数，都是下一次排查的障碍。**






---

## 十六、脸部光照反向：三个坑叠在一起

### 现象

角色转身后（或者把角色从 180° 改回 0°），脸部明暗和身体相反 / 或者脸完全没有阴影。

### shader 里这段是怎么算的

```hlsl
half3 F = SafeNormalize(half3(_FaceDirection.x, 0.0, _FaceDirection.z));  // 脸朝哪
half3 L = SafeNormalize(half3(lightDirection.x, 0.0, lightDirection.z));  // 光从哪来
half FDotL = dot(F, L);
half FCrossL = cross(F, L).y;

half2 shadowUV = input.uv;
shadowUV.x = lerp(shadowUV.x, 1.0 - shadowUV.x, step(0.0, FCrossL));   // 左右翻转
half faceShadowMap = SAMPLE_TEXTURE2D(_FaceLightMap, sampler_FaceLightMap, shadowUV).r;
half faceShadow = step(-0.5 * FDotL + 0.5 + _FaceShadowOffset, faceShadowMap);
```

`_FaceDirection` 是**世界空间**向量。它错了，脸就错了；但身体用的是 `normalWS`，
会跟着物体转，所以**只有脸会反**。

注意一个反直觉的点：把 `_FaceDirection` 取反，画面**不会**简单左右镜像。
因为 `FCrossL` 也变号，`shadowUV.x` 的翻转跟着变，两个效应抵消了一部分。
实测取反只让脸暗了 3 个亮度值，方向没变。所以别用"取反试试"来判断对错。

### 坑 1：局部方向缓存没有跟着根节点旋转

`m_HeadDirection` 存的是"相对头骨的朝向"，靠
`head.InverseTransformDirection(root.forward)` 算出来。
角色根节点一转，这个缓存就失效了。

```csharp
// 错误：只算一次
m_HeadDirection = head.InverseTransformDirection(root.forward);

// 正确：每帧检查根节点旋转有没有变
if (Quaternion.Angle(transform.rotation, m_LastRootRotation) > 0.01f)
{
    m_LastRootRotation = transform.rotation;
    UpdateFaceDirectionFromRoot(transform);
}
```

### 坑 2：MaterialPropertyBlock 不会被保存

MPB 是纯运行时数据。Unity 重开工程、重载场景、切 Play 模式，它都会变空。
`GetPropertyBlock` 读出来是 `(0,0,0,0)`。

但**这不等于 shader 收到 0**——MPB 没设过的字段，渲染时会回退到材质里的值。
所以"MPB 是 0"这件事本身不一定是 bug，要看材质里的值对不对。

### 坑 3：`[ExecuteAlways]` 的 Update 在编辑模式下不是每帧跑

这是最阴的一个。加了 `[ExecuteAlways]` 之后：

```csharp
[ExecuteAlways]
public class X : MonoBehaviour { private void Update() { ... } }
```

在编辑模式下，`Update` **只在 Scene 视图需要重绘时才调用**（比如你动了鼠标）。
所以 MPB 可能长时间不更新，或者干脆一次都不写。

正确做法是显式挂到编辑器的更新循环：

```csharp
private void OnEnable()
{
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

private void EditorTick()
{
    if (!Application.isPlaying) Tick();   // 编辑模式的每帧
}

private void Update()
{
    if (Application.isPlaying) Tick();    // 运行时的每帧
}
```

### 怎么验证修好了

不要靠眼睛，用点乘：

```csharp
var dir = mpb.GetVector("_FaceDirection");
float dot = Vector3.Dot(new Vector3(dir.x, 0, dir.z).normalized, root.forward);
// dot 应该 = 1.0
```

实测：角色转 90° → `_FaceDirection = (1,0,0)`，dot = **1.0000**；
转回 0° → `(0,0,1)`，dot = **1.0000**。

### 怎么判断"是不是真的反了"

取脸部横向亮度剖面（把脸按列切成 12 段，每段求平均亮度），看峰值在第几段：

```
目标  198.8 194.7 195.1 197.1 202.5 212.1 [231.4] 223.9 218.4 204.9 203.2 191.3   峰值 7/12
参考  187.3 185.6 190.9 193.3 195.8 210.8 [230.0] 221.9 211.9 198.3 202.9 179.8   峰值 7/12
```

峰值段一致 = 方向一致。这比看整体明暗可靠得多。

> 教训：
> **"我看它反了"要用剖面/点乘去证实，不能靠肉眼和整体亮度。**
> 这次的实际问题不是方向反了，是 `_FaceDirection` 根本没人写（编辑模式 Update 不跑），
> 脸处于"没有 SDF 计算"的状态，看起来就像不跟着身体走。

---

## 十七、"星星眼"：模型自带的辅助层

### 现象

纳西妲瞳孔里有一个非常明显的金黄色十字星，参考工程没有。

### 根因

不是渲染错误，是**模型自带的两个辅助网格没被禁用**。

原神提取的 FBX 里，纳西妲除了 Body / Brow / Face / Face_Eye 之外，还有：

| 网格 | 顶点数 | 用途 |
|---|---|---|
| `EyeStar` | 50 | 瞳孔上的星形高光面片，游戏里给特写/技能演出用 |
| `EffectMesh` | — | 特效面片 |

它们被赋了 `Nahida_Face` 材质（`_SrcBlend=1 / _DstBlend=0`，不透明，`_Cutoff=0`，
`_ALPHATEST_ON` 关闭），于是**整块星形网格被当成实体渲染**，
用脸部 Diffuse 贴图按自己的 UV 采样出一块金色，直接压在瞳孔上。

参考工程的做法很简单：这两个节点 `activeSelf = false`，压根不渲染。

### 怎么查

遍历时一定要 **包含未激活对象**，否则查不到：

```csharp
// 错：默认只返回 active 的对象，会以为参考工程没有 EyeStar
Object.FindObjectsOfType<SkinnedMeshRenderer>()

// 对：
Object.FindObjectsOfType<SkinnedMeshRenderer>(true)
```

查出来会是：

```
参考工程                     目标工程
EyeStar    activeSelf=False   EyeStar    activeSelf=True   ← 多渲染了
EffectMesh activeSelf=False   EffectMesh activeSelf=True   ← 多渲染了
```

### 修复

1. 场景里把两个节点的 `activeSelf` 设为 false
2. 放置工具里自动处理：

```csharp
private static void HideUtilityLayers(GameObject instance)
{
    string[] names = { "EyeStar", "EffectMesh" };
    foreach (Transform t in instance.GetComponentsInChildren<Transform>(true))
    {
        for (int i = 0; i < names.Length; i++)
        {
            if (t.name != names[i]) continue;
            t.gameObject.SetActive(false);
            break;
        }
    }
}
```

### 顺带发现：两个工程的模型不同源

对比 Renderer 列表时发现：

| | 参考工程 `NahidaUnityModel` | 目标工程 `Avatar_Loli_Catalyst_Nahida` |
|---|---|---|
| Body 的 mesh | `Nahida_Body_Smooth` | `Body` |
| Renderer 数 | 4（+2 个禁用） | 6 |
| 额外节点 | `纳西妲_mesh`（MMD 版） | — |

命名一个带 `_Smooth` 后缀、一个不带，说明**不是同一份导入产物**。
之前做像素级对比时残留的 4~6% 差异，很可能就和这个有关。

> 教训：
> **"多出来的东西"往往来自模型本身，而不是渲染管线。**
> 排查渲染问题时，先把「场景里有哪些对象、各自是否启用」列一遍，
> 比对着画质指标猜要快得多。
