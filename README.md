# ProjectA —— 无限可破坏宇宙（Astroneer 风格）

在 Godot 4.7（.NET/mono）中实现 **程序化星球 + 完全可破坏可建造地形 + 可扩展多人**。
架构逆向自 Astroneer v1.42.3.0，见 [`docs/reference/astroneer-reverse-engineering.md`](docs/reference/astroneer-reverse-engineering.md)。

## 技术约束

- **3D**，Forward+，Jolt Physics
- **C# + GDScript 混用**：C# 负责网络核心与性能关键路径，GDScript 负责玩法
- **联机双模式**：好友联机用客户端权威（低延迟），大型服可用专用服务器（防作弊）
- **CPU 侧动态多核**：`Parallel.For` / `WorkerThreadPool` 按负载动态调度

## 当前进度

| 阶段 | 状态 | 内容 |
|---|---|---|
| P0 | ✅ | C# 工程 + `NetworkCore`（两种权威模式 / 兴趣网格 / 编辑消息） |
| GPU raymarch | ✅ | C# 多核密度内核 + 3D 纹理 + 光线步进渲染 |
| 无限地形 | ✅ | 解析基线（GLSL 复刻 C#，逐位一致）+ delta 窗口 |
| **太阳系** | ✅ | **恒星（真实方向光）+ 八大行星**（水星…海王星，比例缩放） |
| **重力场** | ✅ | **每颗行星独立球面重力场** + 姿态平滑修正（无跳变） |
| **镜头** | ✅ | **宇宙自由三维旋转 / 星球重力场内 ±90° 夹紧**（按局部重力计算） |
| 第一人称 | ✅ | WASD + 鼠标 + Space/Ctrl 升降 + F 飞行 + 左键挖/右键填 |
| 音频 | ⬜ | **无**（见 BLOCKERS A1） |
| 美术资产 | ⬜ | 仅程序化地形；角色/建筑/植被无模型（见 BLOCKERS A2） |

## 操作方式

| 按键 | 功能 |
|---|---|
| `W A S D` | 移动 |
| 鼠标 | 视角（**宇宙中完整三维自由旋转**；**星球重力场内 ±90° 夹紧**） |
| `Space` | 上升（飞行）/ 跳（步行） |
| `Ctrl` | 下落（飞行） |
| `F` | 切换 步行 / 飞行 |
| `Shift` | 加速 |
| 左键 | 挖掘 |
| 右键 | 填土 |
| `G` | 传送到下一颗行星（验证多行星重力场） |
| `Esc` | 释放鼠标 |

### 视角规则（本轮修复重点）

| 环境 | 规则 |
|---|---|
| **宇宙中**（无重力场） | 完整三维自由旋转，**无任何限制**（四元数累加，可翻过极点） |
| **星球重力场内** | 以**该星球局部重力方向**为参考：最低看向星球中心（-90°），最高抬头 90° |

角度**全部基于玩家所在位置与该星球的重力方向计算**，不依赖世界坐标——
因此走到星球另一面也完全正确（已测 `up=±X/±Y/±Z` 及极点）。

## 核心设计（三条）

1. **地形 = 解析基线 + 稀疏差分**
   `density(p) = baseline(p) + delta(p)`。基线**永不存储**（C# 与 GLSL 各算一份，逐位一致），
   因此世界**无限**、没有体积边界；存档只存玩家改动。
2. **编辑可加（可交换）→ 确定性**
   挖/填是线性可加平滑核，与网络到达顺序无关，所有 peer 必然算出同一地形。
3. **网络只同步 edit，不同步体素**；兴趣网格只发给附近玩家。

### 为什么噪声要自研（不用 FastNoiseLite）
渲染（GPU）与碰撞（CPU）必须算出**同一个**密度场，否则玩家穿模/悬空。
因此 `TerrainNoise.cs` 与 `terrain_noise.gdshaderinc` 是同一算法的手写双实现，
只用整数哈希 + float32 基本算术。**已实测逐位一致**（`tests/probe_noise_match`）。

## 架构

```
scripts/net/                        ← C# 网络核心（不含玩法）
  NetworkCore.cs / AuthorityMode.cs / TerrainEdit.cs / InterestGrid.cs

scripts/voxel/                      ← 体素（GPU raymarch 路径）
  TerrainNoise.cs          ★ 基线噪声（C# 版，与 GLSL 逐位一致）
  VoxelTerrainCore.cs      ★ 权威数据 + 稀疏编辑 + 窗口光栅化 + 精确查询
  voxel_volume.gd            窗口跟随玩家 + 上传 delta 纹理
  raymarch_terrain.gd        raymarch 渲染节点
  surface_nets.gd / voxel_world.gd / voxel_chunk.gd / procedural_field.gd
                             MESH 回退模式（无 RenderingDevice 时）

scripts/player/
  first_person_player.gd      WASD + 球面重力 + 飞行 + SDF 碰撞 + 挖掘

scripts/planet/
  planet_definition.gd        行星纯数据
  planet.gd                   RAYMARCH / MESH / PROXY_ONLY 三模式 + 球面重力
  solar_system.gd             4 行星 + 轨道 + 活动行星

assets/shaders/
  terrain_noise.gdshaderinc   ★ 基线噪声（GLSL 版，与 C# 逐位一致）
  raymarch_terrain.gdshader   ★ 无限地形光线步进（解析基线 + delta 窗口 + 写 DEPTH）

scenes/levels/bootstrap.{gd,tscn}   主场景
```

## 如何验证

Godot（mono 版）：`D:\Godot\Godot_4.7_stable_mono\Godot_v4.7-stable_mono_win64_console.exe`

```bash
# 1) 编译 C#
dotnet build

# 2) CPU/GPU 噪声一致性（关键正确性：渲染与碰撞同场）
"<godot>" --path . --rendering-driver vulkan res://tests/probe_noise_match.tscn   # 期望 NOISE MATCH PASS

# 3) 地形核心自检（基线/编辑/确定性/射线/存档往返）
"<godot>" --path . --rendering-driver vulkan res://tests/headless_selftest_gpu.tscn  # 期望 CORE SELFTEST PASS

# 4) Surface Nets 回退路径自检
"<godot>" --headless --path . res://tests/headless_selftest.tscn   # 期望 SELFTEST PASS

# 5) 玩家物理模拟（重力落地 / 挖掘）
"<godot>" --path . --rendering-driver vulkan res://tests/sim_player.tscn

# 6) 性能基准
"<godot>" --path . --rendering-driver vulkan --resolution 1280x720 res://tests/perf_play.tscn

# 7) 镜头回归（yaw/pitch 正交、夹紧、星球另一面、宇宙自由旋转）
"<godot>" --path . --rendering-driver vulkan res://tests/repro_camera.tscn

# 8) 太阳系验证（8 行星 / 太阳光照 / 重力场 / 姿态平滑）
"<godot>" --path . --rendering-driver vulkan res://tests/verify_solar.tscn

# 9) 重力场精确验证（8 行星 × 5 方向）
"<godot>" --path . --rendering-driver vulkan res://tests/verify_gravity.tscn

# 10) 实际游玩（有窗口）
"<godot>" --path . --rendering-driver vulkan res://scenes/levels/bootstrap.tscn
```

> **注意**：`--headless` 用 dummy 渲染器，**没有 RenderingDevice**，raymarch 路径会回退到 MESH。

## 实测性能（RTX 4070 Laptop / 20 核 / 1280×720）

| 指标 | 数值 |
|---|---|
| 玩家场景帧时 | **~12 ms（83 fps）**，1 个 draw call |
| 单次挖掘（半径 3m） | **2.5 ms**（零网格重建） |
| 地形生成 96³ | C# 多核 24 ms（GDScript 需 352 ms，差 30×） |

> 性能优化：天空像素早退（站在地表向外看时 `discard`，不跑 raymarch），
> 球壳裁剪 + 自适应步长 + 固定步长中心差分法线（消除阶梯伪影）。

## 截图

- [`docs/reference/surface_view.png`](docs/reference/surface_view.png) —— 地表视角：起伏地形 + 弧形地平线 + 星空 + 邻星
- [`docs/reference/scene_player.png`](docs/reference/scene_player.png) —— 第一人称视角
- [`docs/reference/orbit_view.png`](docs/reference/orbit_view.png) —— 轨道视角：raymarch 行星 + LOD 代理

## 待决事项（重要）

**请先阅读 [`docs/design/BLOCKERS.md`](docs/design/BLOCKERS.md)** —— 记录了音频/美术缺失、
行星尺度与联机模式等需要你拍板的架构决策，以及我擅自做的决定。

## 文档

- [`docs/design/BLOCKERS.md`](docs/design/BLOCKERS.md) —— 待决/阻塞项
- [`docs/design/raymarch-implementation.md`](docs/design/raymarch-implementation.md) —— raymarch 实现 + Godot 陷阱
- [`docs/design/universe-engine-design.md`](docs/design/universe-engine-design.md) —— 总体设计
- [`docs/reference/astroneer-reverse-engineering.md`](docs/reference/astroneer-reverse-engineering.md) —— 逆向报告
