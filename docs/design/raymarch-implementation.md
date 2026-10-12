# GPU Raymarch 体素地形 —— 实现说明（已落地）

> 本文记录 **已实现** 的 GPU raymarch 体素地形，以及实现过程中实测到的 Godot 陷阱。
> 可行性论证见 [`gpu-raymarch-feasibility.md`](gpu-raymarch-feasibility.md)；总体设计见 [`universe-engine-design.md`](universe-engine-design.md)。

---

## 1. 架构

```
┌──────────────────────────────────────────────────────────────┐
│ GDScript 玩法层                                               │
│   Planet (render_mode: RAYMARCH / MESH / PROXY_ONLY)          │
│     ├─ VoxelVolume      —— 数据 + 生命周期 + 纹理上传          │
│     └─ RaymarchTerrain  —— 纯渲染（MeshInstance3D + 材质）     │
├──────────────────────────────────────────────────────────────┤
│ C# 性能层（动态多核）                                          │
│   VoxelDensity : RefCounted                                   │
│     ├─ Build(...)      —— Parallel.For 按 Z 切片生成密度       │
│     ├─ ApplyEdit(...)  —— Parallel.For 只重算受影响切片        │
│     ├─ GetBytes()      —— 供上传 3D 纹理                       │
│     └─ SampleDensity() —— CPU 射线查询                        │
├──────────────────────────────────────────────────────────────┤
│ GPU 渲染层                                                     │
│   raymarch_terrain.gdshader : sampler3D 光线步进 + 写 DEPTH    │
│   Texture3DRD (R32F)  ← 由 RenderingDevice.texture_update 更新 │
└──────────────────────────────────────────────────────────────┘
```

**数据流（一次挖掘）**：
```
玩家 dig() → TerrainEdit（网络消息）
  → VoxelVolume.apply_edit()
      → C# VoxelDensity.ApplyEdit()      [Parallel.For 重算受影响切片, ~3ms]
      → RenderingDevice.texture_update()  [上传 3D 纹理]
  → 下一帧 shader 采样到新密度 → 画面更新
  → NetworkCore.BroadcastEdit()           [同步给其他玩家]
```
**全程零网格重建** —— 没有顶点缓冲、没有碰撞体、没有 meshing。

---

## 2. 实测性能（本机 RTX 4070 Laptop / 20 核 / Vulkan）

| 指标 | 数值 |
|---|---|
| 体积生成 96³（88 万采样） | **24–27 ms**（C# 多核） |
| 体积生成 128³（210 万采样） | **39–64 ms** |
| 单次编辑（半径 5–10m） | **2.5–13 ms**（只重算受影响切片） |
| 主场景帧时（1280×720, raymarch） | **中位 0.67 ms（~1488 fps）** |
| draw call | **1** |

对照：GDScript 逐体素生成同样 96³ 需 **352 ms**（解释器开销），C# 多核快约 **30 倍**。

---

## 3. 实现中实测到的 Godot 陷阱（重要，都是踩过的坑）

### 3.1 `#[compute]` 指令不能出现在 `RDShaderSource`
`RDShaderSource.source_compute = "..."` 里**不要**写 `#[compute]` 和 `#version` 之外的头。
`#[compute]` 只用于 `.glsl` 文件被 `RDShaderFile` 加载时。写了会报 `'#' : invalid directive`。

### 3.2 主 RenderingDevice 不能手动 submit/sync
```gdscript
var rd := RenderingServer.get_rendering_device()
rd.submit()   # ✗ ERROR: Only local devices can submit and sync.
rd.sync()     # ✗
```
**主 RD** 由引擎管理提交时机。因此 compute-shader 写纹理这条路在主 RD 上走不通
（`RenderingServer.create_local_rendering_device()` 可以 submit，但那个设备与渲染器无关，
产出的纹理没法直接给材质采样）。
→ **本实现改为 CPU 计算 + `texture_update` 上传**，反而更适合「动态多核」的需求。

### 3.3 `texture_update` 可用，但需要正确的 usage bits
```gdscript
fmt.usage_bits = (TEXTURE_USAGE_SAMPLING_BIT
    | TEXTURE_USAGE_CAN_UPDATE_BIT
    | TEXTURE_USAGE_CAN_COPY_FROM_BIT)   # 回读需要 COPY_FROM
rd.texture_update(rid, 0, bytes)          # 主 RD 上可用 ✓
rd.texture_get_data(rid, 0)               # 需要 CAN_COPY_FROM_BIT
```

### 3.4 包装类名是 `Texture3DRD`（全大写 RD）
不是 `Texture3Drd`。`t.texture_rd_rid = rid` 即可让材质 `sampler3D` 采样。

### 3.5 PackedFloat32Array 传给 C# 是**值拷贝**（最坑）
```gdscript
# ✗ 完全无效：C# 写的是副本，GDScript 看不到任何变化（实测 512/512 元素未变）
var arr := PackedFloat32Array(); arr.resize(n)
DensityGenerator.Generate(arr, ...)   # C# 内部 arr[i] = ... 全部丢失
```
**解法：数据所有权放在 C#。** GDScript 不传数组，而是调用方法驱动；
需要数据时用 `GetBytes()` / `GetData()` 取回。

### 3.6 WorkerThreadPool 并行写入的正确姿势
- `add_group_task(action, elements, tasks_needed, ...)` **只有 5 个参数**，数据要靠 `Callable.bind()` 传。
- 回调**只收到 element 一个参数**（bind 的参数排在其后）。
- **不要取局部引用再写**：`var s = arr[z]; s[0] = 1` → COW 静默丢弃。
- **不要多线程 `Array.append`** → 有竞态（实测 8 个元素丢了 1 个）。
- **正确**：共享一个扁平 `PackedFloat32Array`，每个 worker 只写自己那段不相交索引区间。

### 3.7 headless 没有 RenderingDevice
`--headless` 用 dummy 渲染器，`RenderingServer.get_rendering_device()` 返回 `null`。
所以 GPU 测试必须带窗口或 `--rendering-driver vulkan`；
`Planet` 会自动检测并回退到 MESH 模式（已实现）。

### 3.8 噪声必须在**世界坐标**采样
用归一化方向采样噪声 → 整颗行星只落在一个噪声格子里 → 地形呈块状/台阶。
必须用米制世界坐标采样（本实现已修正）。

---

## 4. 为什么 CPU 侧用 C#（对应你的「动态多核」要求）

实测瓶颈不是噪声数学，而是 **GDScript 解释器逐体素调用原生函数的开销**：
- 纯数学单线程 88 万次：18 ms
- 纯噪声单线程 88 万次：352 ms（≈0.4 µs/采样）
- 纯数学多线程（20 核）：5.3 ms（3.4× 加速）
- 纯噪声多线程（GDScript）：260 ms（几乎无加速，被解释器开销淹没）

所以把密度内核移到 **C# + `Parallel.For`**：
- `Parallel.For` 由 .NET 线程池**按实际负载动态调度**（不是静态均分），核心数变化时自动适应；
- 实测 96³：352 ms → 24 ms；128³：39–64 ms。
- 单次编辑只重算受影响 Z 切片，2.5–13 ms。

---

## 5. 已知限制与后续

| 限制 | 说明 | 后续 |
|---|---|---|
| 体积是有限盒子 | 64m 盒外会看到 AABB 边界（demo 截图可见） | 体积跟随玩家移动 + 与解析基线混合 |
| 碰撞未做 | 无网格 → 不能用 ConcavePolygonShape3D | 自写 SDF raycast + 胶囊 SDF 求解 |
| 稠密而非稀疏分页 | 现在整块上传；Astroneer 用分页只传改动页 | 改为稀疏分页 + 局部 `texture_update` |
| 材质单一 | 只有一种土色 | 用密度梯度 + 群系权重着色 |

**下一步优先级**：① 体积跟随玩家（消除 AABB 边界）；② SDF 碰撞；③ 稀疏分页局部更新。

---

## 6. 相关文件

| 文件 | 作用 |
|---|---|
| `scripts/voxel/VoxelDensity.cs` | C# 密度内核（多核生成/编辑，数据所有权） |
| `scripts/voxel/voxel_volume.gd` | 体积生命周期 + 纹理上传 |
| `scripts/voxel/raymarch_terrain.gd` | raymarch 渲染节点 |
| `assets/shaders/raymarch_terrain.gdshader` | 光线步进着色器（采样 3D 纹理 + 写 DEPTH） |
| `scripts/planet/planet.gd` | 行星（RAYMARCH/MESH/PROXY 三模式切换） |
| `tests/headless_selftest_gpu.gd` | GPU 路径自检（正确性/确定性/编辑生效） |
| `tests/perf_scene.tscn` | 端到端性能基准 |
| `docs/reference/raymarch_terrain.png` | raymarch 地形截图（含挖掘的坑） |
| `docs/reference/bootstrap_shot.png` | 主场景截图 |
