# 开发阶段总结 · 阶段一

> 时间：2026-10-13
> 范围：从零到「可运行的 Astroneer 式太阳系 + 可破坏地形 + 第一人称」
> 仓库：`ProjectA`（应用）+ `UnrealWorld`（世界插件，submodule）

---

## 1. 这一阶段做成了什么

一句话：**在 Godot 4.7 里做出了一个能跑、能玩、能挖的太阳系。**

| 能力 | 状态 | 验证方式 |
|---|---|---|
| 无限可破坏地形（GPU raymarch） | ✅ | 渲染截图 + 挖掘测试 + 自检 |
| 恒星（真实方向光照） | ✅ | 实测改目标光照方向转 90° |
| 八大行星（比例缩放） | ✅ | 清单 + 渲染截图 |
| 每颗行星独立球面重力场 | ✅ | 8 行星 × 5 方向全部对齐 |
| 姿态随重力平滑修正 | ✅ | 60 帧内单帧最大变化 < 20° |
| 第一人称（WASD + 鼠标） | ✅ | 物理模拟（重力落地 + 挖掘） |
| 镜头双模式（宇宙自由 / 星球限制） | ✅ | 7 项镜头回归测试 |
| 网络核心（双权威 + 兴趣网格） | ✅ 代码就绪 | 未双机实测 |

**性能**：1280×720 约 12 ms（83 fps），1 个 draw call；单次挖掘 2.5 ms（零网格重建）。

---

## 2. 关键架构决策（为什么这么做）

### 2.1 无限地形 = 解析基线 + 稀疏差分

```
density(p) = baseline(p) + delta(p)
```

- `baseline(p)`：解析函数，**永不存储**，由 GLSL 求值（C# 复刻同一算法供碰撞）。
- `delta(p)`：玩家挖填的稀疏列表，光栅化进跟随玩家的窗口纹理。

**收益**：世界无边界；存档只存改动；联机只同步改动。

### 2.2 渲染与碰撞共用同一密度场（最关键的约束）

`voxel/TerrainNoise.cs`（C#）与 `shaders/terrain_noise.gdshaderinc`（GLSL）是
**同一算法的手写双实现**，只用整数哈希 + float32 基本算术。
**实测逐位一致**（`tests/probe_noise_match`，最大误差 0.00003）。

> ⚠️ 改任一处必须同步另一处，否则玩家穿模/悬空。

### 2.3 世界系统与网络解耦

世界系统**不依赖网络**：本地编辑通过 `Planet.edit_created` 信号暴露，
由宿主（ProjectA 的 `bootstrap.gd`）决定是否广播。

### 2.4 双仓库（插件化）

| 仓库 | 职责 |
|---|---|
| `ProjectA` | 应用层：第一人称、网络核心、主场景、测试 |
| `UnrealWorld` | 世界插件：行星/太阳系/恒星/体素地形/噪声/着色器/`TerrainEdit` |

`ProjectA` 通过 git submodule 引入 `UnrealWorld`。工作流见 ProjectA 的 README。

---

## 3. 本轮修掉的真实 bug（复现 → 定位 → 修复）

### 3.1 镜头左右旋转失效（用户报告，最优先）

- **根因**：`Basis(Quaternion(up, yaw))` 从**世界单位帧**绕 `up` 旋转，
  导致 pitch 轴与 yaw 轴在 `up≈±X` 时退化；`up≈±Z` 时 forward 掉出切平面。
- **修法**：先构造 Y 轴 = up 的**切平面正交基**，再叠加 yaw 与 pitch。
- **附带**：修正 pitch 符号（鼠标下移曾变成抬头）。

### 3.2 其他

| # | 现象 | 根因 | 修法 |
|---|---|---|---|
| 1 | 天空橙色 | 细节行星与太阳同处原点，玩家在太阳内 | 细节行星放自己轨道位置 |
| 2 | 地形画布漂移 | 细节行星公转/自转 | 细节行星不公转不自转 |
| 3 | 玩家被自己代理球包裹 | LOD 阈值 < 行星半径 | 阈值取 `max(proxy_distance, radius*3)` |
| 4 | 地形从内部命中 | 球壳步进从 t=0 起 | 从球壳入口起 + 起点实体则 discard |
| 5 | 性能 64 fps | 地平线像素在球壳走 489 m | 天空像素早退 → 92 fps |
| 6 | 阶梯伪影 | 法线步长随距离放大 | 固定步长中心差分 |

---

## 4. 踩过的 Godot 陷阱（详见 `raymarch-implementation.md` §3）

最值得记的三条：

1. **addon 子目录含 `project.godot` → 整个目录被 Godot 忽略**。
   插件仓库根**绝不能**放 `project.godot`。
2. **`PackedFloat32Array` 传给 C# 是值拷贝**，C# 原地写全部丢失 → 数据所有权必须放 C#。
3. **主 RenderingDevice 不能手动 `submit`/`sync`** → 改用 CPU 多核 + `texture_update`。

---

## 5. 明确未完成（见 `BLOCKERS.md`）

| 项 | 状态 |
|---|---|
| 音频 | **完全没有**（无法创作素材） |
| 美术资产 | 仅程序化地形；无角色/建筑/植被模型 |
| 联机双机实测 | 两种权威模式代码就绪，未实跑 |
| 星际旅行 | 仅 1 颗行星可着陆，其余为远景代理球 |
| 土星环 / 挖掘粒子 / UI | 未做 |

---

## 6. 验证入口（可复现）

```bash
# 编译
dotnet build

# 镜头回归（7 项）
"<godot>" --path . --rendering-driver vulkan res://tests/repro_camera.tscn

# 太阳系（8 行星 / 光照 / 重力 / 姿态）
"<godot>" --path . --rendering-driver vulkan res://tests/verify_solar.tscn

# 重力场（8 行星 × 5 方向）
"<godot>" --path . --rendering-driver vulkan res://tests/verify_gravity.tscn

# C#/GLSL 噪声一致性
"<godot>" --path . --rendering-driver vulkan res://tests/probe_noise_match.tscn

# 地形核心（基线/编辑/确定性/射线/存档）
"<godot>" --path . --rendering-driver vulkan res://tests/headless_selftest_gpu.tscn

# 玩家物理
"<godot>" --path . --rendering-driver vulkan res://tests/sim_player.tscn
```

---

## 7. 下一阶段的候选（按性价比）

1. **实机试玩反馈**（手感：鼠标灵敏度、移动速度、跳跃）——唯一无法自测的。
2. 确认**行星尺度**与**联机模式**（决定后续方向，见 BLOCKERS C1/C2）。
3. 基础 UI（准星 / 坐标 / 操作提示）。
4. 挖掘反馈（粒子 + 占位音）。
5. 多行星可着陆（星际旅行）。
6. 联机双机实测。
