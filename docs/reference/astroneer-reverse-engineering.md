# Astroneer v1.42.3.0 —— 宇宙 / 星球 / 可破坏地形 逆向分析报告

> 分析对象：`F:\games\Astroneer v1.42.3.0`（Unreal Engine 4.26+ Shipping，`1.42.3.0-SHP`）
> 分析手段：REA（PE 反射元数据 / 字符串 / 段）静态分析。本机未安装 Ghidra，因此**未做**函数级反编译；
> 本报告的结论全部来自 UE4 的 **UClass/UFUNCTION/UPROPERTY 反射元数据**（Shipping 版仍保留 `Name` 表与 `::` 函数签名），
> 这足以还原系统级架构，但**不等于**原版算法源码。

---

## 0. 结论速览

Astroneer 的“无限可破坏宇宙”由**四层**叠成，理解这四层就理解了全部：

| 层 | 关键类 / 数据 | 职责 |
|---|---|---|
| ① 星系层 | `USolarSystem` / `ASolarBody` / `ESolarBodyIdentifier` | 恒星 + 行星 + 轨道平台 + 传送门，做轨道运动 |
| ② 行星层 | `AAstroPlanet` / `APlanetProxyActor` | 一颗可着陆的球形天体，承载重力、生物群系、大气、风暴 |
| ③ 体素层 | `UVoxelVolumeComponent` + `Terrain2` 模块 | **GPU 光线步进的虚拟化分页 SDF 体素**，玩家挖/填的就是它 |
| ④ 程序化层 | `UProceduralExpression_*` 表达式图 | 用 SDF 表达式图 + 噪声 + 生物群系权重“算出”第 ③ 层的初始形状 |

**最关键的两个事实（决定了能不能在 Godot 里复刻）：**

1. **地形不是网格（mesh），是 SDF 体素场 + GPU 光线步进。**
   `FVoxelVirtualRaymarchingCS`、`FVoxelRasterComputeCS`、`VoxelPageTexture`、`PageTable` 这些名字说明：
   地形以**分页（page）的 3D 纹理**存储（虚拟纹理思路），渲染时用 compute shader 对 SDF 做 ray marching，
   而不是每帧生成 marching cubes 网格。分页 + 页表 = 稀疏虚拟体素（只有玩家碰过的区域才分配物理页）。

2. **网络同步的不是体素，是“修改操作”。**
   `UVoxelVolumeModifier::Apply_AnyThreadConcurrent` + `EDeformType` + `AstroSaveChunk`
   说明：地形改动被抽象为**有界的修改器（BoundedVoxelVolumeModifier）**，存档只存**相对程序化基线的差分**。
   这正好是多人联机可用的模型：**同步 deform 操作而非体素**，所有人确定性重放。

---

## 1. 安装体结构

```
Astro.exe                                  414 KB   启动器
Astro/Binaries/Win64/Astro-Win64-Shipping.exe  110 MB  ★ 全部 C++ 逻辑
Astro/Binaries/Win64/Astro-Win64-Shipping.exe.bak 111 MB  ← 备份，与当前 exe 不一致（见 §7）
Astro/Content/Paks/pakchunk0-WindowsNoEditor.pak  2.87 GB  ★ 全部资产
Engine/...                                  UE4 引擎（D3D12/Vulkan RHI、Slate、UMG…）
Astro/Plugins/Wwise/...                     Audiokinetic Wwise 音频
```

- Pak：UE4 `magic=0x5A6F12E1`，版本 **11**，索引位于 `size-204`，条目 **46,326** 个，**索引已 frozen/压缩**（无明文路径表）。
  内容需 FModel / UE4SS 之类工具才能列出；本报告不依赖 pak 内容。
- exe 内嵌**完整源码路径**：`C:/dev/cosmo/Unreal.Astro/Source/Runtime/Terrain2/...`、
  `.../Source/Astro/Private/Planet/PlanetStatics.cpp`、`.../Private/Physics/CustomGravityVolumeComponent.cpp`。
  自定义模块：**`Astro`（游戏）+ `Terrain2`（体素地形运行时）**。

---

## 2. 星系层（`USolarSystem`）

```
USolarSystem
 ├─ SolarBodies : TArray<ASolarBody>            // 全部天体
 ├─ SolarSystemSpeed / SolarTimeScaleMultiplierCreative
 ├─ SolarSystemViewRadius                        // 星图视野半径
 └─ SolarSystemChambersRequiredForEngineActivation

ASolarBody
 ├─ ESolarBodyIdentifier : {None, Planet, OrbitalPlatform, GateStation, SolarControlRoom}
 ├─ SolarBodyRelativeTransforms                  // 相对恒星的位置
 ├─ OrbitAxis / OrbitDistance / OrbitAngle / OrbitPhase
 ├─ OrbitFrequencyBase+Range / OrbitOffsetBase+Range / OrbitPhaseBase+Range
 ├─ OrbitDirection / OrbitalMass / OrbitModules
 ├─ SolarVelocity / SunDirectionRelativeToSolarBody
 └─ GravityRadius / GravityStrength / GravityType
```

设计要点：
- **天体 = 数据驱动**：轨道用 `Base + Range` 的随机化参数（`OrbitFrequencyBase`/`Range`、`OrbitPhaseBase`/`Range`），
  每个存档 seed 决定具体轨道。行星公转由 `SolarSystemSpeed` 统一驱动。
- **恒星是光源**：`SunDirectionRelativeToSolarBody`、`USolarPowerComponent`、`ESolarGeneratorSunTrackingBehavior`
  （LocalPitch/Roll/Yaw Axis, Pivot）说明太阳能板朝恒星自动转向。
- **轨道平台 / 传送门是特殊 SolarBody**：`AOrbitalPlatform`、`GateStation`、`ASolarControlRoom`
  —— 它们不是行星，但挂在同一套天体系统里，可被 `GetUniqueAnchorOfTypeInSolarSystem` 查询。
- 玩家有 `CurrentSolarBody` / `LocalSolarBody`（`OnLocalSolarBodyChanged`），
  即“我现在在哪个天体上”，用于重力、大气、光照的切换。

---

## 3. 行星层（`AAstroPlanet`）

```
AAstroPlanet
 ├─ PlanetIdentifier : EPlanetIdentifier
 │     {None, Terran, TerranMoon, Arid, Exotic, ExoticMoon,
 │      Tundra, Radiated, GlitchPlanet, GlitchMoon, OrbitalPlatform}
 ├─ PlanetRadius / PlanetRadiusKm
 ├─ PlanetUp / PlanetUpComponent                  // “上”方向（球面法线）
 ├─ PlanetType / PlanetTag / PlanetDisplayName
 ├─ PlanetMeshDefinition                          // 远景观感网格
 ├─ PlanetProxyActor                              // ★ LOD 代理：远处用低模代理渲染
 ├─ PlanetOptimization : {None, Low, High}        // 动态优化档
 ├─ PlanetaryVoxelVolume                          // ★ 该行星的体素体积
 ├─ PlanetEffect / PlanetMusic / PlanetMarker
 └─ (程序化) BiomeWeight / Height                 // 见 §4
```

- **球形世界**：行星是一个球，`PlanetUp` 就是球面法线；玩家在球面上“站立”，重力指向球心（§5）。
- **双层表现**：近处是完整体素地形（`PlanetaryVoxelVolume`），远处换成 `PlanetProxyActor` 低模 —— 这就是
  `EPlanetOptimization` 与 `DynamicPlanetOptimization` 的用途。**实现无限宇宙必须复刻这个 LOD 切换**。
- 行星半径量级：Astroneer 的行星约 **几百米半径**（`PlanetRadiusKm` 字段用于星图显示的真实公里数）。
  体素世界坐标用 `double` 会溢出，所以用“相对行星的局部坐标 + 行星原点”的**双精度原点（floating origin）**方案。

---

## 4. 程序化生成层（`UProceduralExpression_*`）

这是 Astroneer 地形的“算法”，是一张 **SDF 表达式图**（不是噪声堆叠）：

```
UProceduralExpression (基类)
 ├─ 输入/常量:  Constant, Parameter, CartesianToSpherical, Height, BiomeWeight
 ├─ 数学:        Add, Subtract, Multiply, Reciprocal, Sqrt, Clamp, Saturate,
 │               Lerp, Remap, Min, Max, Levels, Sin, Cos,
 │               SmoothStep, SmootherStep, Select, Compare(EProceduralExpressionComparison)
 ├─ 噪声:        Noise (ENoiseType/Dimension/Fractal/Interpolation/Perturb)
 ├─ SDF 原语:    PrimitiveShape
 ├─ SDF 布尔:    SdfUnion, SdfIntersect, SdfDifference, SdfDisplace, SdfToDensity
 ├─ 放置:        ProceduralPlacement, ModifierOutput, SubModifier, OutputBase
 └─ 注释:        Comment
```

**含义**：地形 = `SdfToDensity( SDF 表达式图(球面坐标, 噪声, 生物群系权重) )`。
- `CartesianToSpherical` → 把体素位置转成球面坐标，喂给噪声 → 得到**球面高度场**。
- `BiomeWeight` → 输出当前点的生物群系权重，`Lerp/Select` 按权重混合不同生物群系的形状。
- `SdfDisplace` → 用噪声位移 SDF 表面（做起伏）。
- `SdfToDensity` → 把 SDF 距离转成体素密度（供 raymarch 与挖填使用）。
- `ModifierOutput` / `SubModifier` → **挖填修改器的注入点**（玩家改动覆盖基线）。

生物群系（`UBiomeSamplerComponent`）：

```
BiomeLayer : {Core, Mantle, Crust, Surface, Underground}   // 径向分层
BiomeWeight / BiomeEntryWeightThreshold / BiomeExitWeightThreshold
BiomeTagToVoxelStringIds    // 群系标签 → 体素材质 id
SurfaceBiomeLow/HighWeightCutoff, MinimumBiomeWeight...
```

行星从内到外分层：**Core（核）→ Mantle（幔）→ Crust（壳）→ Surface（表）→ 洞穴层**，
每层按权重混入不同群系（Terran/Arid/Exotic/Tundra/Radiated…），群系再决定体素材质与植被/资源放置。

---

## 5. 体素层（`Terrain2` 模块，本作的核心）

```
UVoxelVolumeComponent                     // 行星上的体素体积实例
 ├─ HighestDetailVoxelSize                // 最高细节体素边长
 ├─ VoxelWorldSize / VoxelResolution      // 体积世界尺寸 / 分辨率
 ├─ VoxelMinAABB / VoxelMaxAABB
 ├─ PageWorldSize / PageSize / PageResolution / PageCountResolution
 ├─ PageTexture / PageTable / PageTableIndirection     // ★ 分页虚拟体素
 ├─ CPU_PageIndexResolution / DispatchedPageIndexResolution
 ├─ InitialChunkSize / TotalChunkSize / MaxTotalPageIndexCount
 ├─ VoxelMaterial / VoxelMaterialProperties / PackedVoxelMaterialInfo
 └─ IsPointInsideVoxelVolume / RaycastVoxels / GetVoxelVolumeComponent

渲染/生成 (compute):
  FVoxelRasterComputeCS              // 把修改器光栅化进体素页
  FVoxelVirtualRaymarchingCS         // ★ 渲染：对 SDF 光线步进
  FVoxelAllocateVoxelPageCS          // 稀疏页分配
  FVoxelMarkValidPageIndexCS         // 有效页标记
  FVoxelIndPageClearCS / ...BufferGenCS
  FVoxelizeVolumeVS/PS/GS            // 体积体素化

修改器:
  UVoxelVolumeModifier::Apply_AnyThreadConcurrent   // ★ 线程并发应用修改
  UBoundedVoxelVolumeModifier / UBoundedVoxelVolumeModifierList
  UVoxelVolumeMaterialPalette / UVoxelVolumePaintModePalette

挖掘/建造:
  EDeformType : {Add, Subtract, Flatten, FlattenAddOnly, FlattenSubtractOnly,
                 ColorPaint, ColorPick, Crater, PlatformSurface, TrueFlatStamp,
                 RevertModifications}
  EDeformShape: {Sphere, Box}
  DeformationParamsT2 / DeformRadius / DeformTier / DeformationTier
  ADeformTool / DeformEffectComponent / DeformEventReceiverComponent
  TerrainHardness / AnalyzedTerrainMaterialSample / AugmentedTerrainHardness
```

设计要点：
- **稀疏虚拟体素**：只有被改动/靠近玩家的区域才分配 `PageTexture` 物理页，页表（`PageTable`）做间接寻址。
  这既是**内存优化**（整颗行星的体素不可能常驻显存），也是**无限/大地形的关键**。
- **SDF + Raymarching 渲染**：不生成三角形网格，直接对 SDF 场 ray march。好处：挖填即时、无重建网格开销、
  无缝 LOD。坏处：需要 compute shader，且碰撞要单独做（`RaycastVoxels`、`IsPointInsideVoxelVolume` 提供 CPU 侧查询）。
- **修改器并发应用**：`Apply_AnyThreadConcurrent` —— 挖填可在任意线程并发写体素，说明修改器是**幂等/可交换的有界算子**。
- **硬度分级**：`DeformTier` / `DeformationTier` / `TerrainHardness` —— 工具等级不够挖不动硬岩，是玩法门槛。
- **沉积物（sediment）**：`EDeformToolSedimentTankReservesState`、`AdjustSedimentTankAmountFromDeformationDelta`、
  `ConvertDeformationDeltaToSedimentAmount` —— 挖出的土进入背包“土罐”，填土消耗土。**这是可建造玩法的资源闭环。**

---

## 6. 重力 / 大气 / 风暴

```
重力:
  UCustomGravityVolumeComponent (基类)
   ├─ USphericalGravityVolumeComponent     // 球形（行星本体）
   ├─ UCylindricalGravityVolumeComponent   // 柱形（空间站/环）
   └─ UFixedGravityVolumeComponent         // 固定方向（轨道平台）
  GeneratePlanetGravitySources / GetBodyGravityAtLocation
  GravityRadius / GravityStrength / GravityScale / GravityVector
  CustomGravityScaler / GravitySourceInterpolationTime

大气 / 危险:
  AtmosphereHeight / AtmosphericFog...
  AAstroStorm / StormSpawnConfiguration / StormMutualExclusionRadius
  PerPlanetStormStates / IsBossStorm / StormEncumbrance(风暴负重)
```

- 重力是**体积（volume）驱动的**：玩家进入哪个重力体积，就受哪种重力。行星 = 球形重力，指向球心。
- 重力有 `GravitySourceInterpolationTime`，切换天体时平滑过渡。
- 风暴是**行星级事件**，有生成点候选、互斥半径、负重（把玩家吹飞）。

---

## 7. 网络 / 存档 / 反作弊

```
网络 (UE4 Replication Graph 扩展):
  UAstroReplicationGraph
   ├─ UAstroReplicationGraphNode_GridSpatialization3D   // ★ 3D 空间网格复制
   ├─ UAstroReplicationGraphNode_GridCell
   ├─ UAstroReplicationGraphNode_AlwaysRelevant_ForConnection
   ├─ UAstroReplicationGraphNode_ActorListFrequencyBuckets  // 频率分桶(按距离降频)
   └─ UAstroReplicationGraphNode_PlayerStateFrequencyLimiter
  大量 Authority* 函数（700+）→ 服务器权威（Server Authoritative）

存档:
  UAstroSave / UAstroSaveAsset / UAstroSaveCustomArchiveProxy
  AstroSaveChunk / AstroSaveFileInformation
  UActorSaveComponent / ProceduralPlacementSaveID
  → 世界按空间分块存“相对程序化基线的差分”
```

- **复制图 = 3D 空间网格**：只同步玩家附近的 actor，按距离分频。这是大型多人可扩展的关键。
- **存档 = 差分**：`AstroSaveChunk` + `ProceduralPlacementSaveID` —— 程序化世界只存**玩家改动的部分**，
  其余靠 seed 重算。**这是“无限世界可存档”的根本原因。**
- **权威模型**：原版是**服务器权威**（所有 `Authority*` 在服务器跑，客户端只发输入）。
  注意：这不等于“客户端权威”，Astroneer 的联机是 host 权威。

### 7.1 关于被修改的 exe
`Astro-Win64-Shipping.exe` 与 `Astro-Win64-Shipping.exe.bak` **不一致**（md5 不同、体积差 234 KB）。
当前 exe 极可能是**被 mod / 汉化 / 破解工具打过补丁**的版本。**本报告结论取自当前 exe 的反射元数据，
若需纯净基线应改分析 `.bak`。** 这一点影响“分析对象可信度”，特此标注。

---

## 8. 与 Godot 复刻的差距（诚实评估）

| Astroneer 做法 | Godot 4.7 可行性 | 建议 |
|---|---|---|
| GPU raymarch 虚拟 SDF 体素 | 需 `RenderingDevice` compute + 自定义 shader，**可做但工作量巨大** | 先用 CPU marching cubes 出可玩原型，再逐步 GPU 化 |
| 稀疏分页体素 | 需自建页表/页分配 | CPU 端用 `Dictionary<pageKey, Chunk>` 稀疏存储 |
| 表达式图程序化 | 可移植为资源（`Resource`）驱动的节点图 | 用 `Resource` 定义 SDF 表达式树 |
| 球形行星 + floating origin | Godot 无原生支持，需自己实现 | 行星局部坐标系 + `double` 原点 |
| Replication Graph | Godot 无，需自建（且 C# 负责） | 自建 3D 网格复制（本设计第 3 节） |
| 差分存档 | 可直接做 | 只存 chunk 的 deform 操作日志 |

**结论**：可以在 Godot 里做到“同样类型的玩法”（球形可挖行星 + 无限宇宙 + 建造），
但**渲染管线必须换**：Godot 没有 Astroneer 那套自研 GPU raymarcher，代价最小且效果可接受的是
**分块 marching cubes 网格 + 稀疏差分存档**。详见 `docs/design/universe-engine-design.md`。
