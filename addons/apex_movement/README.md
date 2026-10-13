# ApexMovement —— Apex 风格角色移动插件

> 把 **Apex Legends 的常规角色移动机制**做成 Godot 4.7（.NET）插件，服务 ProjectA。
> 架构逆向自 Apex Legends `r5apex_dx12.exe`（v3.0.3.65，SHA256 `b78dc5b0…`）。
> 插件的定位：**通用移动子系统**——不依赖 ProjectA，任何 Godot .NET 3D 项目都能用。

---

## 1. 逆向结论（证据）

Apex 用 Source 2 系引擎（Titanfall 血统）。二进制保留了完整的移动符号表
（`CGameMovement::*`、`sv_*` convar、`slide*` / `wallrun*` / `mantle*` / `airStrafe*` 等参数名）。
**证据等级**：这些是 `r5apex_dx12.exe` 字符串表中逐条核对的**符号名**（观测）；
**数值不在二进制里**（由脚本/配置在运行时给出），因此本插件的**数值是按符号语义 + Apex 手感重建**（推断），
不是原版精确值。这一点必须诚实标注。

| 类别 | 逆向到的符号（节选） |
|---|---|
| Source 基础 | `sv_friction` `sv_accelerate` `sv_airaccelerate` `sv_stopspeed` `sv_gravity` `sv_maxspeed` `sv_maxvelocity` `m_flFriction` `CategorizePosition` |
| 地面/空中 | `acceleration` `deceleration` `airAcceleration` `airDrag` `airFriction` `airSpeed` `airStrafeAcceleration` `airStrafeTaperStart/Finish` `sprintAcceleration` `sprintDeceleration` |
| 滑铲 | `slideAccel` `slideAccelForwardFrac` `slideAccelSideFrac` `slideDecel` `slideRequiredStartSpeed` `slideRequiredStartSpeedAir` `slideSpeedBoost` `slideSpeedBoostCap` `slideGravityScale` `slideGravityDownhillMultiplier` `slideGravityUphillMultiplier` `slideJumpHeight` `slideMaxJumpSpeed` `slideStopSpeed` `slideMaxStopSpeed` `slideVelocityDecay` `slide_max_angle_dot` |
| 蹬墙跑 | `wallrunAccelerateHorizontal/Vertical` `wallrunDecelerateHorizontal/Vertical` `wallrunMaxSpeedHorizontal/Vertical` `wallrunMinSpeed` `wallrunAllowedWallDistance` `wallrunGravityRampUpTime` `wallrun_timeLimit` `wallrunJumpUpSpeed` `wallrunJumpOutwardSpeed` `wallrunJumpInputDirSpeed` `wallrun_sameWallDist/Dot` `wallrunUpWallBoost` |
| 跳跃 | `ACT_DOUBLEJUMP` `player_doubleJumpAirControl` `jumpSpeed` `antiMultiJumpHeightFrac` `canJumpWhileCrouched` |
| 蹲伏/攀爬 | `crouchFraction` `mantleHeight` `mantleAngleScale` `climbAccel` `climbDecel` |

**参数名 → 插件字段**：见 `core/ApexMoveConfig.cs`，每个字段的 XML 注释都标了对应符号。

---

## 2. 架构（符合 ProjectA 约定）

```
apex_movement/
  core/                         ← C# 确定性核心（网络可回滚）
    ApexMoveConfig.cs           Resource：全部调参（含逆向符号对照）
    ApexMoveState.cs            纯数值状态（位置/速度/蹲/跳/滑/墙跑），可序列化
    ApexMoveInput.cs            确定性输入帧（wishdir + 按钮位）
    ApexMoveEnv.cs              物理环境查询结果（地面/墙/重力/吸附）
    ApexMovementCore.cs         求解器（纯函数，不碰引擎）
  character/
    apex_character.gd           GDScript 玩法层：输入采集 + 物理探测 + 驱动核心
  plugin.cfg / plugin.gd
```

**为什么这样分**（对齐项目「C# 负责核心、GDScript 负责玩法」）：

- **C# 核心 = 纯确定性求解器**：`(state, input, env, config, dt) → state'`。
  不读输入、不做射线、不碰节点 —— 所以**同一份代码**可跑在客户端权威（本地预测）
  与服务器权威（重放校验）两侧，且**逐位可复现**（`apex_move_selftest` 验证）。
- **GDScript 玩法层 = 感知 + 装配**：读键盘、做地面/墙探测、把结果写进 `ApexMoveEnv`、
  驱动核心、写回 transform。球面重力与地形都通过**回调**注入（不硬编码 ProjectA 依赖）。

**网络就绪**：`ApexMoveState.GetState()/SetState()` 与 `ApexMoveInput.GetInput()/SetInput()`
提供紧凑序列化（state 28 float / input 6 float），可直接进 ProjectA 的 `NetworkCore` 编辑消息流。
核心的确定性意味着服务器只需重放输入即可校验。

---

## 3. 接入 ProjectA 的两个适配点

ProjectA 是**球面行星 + raymarch 密度场地形（无碰撞体）**，因此不能直接用物理射线。
插件通过两个 `Callable` 解耦（`apex_character.gd`）：

```gdscript
char.gravity_up   = func(pos): return { "up": ..., "gravity": ... }  # 球面重力
char.sdf_density  = func(pos): return density                        # 地形密度（<0 实体）
```

- **`gravity_up`**：接入 `Planet.gravity_info_at()` → `up` 随星球径向变化，走到星球另一面也正确。
- **`sdf_density`**：接入 `Planet.sample_density()`。因为 raymarch 地形**没有碰撞体**，
  地面探测改为**把脚底投影到 density=0 等值面**（牛顿步），算出吸附偏移。
  未设置 `sdf_density` 时自动回退到物理射线（适用于有碰撞体的普通场景）。

**球面行星的关键修正**（本轮实测发现并修掉）：
纯切向速度在球面上会**沿切线飘离曲面**（切线一旦离开球面就不再是切线）。
修法是 Source 的 **ground snap**：接地时每帧把脚底用 `env.GroundSnap` 压回等值面。
不加这条，角色会在地表"跳动"（实测接地率仅 63% → 修复后 100%）。

---

## 4. 已实现的机制

| 机制 | 说明 | 对应符号 |
|---|---|---|
| 地面加速/摩擦 | Source 经典 `Accelerate()` + `Friction()`，不会超过目标速度 | `sv_accelerate` `sv_friction` `sv_stopspeed` |
| 空中控制 | `wishspeed` 被 `AirSpeedCap` 夹紧 → 保留 Source「空中急转」 | `sv_airaccelerate` `airStrafe*` |
| 跳跃 + 双跳 | coyote time + 输入缓冲 + 起跳锁；双跳带空中控制 | `player_doubleJumpAirControl` |
| 蹲伏 | 高度/速度缩放 + 平滑过渡 | `crouchFraction` |
| 冲刺 | 速度上限切换 | `sprintspeed` |
| 滑铲 | 起滑速度门槛 + 坡向重力缩放（下坡更快）+ 一次性加速 + 滑铲跳 | `slide*` |
| 蹬墙跑 | 速度/角度门槛 + 水平垂直加速 + 重力压制 + 蹬墙跳 + 同墙冷却 | `wallrun*` |
| **翻越 / 攀爬** | 自动上-翻：低障碍快速翻越、高障碍沿墙攀爬；过程可跳离 | `mantle*` `climb*` |
| **台阶上抬** | 近竖直小障碍自动上抬（Source `StepMove`） | — |
| **横向碰撞** | collide-and-slide（胶囊 shape cast），可开关 | — |

**未实现**（诚实标注）：抓钩（`grapple`）、滑索（`zipline`）、载具/悬浮板。
这些是独立的动作系统，建议后续按需追加。

### 攀爬 / 翻越细节

- **触发**：探测层沿移动方向找到「前方竖直挡面 + 上方可站立顶面」，高度落在
  `StepHeight < h ≤ ClimbHeight` 区间即触发。无输入时也允许（模拟自动翻越）。
- **分档**：`h ≤ MantleHeight` → 翻越（快，`MantleDurationLevel`）；
  `h ≤ ClimbHeight` → 攀爬（慢，`MantleDurationHigh`）。
- **过程**：脚本化位移（起点→目标点缓动），期间忽略重力与输入；按跳跃键可「跳离」。
- **关键实现约束**（实测踩坑）：
  1. 顶面探测必须**从墙上方往下**打射线，不能从墙内往上（Godot 对起点在实体内的射线不可靠）。
  2. 目标点是**胶囊中心**（脚站在墙顶 = 中心抬高半个身高），不是脚底。
  3. 翻越完成的那一帧必须**跳过横向碰撞解算**，否则会被当成穿墙弹回墙前。


---

## 5. 验证（可复现，全部实跑）

```bash
# 编译
dotnet build

# 单元自检（确定性 / 加速 / 摩擦 / 跳跃高度 / 双跳 / 空中控制 / 滑铲 / 墙跑 / 序列化）
"<godot>" --headless --path . res://tests/apex_move_selftest.tscn     # 期望 APEX MOVE SELFTEST PASS

# 集成（在 ProjectA 真实球面 raymarch 行星上行走）
"<godot>" --path . --rendering-driver vulkan res://tests/apex_move_integration.tscn  # 期望 PASS

# 独立测试关卡（平地/斜坡/台阶/翻越/攀爬/地洞/蹬墙/滑铲）——自动化验证
"<godot>" --headless --path . res://tests/apex_test_level/apex_level_test.tscn  # 期望 APEX LEVEL TEST PASS

# 独立测试关卡（可实际游玩，有窗口）
"<godot>" --path . --rendering-driver vulkan res://tests/apex_test_level/apex_playground.tscn
```

实测结果（本机 RTX 4070 / Godot 4.7 mono）：

- `apex_move_selftest`：**14 项全过**（含确定性、跳跃高度 1.55m ≈ 配置 1.6m、双跳不无限）
- `apex_move_integration`：**6 项全过**（球面 up 对齐 1.0000、行走接地 100%、不穿地）
- `apex_level_test`：**17 项全过**（平地/上下坡/台阶/翻越 2.10m/攀爬 3.50m/地洞/蹬墙/滑铲）
- ProjectA 原有 7 项测试**零回归**

### 独立测试关卡（`tests/apex_test_level/`）

为**避免虚拟世界（raymarch）干扰**，移动测试用**独立关卡**（`test_level.gd` 纯代码构建，
全部 Godot 原生 StaticBody3D 碰撞体）：

| 设施 | 位置 | 用途 |
|---|---|---|
| 大平地 | 原点 | 行走/加速/摩擦 |
| 上坡 15° | x=-30, z=-4→-18 | 爬坡接地 |
| 下坡 | x=-30, z=4→18 | 下坡不穿地 |
| 台阶 0.18/0.32/0.5m | x=10, z=-20 | 台阶上抬 |
| 翻越墙 1.2m | x=10, z=0 | mantle |
| 攀爬墙 2.6m | x=10, z=10 | climb |
| 地洞 6×6×6m | (30, 0) | 跌落 + 不穿透 |
| 蹬墙跑走廊（间距 3m） | z=-40 | wallrun |
| 滑铲长坡 22° | x=30, z=18→40 | slide |


---

## 6. 用法

```gdscript
# 1) 挂到场景（或纯代码创建）
var char := ApexCharacter.new()
char.config = ApexMoveConfig.new()      # 或 .tres 资源
add_child(char)

# 2) 接 ProjectA 的球面重力与地形
char.gravity_up  = func(p): return planet.gravity_info_at(p)
char.sdf_density = func(p): return planet.sample_density(p)

# 3) 外部驱动（网络/AI/测试）时关掉本地输入采集
char.autonomous_input = false
char.input_frame.WishDir = some_direction
char._step(1.0/60.0)
```

**输入映射**（默认复用 ProjectA 的动作名）：`move_forward/back/left/right`、
`move_up`（跳）、`move_down`（蹲）、`sprint`。
