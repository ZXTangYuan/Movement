# 无限可破坏宇宙引擎 —— Godot 4.7 设计文档

> 目标：在本项目（3D / C#+GDScript 混用 / 联机）中，实现 Astroneer 那样的
> **程序化生成的星球 + 完全可破坏可建造地形 + 可扩展多人**。
> 逆向依据见 [`../reference/astroneer-reverse-engineering.md`](../reference/astroneer-reverse-engineering.md)。

> 📌 **阅读提示**：本文是**设计蓝图**（含早期规划）。**已实现的架构与实测数据**请以
> [`DEV-STAGE-1-SUMMARY.md`](DEV-STAGE-1-SUMMARY.md)（阶段总结）与
> [`raymarch-implementation.md`](raymarch-implementation.md)（地形实现细节）为准。
> 文中 §3.2 / §4 的运行时结构是**早期方案**，实现时已演进（例如体积改为解析基线 + delta 窗口）。

---

## 0. 硬约束（来自用户）

| 约束 | 决定 |
|---|---|
| 3D | `Node3D`，Forward+，Jolt 物理 |
| C# + GDScript 混用 | **C# 负责网络核心**；**GDScript 负责玩法**（地形、行星、玩家、建造） |
| 联机：好友 → 客户端权威 | 房主托管，客户端可改世界并广播，低延迟、零成本 |
| 联机：大型 → 专用服务器 | 服务器权威，校验改动，防作弊 |
| 权威模式可切换 | **同一套代码，`AuthorityMode` 枚举切换**，不是两套实现 |

**核心设计决策**：无论哪种权威模式，**网络上同步的都是"地形编辑操作（edit op）"，不是体素本身**。
这一条同时解决了带宽、存档、确定性和双权威模式四个问题。

---

## 1. 架构总览

```
┌─────────────────────────────────────────────────────────────┐
│  C#  NetworkCore (autoload)  —— 网络核心，不含玩法           │
│   ├─ Transport (ENet / WebSocket / 本地回环)                  │
│   ├─ AuthorityMode { ClientAuthoritative, ServerAuthoritative }│
│   ├─ InterestGrid  3D 空间兴趣网格（只同步玩家附近）           │
│   ├─ ReplicationRegistry  玩法侧注册"要同步什么"               │
│   └─ Messages: TerrainEditMsg / PlayerStateMsg / ...          │
├─────────────────────────────────────────────────────────────┤
│  GDScript  Gameplay  —— 玩法，通过 NetworkCore 的 API 通信     │
│   ├─ SolarSystem → Planet → VoxelWorld → VoxelChunk            │
│   ├─ ProceduralGenerator (SDF 表达式树)                        │
│   ├─ DeformBrush (挖/填/平整/上色)                             │
│   ├─ GravityVolume                                             │
│   └─ Player / Buildable                                        │
├─────────────────────────────────────────────────────────────┤
│  Data  Resources  —— 纯数据，无逻辑                            │
│   ├─ PlanetDefinition / BiomeDefinition / SdfExpression        │
│   └─ VoxelMaterialPalette / DeformToolDefinition               │
└─────────────────────────────────────────────────────────────┘
```

**边界契约（最重要）**：
- GDScript 玩法代码**永不**直接碰 socket / peer / RPC 底层。它只调 `NetworkCore` 的强类型 API：
  - `NetworkCore.Instance.Host(mode, port)` / `Join(addr)` / `Disconnect()`
  - `NetworkCore.Instance.BroadcastEdit(edit)` —— 请求一次地形编辑
  - `NetworkCore.Instance.OnEditReceived` —— 收到别人的编辑（信号）
  - `NetworkCore.Instance.IsAuthorityFor(planetId, regionKey)` —— 我是否有权直接改
- C# 网络代码**永不**知道"体素""行星"是什么。它只搬运 `IMessage`。
- 这条边界让"玩法"和"网络"可以各自独立测试、独立替换。

---

## 2. 坐标系统与浮动原点（Floating Origin）

球面行星 + 宇宙尺度必须解决浮点精度。Astroneer 用"相对行星的局部坐标"。

```
SolarSystemSpace   : 用 double 表示天体轨道位置（银河/星系尺度）
   │
   └─ PlanetSpace   : 每颗行星一个局部坐标系，原点在行星质心
         │  位置 = Vector3 (float32)，行星半径 ≤ ~600m，
         │  float32 在 600m 处精度 ≈ 6e-5 m（0.06 mm）—— 足够
         └─ 玩家/体素/建筑都用 PlanetSpace 坐标
```

- 玩家跨行星旅行：切换 `PlanetSpace`，用 `GravitySourceInterpolationTime` 平滑过渡重力。
- 渲染：只在必要时把 `PlanetSpace` 坐标搬到相机附近（大型载具/轨道平台）。
- **Godot 实现**：`Planet` 节点本身放在世界原点附近即可；真正的"宇宙尺度"由星图 UI 用 double 表达，
  不需要真的把行星放在 10^9 米外。**这是省掉 90% 浮动原点复杂度的关键取巧。**

---

## 3. 星系与行星

### 3.1 数据（Resource）
```gdscript
class_name PlanetDefinition extends Resource
@export var id: StringName                    # "terran"
@export var display_name: String
@export var radius: float = 256.0             # 米
@export var seed: int = 0
@export var gravity_strength: float = 9.8
@export var atmosphere_height: float = 120.0
@export var biome_layers: Array[BiomeLayer]   # Core→Mantle→Crust→Surface
@export var surface_sdf: SdfExpression        # 程序化形状
@export var voxel_material_palette: VoxelMaterialPalette
```

### 3.2 运行时
```
SolarSystem (Node3D)
 ├─ star: DirectionalLight3D              # 恒星光照方向
 ├─ planets: Array[Planet]
 └─ _process: 按轨道参数推进各行星位置（星图用）

Planet (Node3D)
 ├─ definition: PlanetDefinition
 ├─ gravity_volume: GravityVolume         # 球形重力，指向质心
 ├─ voxel_world: VoxelWorld               # 体素地形（§4）
 └─ lod_proxy: MeshInstance3D             # 远处低模代理（对应 Astroneer PlanetProxyActor）
```

- **LOD 切换**：玩家距离 > `proxy_distance` 时，隐藏 `voxel_world`、显示 `lod_proxy`（球体低模）。
  这是"无限宇宙"的性能底线 —— 不可能所有行星都跑体素。
- 重力：`GravityVolume` 提供 `get_gravity_at(local_pos) -> Vector3`（= 指向质心的单位向量 × 强度）。

---

## 4. 体素地形（核心系统）

### 4.1 存储模型（决定一切）

```
密度场 density(p) : 解析函数（程序化 SDF），不存储
     │
     ├─ Baseline  程序化基线：由 (planet_seed, p) 确定性算出，永不存储
     └─ Delta     差分：玩家挖填操作，稀疏存储
                    Dictionary[ChunkKey, EditList]

最终密度 = Baseline(p) + Σ Delta(edit)(p)
```

**为什么这样**：Astroneer 的 `AstroSaveChunk` + `ProceduralPlacementSaveID` 就是这个模型。
- 存档只存"玩家改了什么"，一个玩了 100 小时的世界存档可能只有几 MB。
- 世界"无限"是因为基线永远能按需重算。
- 联机只同步 Delta（编辑操作），不同步体素 —— 带宽恒定，与地形复杂度无关。

### 4.2 分块与流式加载
```gdscript
class_name VoxelChunk
const SIZE := 32                          # 32³ 体素
var key: Vector3i                         # 块坐标
var density: PackedFloat32Array           # (SIZE+1)³ 密度样本
var mesh: ArrayMesh                       # Surface Nets 生成的网格
```

- 玩家周围 `render_distance` 内的块按需生成（后台线程）。
- **只对有零交叉（表面穿过）的块建网格** —— 球壳外/内的块被自然剔除。
- 内存：稀疏 `Dictionary`；超出范围的块释放（但 Delta 保留在存档里）。

### 4.3 网格生成：Surface Nets（而非 Marching Cubes）
选 Surface Nets 的理由：**代码量 1/10、结果同样平滑**，且天然生成四边形。
（Marching Cubes 的 256 项三角表约 4000 个数字，SN 只需一个顶点缓冲。）

算法：
1. 对每个 cell（2×2×2 角点），若符号变化 → 在边的交点平均处放一个顶点。
2. 对每条符号变化的边，连接其相邻 4 个 cell 的顶点成一个 quad。
3. 顶点法线用密度梯度（中心差分）—— 免费得到平滑着色。

### 4.4 可破坏 / 可建造（DeformBrush）
```gdscript
class_name TerrainEdit extends Resource
enum Type { ADD, SUBTRACT, FLATTEN, PAINT }
var type: Type
var shape: int          # SPHERE / BOX
var position: Vector3   # PlanetSpace
var radius: float
var material: int
```

- 挖（SUBTRACT）= 球/盒 SDF 从密度场减去；填（ADD）= 加上。
- 修改只写进 Delta（§4.1），然后**重建受影响块**（`ceil(radius/chunk_world_size)+1` 个块）。
- 对应 Astroneer 的 `EDeformType`：`Add/Subtract/Flatten/ColorPaint/Crater/PlatformSurface`。
- **土罐闭环**（Astroneer 的 sediment）：挖出的体积进背包，填消耗 —— 可建造玩法的资源约束。

---

## 5. 程序化生成（SDF 表达式树）

对应 Astroneer 的 `UProceduralExpression_*`。用 `Resource` 表达表达式树：

```gdscript
class_name SdfExpression extends Resource
@export var op: Op        # CONSTANT, NOISE, ADD, MUL, LERP, CLAMP, SPHERE, UNION...
@export var children: Array[SdfExpression]
@export var params: Dictionary

func evaluate(p: Vector3) -> float:  # 返回 SDF 距离（或密度）
```

基础表达式集（对齐 Astroneer 的算子表）：
`Constant, Parameter, Noise, Add, Subtract, Multiply, Lerp, Remap, Clamp, Saturate, Min, Max, SmoothStep, Sin, Cos, Select, CartesianToSpherical, SdfUnion, SdfIntersect, SdfDifference, SdfDisplace, SdfToDensity, PrimitiveShape`

球面行星的典型图：
```
density(p) = SdfToDensity(
    SdfDisplace(
        Sphere(radius) ,                                  # 基础球
        Noise(p * freq) * amplitude * BiomeAmplitude(p)   # 群系调制的起伏
    )
)
```
`CartesianToSpherical(p)` → 得到 (lat, lon, r)，噪声用球面坐标采样 → **无接缝的球面地形**。

生物群系（`BiomeSampler`）：按纬度 + 另一套噪声得到 `weight`，
`Lerp/Select` 在多个群系的形状/材质间混合 —— 对应 Astroneer 的 `UBiomeSamplerComponent`。

---

## 6. 网络设计（C# 核心）

### 6.1 权威模式
```csharp
public enum AuthorityMode {
    ClientAuthoritative,   // 好友联机：房主 + 客户端都能改世界
    ServerAuthoritative,   // 专用服务器：只有服务器改世界
}
```

| | ClientAuthoritative | ServerAuthoritative |
|---|---|---|
| 谁跑世界模拟 | 每个客户端各跑一份 | 只有服务器 |
| 地形编辑 | 本地立即生效 + 广播 | 发请求 → 服务器校验 → 广播 |
| 延迟手感 | 0（本地先做） | 有 RTT（可客户端预测） |
| 防作弊 | 无 | 有 |
| 适用 | 好友（≤4-8 人） | 大型公开服 |

**同一套 `NetworkCore`**，区别只在：
- `ClientAuthoritative`：`BroadcastEdit` 直接本地 apply 并广播；收到别人的 edit 直接 apply。
- `ServerAuthoritative`：`BroadcastEdit` 发到服务器；服务器 `Validate(edit)` 通过后广播；客户端收到后 apply。

### 6.2 地形编辑同步（协议核心）
```
TerrainEditMsg {
    int   PlanetId
    byte  EditType        // ADD/SUBTRACT/FLATTEN/PAINT
    byte  Shape           // SPHERE/BOX
    float3 Position       // PlanetSpace
    float Radius
    int   Material
    uint  Sequence        // 幂等/排序
    int   OriginPeerId
}
```
- **确定性地形**：所有 peer 用同一 `planet_seed` + 同一 edit 序列 → 结果必然一致。
- **兴趣管理**：`InterestGrid`（3D 网格，对应 Astroneer 的 `GridSpatialization3D`）只把 edit
  发给"该区域附近"的 peer。远处玩家的挖掘不同步给他 —— 这就是可扩展性来源。
- **新玩家加入（JIP）**：服务器/房主把目标区域的 Delta 汇总发过去（对应 Astroneer 的 `JIPPackageSent_Terrain2`）。

### 6.3 C# 侧组件
```
NetworkCore.cs        (autoload Node)  —— 门面：Host/Join/Broadcast/事件
AuthorityMode.cs
ITransport.cs + ENetTransport.cs       —— 传输抽象（Godot 用 ENetMultiplayerPeer 包装）
ReplicationRegistry.cs                 —— 玩法注册要同步的类型
InterestGrid.cs                        —— 3D 空间网格，兴趣管理
Messages/IMessage.cs + TerrainEditMsg.cs
```

### 6.4 GDScript 侧用法
```gdscript
func _ready():
    NetworkCore.instance.edit_received.connect(_on_remote_edit)

func dig(pos: Vector3, radius: float):
    var edit := TerrainEdit.new()
    edit.type = TerrainEdit.Type.SUBTRACT
    edit.position = pos
    edit.radius = radius
    voxel_world.apply_edit(edit)          # 本地立即生效
    NetworkCore.instance.broadcast_edit(edit.to_net())   # 交给 C# 同步

func _on_remote_edit(planet_id: int, msg: Dictionary):
    if planet_id == planet.definition.id_hash:
        voxel_world.apply_edit(TerrainEdit.from_net(msg))
```
玩法代码只有这几行，网络细节全在 C# 里。

---

## 7. 存档

```
SaveFile
 ├─ seed / planet_ids
 ├─ for each planet: Dictionary[ChunkKey, EditList]   # 只存差分
 ├─ for each planet: placed_buildables[]
 └─ player_state[]
```
- 与 Astroneer 的 `AstroSaveChunk` 同构。
- 世界可"无限"，存档随游玩线性增长，且可压缩（edit 可合并）。

---

## 8. 实施路线与进度（阶段一已完成）

| 阶段 | 交付 | 状态 |
|---|---|---|
| **P0** | C# 工程 + `NetworkCore`（模式/传输/消息） | ✅ |
| **P1** | 球形行星 + 体素流式 + Surface Nets（现为 MESH 回退） | ✅ |
| **P1.5** | **GPU raymarch 无限地形**（解析基线 + delta 窗口） | ✅ |
| **P2** | 玩家 + 球面重力 + 挖/填（SDF 碰撞） | ✅ |
| **P2.5** | **太阳系**：恒星 + 八大行星 + 每行星重力场 + 镜头双模式 | ✅ |
| **P3** | SDF 表达式树 + 生物群系 + 多行星**可着陆** | ⬜ 部分（多行星为远景代理） |
| **P4** | 网络编辑同步 + 兴趣网格 + 双权威模式 | ⬜ 代码就绪，未双机实测 |
| **P5** | 差分存档 + JIP + 建造物 | ⬜ 存档往返已实现，其余未做 |

阶段一总结见 [`DEV-STAGE-1-SUMMARY.md`](DEV-STAGE-1-SUMMARY.md)。

---

## 9. 与原版的取舍（诚实说明）

| 原版 | 本项目 | 说明 |
|---|---|---|
| GPU raymarch 虚拟分页 SDF | **GPU raymarch（解析基线 + delta 窗口）** | 已实现；未做稀疏分页（稠密窗口够用） |
| 服务器权威 | **双模式可切换**（客户端权威 / 服务器权威） | 好友低延迟 + 大型防作弊 |
| 自研 Replication Graph | **自建 3D 兴趣网格** | Godot 无对应设施 |
| 行星半径 ~600m | 半径 320~1050（八行星按比例缩放） | 玩法一致 |
| 无网格碰撞（查 SDF） | SDF 梯度推挤（近似） | 高速下落可能穿透，待改连续碰撞 |

**可以达到的**：球形可挖行星、程序化宇宙、多人共建、无限（程序化）世界、差分存档。
**与原版的差距**：未做稀疏分页（内存/带宽）、碰撞是近似而非精确、无音频/美术资产。

---

## 10. 渲染路线决策（已落地）

最初判断"Godot 无 raymarch，只能退回 Surface Nets"。**该判断已被实测推翻并完成实现**。
实现细节见 [`raymarch-implementation.md`](raymarch-implementation.md)。

**插件调研结论**：**没有**现成可用的 Godot 4 raymarch 插件。
[Zylann/godot_voxel](https://github.com/Zylann/godot_voxel) 是成熟的**网格**体素库（Transvoxel），
但**不是 raymarch**（与 Astroneer 观感不同）；Godot 4 的 SDF raymarch 插件为空缺。
→ 因此**自研** raymarch，未引入第三方插件。

**已实现**：
- `voxel/VoxelTerrainCore.cs` —— C# 权威数据 + `Parallel.For` **动态多核**（实测 96³ 24 ms，比 GDScript 快 30×）
- `voxel/voxel_volume.gd` —— delta 窗口 + `RenderingDevice.texture_update` 上传 3D 纹理（R32F）
- `shaders/raymarch_terrain.gdshader` —— 解析基线 + `sampler3D` delta，光线步进写 `DEPTH`
- `planet/Planet` 三模式：`RAYMARCH`（默认）/ `MESH`（Surface Nets 回退）/ `PROXY_ONLY`
- 编辑全链路：`TerrainEdit` → C# `RasterizeDeltaWindow` → 纹理上传，**零网格重建**

**实测**：玩家场景 1280×720 约 12 ms（83 fps），1 个 draw call；单次挖掘 2.5 ms。

**关键发现**：主 RenderingDevice **不允许手动 submit/sync**，compute 写纹理这条路走不通；
改用「CPU 多核计算 + `texture_update` 上传」，反而更契合「动态多核分配」的需求。

**剩余工作**：① 连续碰撞（当前为 SDF 梯度推挤）；② 稀疏分页局部更新；③ 星际旅行。

> 以上世界系统代码现位于 **`UnrealWorld` 插件**（submodule 于 `addons/unreal_world/`）。
> 原 P1 的 CPU Surface Nets 管线保留为 `MESH` 回退模式。
