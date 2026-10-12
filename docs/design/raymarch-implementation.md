# GPU Raymarch 体素地形 —— 实现说明

> 记录**已实现**的 GPU raymarch 无限地形，以及实现中实测到的 Godot 陷阱与性能数据。
> 总体设计见 [`universe-engine-design.md`](universe-engine-design.md)；
> 本阶段总览见 [`DEV-STAGE-1-SUMMARY.md`](DEV-STAGE-1-SUMMARY.md)。
>
> **位置说明**：本文涉及的世界系统代码现位于 **`UnrealWorld` 插件**
> （submodule 于 `addons/unreal_world/`）。

---

## 1. 架构（当前实现）

```
┌──────────────────────────────────────────────────────────────┐
│ GDScript 玩法层（插件）                                        │
│   Planet (render_mode: RAYMARCH / MESH / PROXY_ONLY)          │
│     ├─ VoxelVolume      —— delta 窗口 + 纹理上传               │
│     └─ RaymarchTerrain  —— 纯渲染（MeshInstance3D + 材质）     │
├──────────────────────────────────────────────────────────────┤
│ C# 性能层（动态多核，插件）                                     │
│   VoxelTerrainCore : RefCounted                               │
│     ├─ Baseline()            —— 解析基线（与 GLSL 逐位一致）    │
│     ├─ AddEdit()/SampleDensity() —— 稀疏编辑 + 精确查询        │
│     ├─ RasterizeDeltaWindow() —— Parallel.For 按 Z 切片光栅化  │
│     └─ GetEdits()/SetEdits()  —— 存档往返                     │
│   TerrainNoise（静态）        —— 基线噪声（与 GLSL 逐位一致）   │
├──────────────────────────────────────────────────────────────┤
│ GPU 渲染层（插件）                                             │
│   raymarch_terrain.gdshader : 解析基线 + sampler3D delta       │
│                              + 光线步进 + 写 DEPTH            │
│   Texture3DRD (R32F)  ← RenderingDevice.texture_update 更新    │
└──────────────────────────────────────────────────────────────┘
```

**数据流（一次挖掘）**：
```
玩家 dig() → TerrainEdit
  → VoxelVolume.apply_edit()
      → C# VoxelTerrainCore.AddEdit()          [记录稀疏编辑]
      → C# RasterizeDeltaWindow()              [Parallel.For 重算受影响切片, ~2.5ms]
      → RenderingDevice.texture_update()        [上传 delta 纹理]
  → 下一帧 shader 采样到新密度 → 画面更新
  → Planet.edit_created 信号 → 宿主决定是否 NetworkCore.BroadcastEdit
```
**全程零网格重建** —— 没有顶点缓冲、没有碰撞体、没有 meshing。

### 无限模型（与旧版的关键差异）

```
density(p) = baseline(p)  [解析，GLSL 求值，永不存储]
           + delta(p)     [玩家挖填，稀疏列表 → 窗口纹理]
```
基线解析求值 → **世界无边界**（旧版是有限 64m 盒子，会被 AABB 边界暴露）。

---

## 2. 实测性能（RTX 4070 Laptop / 20 核 / Vulkan）

| 指标 | 数值 |
|---|---|
| 玩家场景帧时（1280×720） | **~12 ms（83 fps）**，**1 个 draw call** |
| 单次挖掘（半径 3 m） | **2.5 ms**（零网格重建） |
| 地形窗口光栅化 96³ | C# 多核 24 ms（GDScript 需 352 ms，差 30×） |

**性能优化手段**：
- **天空像素早退**：相机在球壳内且射线向外 → `discard`，不跑 raymarch（64→92 fps）。
- 球壳裁剪：只在行星表面 ±(幅度×1.2) 内步进。
- 自适应步长 + 球壳入口起跳。
- **固定步长中心差分法线**（消除阶梯伪影）。

---

## 3. 实现中实测到的 Godot 陷阱（都踩过）

### 3.1 addon 子目录含 `project.godot` → 整个目录被忽略 🔴
Godot 会打印 `Detected another project.godot ... The folder will be ignored`，
导致该 addon 下所有 `class_name` 找不到。
**只有根目录的会触发**，子目录无害。
→ 插件仓库根**绝不能**放 `project.godot`。

### 3.2 `#[compute]` 指令不能出现在 `RDShaderSource`
`RDShaderSource.source_compute` 里不要写 `#[compute]`（那是 `.glsl` 文件用的）。
写了报 `'#' : invalid directive`。
另：GLSL 里 `>>` 必须用无符号字面量（`>> 8u`）。

### 3.3 主 RenderingDevice 不能手动 submit/sync
```gdscript
var rd := RenderingServer.get_rendering_device()
rd.submit()   # ✗ ERROR: Only local devices can submit and sync.
```
主 RD 由引擎管理提交时机 → compute 写纹理这条路在主 RD 上走不通。
→ **改用 CPU 多核计算 + `texture_update` 上传**（反而更契合动态多核需求）。

### 3.4 `texture_update` 需要正确的 usage bits
```gdscript
fmt.usage_bits = (TEXTURE_USAGE_SAMPLING_BIT
    | TEXTURE_USAGE_CAN_UPDATE_BIT
    | TEXTURE_USAGE_CAN_COPY_FROM_BIT)   # 回读需要 COPY_FROM
rd.texture_update(rid, 0, bytes)          # 主 RD 上可用 ✓
```

### 3.5 包装类名是 `Texture3DRD`（全大写 RD）
不是 `Texture3Drd`。`t.texture_rd_rid = rid` 即可让材质 `sampler3D` 采样。

### 3.6 `PackedFloat32Array` 传给 C# 是**值拷贝**（最坑）
```gdscript
# ✗ 完全无效：C# 写的是副本（实测 512/512 元素未变）
var arr := PackedFloat32Array(); arr.resize(n)
SomeCSharp.Method(arr, ...)   # C# 内部 arr[i] = ... 全部丢失
```
→ **数据所有权放 C#**：GDScript 调用方法驱动，需要数据时用 `GetBytes()`/`GetData()` 取回。

### 3.7 WorkerThreadPool 并行写入的正确姿势
- `add_group_task(action, elements, tasks_needed, ...)` **只有 5 个参数**，数据靠 `Callable.bind()` 传。
- 回调**只收到 element 一个参数**（bind 的参数排在其后）。
- **不要取局部引用再写**：`var s = arr[z]; s[0] = 1` → COW 静默丢弃。
- **不要多线程 `Array.append`** → 有竞态（实测 8 个元素丢了 1 个）。
- **正确**：共享一个扁平 `PackedFloat32Array`，每个 worker 只写自己那段不相交区间。

### 3.8 headless 没有 RenderingDevice
`--headless` 用 dummy 渲染器，`RenderingServer.get_rendering_device()` 返回 `null`。
→ 测 GPU 必须带窗口或 `--rendering-driver vulkan`；`Planet` 会自动回退到 `MESH`。

### 3.9 噪声必须在**世界坐标**采样
用归一化方向采样 → 整颗行星只落在一个噪声格子里 → 地形呈块状/台阶。

---

## 4. 为什么 CPU 侧用 C#（对应「动态多核」要求）

实测瓶颈**不是**噪声数学，而是 **GDScript 解释器逐体素调用原生函数的开销**：

| 场景 | 耗时（96³ = 88 万采样） |
|---|---|
| 纯数学单线程 | 18 ms |
| 纯噪声单线程 | **352 ms**（≈0.4 µs/采样） |
| 纯数学多线程（20 核） | 5.3 ms（3.4× 加速） |
| 纯噪声多线程（GDScript） | 260 ms（**几乎无加速**，被解释器开销淹没） |

→ 把密度内核移到 **C# + `Parallel.For`**：
- `Parallel.For` 由 .NET 线程池**按实际负载动态调度**（非静态均分，核心数变化自动适应）。
- 实测：96³ 从 352 ms → **24 ms**（约 30×）。
- 单次编辑只重算受影响 Z 切片：**2.5–13 ms**。

---

## 5. 已知限制与后续

| 限制 | 说明 | 后续 |
|---|---|---|
| 无网格碰撞 | 用 SDF 梯度推挤（非连续碰撞），高速下落可能穿透 | 连续碰撞 / swept collision |
| 稠密 delta 窗口 | 整窗口上传；Astroneer 用分页只传改动页 | 稀疏分页 + 局部 `texture_update` |
| 材质单一 | 按高度/坡度 4 色混合，无群系贴图 | 群系权重着色 |
| 只有 1 颗行星可着陆 | 其余为远景代理球 | 星际旅行（切换细节行星） |

---

## 6. 相关文件（均位于 `addons/unreal_world/`，即 UnrealWorld 插件）

| 文件 | 作用 |
|---|---|
| `voxel/VoxelTerrainCore.cs` | ★ 权威数据 + 稀疏编辑 + 多核光栅化 + 精确查询 |
| `voxel/TerrainNoise.cs` | ★ 基线噪声（C#，与 GLSL 逐位一致） |
| `voxel/voxel_volume.gd` | delta 窗口生命周期 + 纹理上传 |
| `voxel/raymarch_terrain.gd` | raymarch 渲染节点 |
| `shaders/raymarch_terrain.gdshader` | ★ 无限地形光线步进（解析基线 + delta + 写 DEPTH） |
| `shaders/terrain_noise.gdshaderinc` | ★ 基线噪声（GLSL，与 C# 逐位一致） |
| `planet/planet.gd` | 行星（RAYMARCH/MESH/PROXY 三模式 + 重力场） |
| `net/TerrainEdit.cs` | 世界编辑消息 |

**测试**（在 ProjectA 仓库）：
- `tests/probe_noise_match.tscn` —— C#/GLSL 噪声一致性
- `tests/headless_selftest_gpu.tscn` —— 地形核心自检
- `tests/perf_play.tscn` —— 端到端性能
