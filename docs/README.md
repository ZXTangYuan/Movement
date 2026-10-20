# ApexMovement 插件 v0.3

Apex 风格移动系统（**固定 tick 确定性**）+ **相机子系统** + 可玩测试场地。
逆向自 Apex Legends **v3.0.3.65** 的可执行文件（只读分析，未修改游戏安装目录），
数值标定自 [Apex Movement Wiki](https://apexmovement.tech/wiki)。

---

## 1. 一句话

复现 Apex 的移动手感（WASD / 蹲 / 跳 / 冲刺 / 收枪 / 滑铲 / 滑铲跳 / 连续滑铲跳 /
四档翻越 / 窗口翻越 / 贴墙爬 / 空中转向 / 跳跃疲劳 / Fallstun / FOV / 镜头晃动 / 走路摇晃），
但把**画面帧判定改成游戏 tick 判定** —— 同样的输入序列在 30 / 60 / 144 / 240 fps 下
产生**逐位相同**的轨迹。

v0.3 的两个主打变化：**攀爬不再是被动吸入**（必须同时满足“面向墙 + 在空中 + 有朝墙速度”，进去以后是一个状态：按住 W 才上爬，松手就掉下来），**移动 tick 固定 60/s 为基准**（昂贵的探测按 30/s 抽样）。落地镜头晃动幅度同时大幅收敛（峰值 10.3° → 2.49°）。

---

## 2. 操作

| 按键 | 动作 |
|---|---|
| `W A S D` | 移动 |
| `Space` | 跳跃（**攀爬不靠 Space**，见下两行） |
| `Ctrl` | 蹲下 |
| `Shift` | 冲刺 |
| `Shift + Ctrl` | 滑铲（需要速度 ≥ 200 hu/s = 5.08 m/s） |
| `Shift + Ctrl + Space` | 滑铲跳 |
| 反复「滑铲 → 跳 → 落地 → 滑铲」 | 连续滑铲跳 |
| 撞向台面 + 按住 `W` | 四档翻越：Below / Level / Above / High |
| 撞向窗口 + 按住 `W` | 窗口翻越 |
| **面向墙 + 在空中 + 有朝墙速度** | **挂上墙（`Climb` 状态）**：视角夹角 ≤ 45.57°、朝墙速度 ≥ 0.5 m/s、且必须在空中 |
| 挂墙后按住 `W` | 以 2 m/s 上爬；**松开 0.22s 就取消攀爬、开始下落** |
| 爬到墙顶（有台面） | 补爬 0.35m 进入 `Hang` 扒墙，再按 `W` 上墙 |
| **贴墙 + 按住 `Ctrl`** | **独立扒墙（`WallHang` 状态）**：空中贴墙按住蹲才挂（非强制）。能自动翻越的矮墙不挂 |
| 挂墙中 `W` / `A` `D` / 不动 | 上爬 ≈1 m/s（并回补握力）/ 横移 ≈0.8 m/s / 缓慢下滑（掉握力） |
| 挂墙中 `Space` / 松 `Ctrl` | 蹬墙跳（离墙 + 向上）/ 直接松手 |
| **走进打滑体积** | **打滑（`Slip` 状态）**：被地面方向推着滑到 8 m/s；蹲下优先转滑铲、按跳优先起跳 |
| `H` | 收枪 / 持枪切换（收枪 = ×1.15 速度） |
| `[` / `]` | FOV −2° / +2° |
| `R` | 回到出生点 |
| `Esc` | 释放鼠标 |

---

## 3. 数值口径（双预设）

**默认预设 `Apex`**：角色尺寸用**需求方口径**，其余用 **Apex 实机实测值**。

| 项 | 值 | 来源 |
|---|---|---|
| 站立高度 | **1.95 m** | 需求方口径（优先级最高） |
| 蹲下高度 | **0.975 m**（= 站立一半） | 需求方口径 |
| 眼位 | 头顶 −0.10 m（站立 1.85 / 蹲下 0.875） | 需求方口径 |
| 胶囊半径 | 0.4064 m（16 hu） | wiki 引擎篇 |
| 重力 | **20.78 m/s²** | **由 wiki 两个实测值反解**：h=56hu、t=0.74s → g=8h/t² |
| 行走速度 | 4.407 m/s（173.5 hu/s） | wiki Ground |
| 冲刺速度 | 6.604 m/s（260 hu/s，持枪） | wiki Sprint |
| 收枪倍率 | ×1.15 → 冲刺 **7.595 m/s**（299 hu/s） | wiki Holster |
| 蹲行速度 | 2.032 m/s（80 hu/s） | wiki Crouch |
| 跳跃高度 / 滞空 | 1.4224 m（56 hu）/ 0.74 s | wiki Jump |
| 空中 wishspeed | **1.524 m/s**（60 hu/s） | wiki Air strafe（air strafe 的物理根因） |
| 空中摩擦 | **0**（Apex 无空气摩擦） | wiki 引擎篇 |
| 跳跃疲劳 | 0.15 s → 0.75 s 线性回升，最低 30% 高度 | wiki（游戏内变量 `antiMultiJumpTime*`） |
| Bunnyhop 减速 | 最多 2.54 m/s（100 hu/s），带 cutoff | wiki Bunnyhop |
| 滑铲触发 | 地面 5.08 m/s；空中 2.286 + 5.08（水平+垂直） | wiki Slide |
| Slideboost | +3.81 m/s（150 hu/s），上限 10.16（400 hu/s），**2 s 冷却** | wiki Slide |
| Slidejump 阈值 | 速度 < 8.89 m/s（350 hu/s）**或** 滑铲 ≥ 0.24 s | wiki Slide jump |
| 翻越上限 | 6.9342 m（273 hu） | wiki 引擎篇 + Climb |
| 攀爬 Attach offset / End boost | 2.54 m（100 hu）/ 0.7112 m（28 hu） | wiki Climb |
| 可越过台阶 | 0.762 m（30 hu） | wiki Ground（overwalkable） |
| Fallstun | 7.62 m（300 hu）起算 → 20.32 m（800 hu）满值 1 s，二次曲线 | wiki Fallstun |
| 打滑速度 / 方向限制锥 | 8 m/s / 35° | **C 级手感参数**（逆向只证明 `slipSpeed`、`m_slipAirRestrictDirection` 字段存在） |
| 扒墙爬升 / 横移 / 下滑 | 1.0 / 0.8 / 0.22 m/s；握力上限 6 s | **C 级手感参数**（对应 `ACT_MP_WALLHANG_*`、`sound_wallHangSlip`） |

**`Legacy` 预设**：v0.1 的口径（行走 5.4 / 冲刺 ×1.37 = 7.4 / 攀爬 6.5 / 跳跃 1.15）。
在 `ApexPlayer.ConfigPreset` 里一行切换。

> 所有参数集中在 `core/MovementConfig.cs`，**每个字段都标注 A/B/C 证据等级**。
> 换算表与来源链接见 [`docs/wiki-reference.md`](docs/wiki-reference.md)。

---

## 4. 运行

```bash
GODOT="D:/Godot/Godot_4.7_stable_mono/Godot_v4.7-stable_mono_win64_console.exe"

# 有窗口，可玩
"$GODOT" --path . --rendering-driver vulkan res://addons/apex_movement/testbed/apex_testbed.tscn

# 自检 A：跨帧率确定性 + 36 项功能 + 23 项相机 + 29 项攀爬/tick（纯数学，无引擎）
#         = 88 项断言
"$GODOT" --headless --path . res://addons/apex_movement/tests/determinism_test.tscn
# 期望：=== APEX MOVEMENT SELFTEST PASS ===

# 自检 B：44 项真实物理集成断言（跑真实 Godot 物理 + 真实场地）
"$GODOT" --headless --path . res://addons/apex_movement/tests/integration_test.tscn
# 期望：=== INTEGRATION PASS（44 项断言） ===

# 截图（需要窗口/真实渲染）
"$GODOT" --path . --rendering-driver vulkan --resolution 1920x1080 \
         res://addons/apex_movement/testbed/shot_testbed.tscn
# 出 20 张图到 user://shot_*.png：
#   9 张自由机位（overview / ramps / mantle_steps / vault_walls / window_frames /
#                  climb_wall / slide_lane / drop_plats / slip_zone）
#  11 张玩家视角（fp / fov110 / fov70 / walking / slide / mantle /
#                  climb / hang / climb_top / slip / wallhang）
#   ← climb / hang / climb_top 是 v0.3 攀爬三阶段；slip / wallhang 是 v0.3 打滑与扒墙
#   wallhang 是唯一的第三人称图（第一人称贴墙只有一片墙，看不出挂点）
#   HUD 会打印当时的 fov / roll / state —— 这是相机效果的视觉证据
```

**为什么需要两套自检**：`determinism_test` 跑在 `AnalyticWorld`（纯数学碰撞替身）上，
覆盖不到 Godot 桥接层与相机应用层。历史上「无法移动」「Ctrl 穿地」「跳跃初速被吸附吃掉」
三个 bug 都只存在于桥接层，AnalyticWorld 完全复现不出来。

---

## 5. 架构

```
core/                        ← 纯 C#，不引用 Godot（可脱离引擎单元测试）
  F3.cs                       确定性三维向量（标量 float，不走 SIMD）
  MovementConfig.cs           全部参数 + 证据等级 + Apex/Legacy 双预设
  MovementTypes.cs            状态/翻越档位/落地档位/输入/快照/世界接口（含 ISlipWorld 打滑探针）
  ApexMovementCore.cs         求解器（状态机 + 加速/摩擦/跳跃/滑铲/翻越/攀爬/打滑/扒墙/疲劳/Fallstun）
  ApexTickClock.cs            固定 tick 累加器（含死亡螺旋防护）

camera/                      ← 纯 C#，相机效果（可脱离引擎单元测试）
  CameraEffectsConfig.cs      FOV / 弹簧视角踢 / 侧倾 / 翻越插值 / 走路摇晃 参数
  ApexCameraRig.cs            每帧算出 FOV / roll / pitch-yaw 偏移 / 眼位偏移

godot_bridge/
  GodotMovementWorld.cs       IMovementWorld + ISlipWorld 的 Godot 实现（CastMotion / GetRestInfo / Raycast / SampleSlip）

player/
  ApexPlayer.cs               玩家节点：输入累积 + 固定 tick 驱动 + 相机应用 + HUD
  apex_input_map.gd           输入动作自注册（不污染宿主项目）

testbed/
  testbed_builder.gd          程序化生成测试场地
  apex_testbed.tscn           可玩入口
  shot_testbed.gd/.tscn       多视角截图工具

tests/
  AnalyticWorld.cs            纯数学碰撞世界（测试替身，契约与 Godot 桥接层一致）
  DeterminismTest.cs          跨帧率确定性
  FeatureTest.cs              36 项功能断言（纯数学）
  CameraTest.cs               23 项相机断言（纯数学）
  ClimbTest.cs                29 项攀爬/tick/探针断言（纯数学）
  SlipHangTest.cs             34 项打滑/扒墙断言（纯数学）
  determinism_test.tscn       自检 A 入口
  integration_test.gd/.tscn   44 项真实物理集成断言（自检 B）
```

### 关键设计：帧率无关

| 环节 | 做法 |
|---|---|
| 状态推进 | 只在固定 tick 内发生，`dt` 恒为 `1/TickRate` |
| 鼠标增量 | 渲染帧**累积**，tick 边界**整份消费**（总量守恒 ⇒ 累计转角一致） |
| 浮点 | 只用标量 `float` 加减乘除 + `MathF.Sqrt`，不用 SIMD 归约 |
| 走路摇晃 | 步频相位在 **tick 内**推进（不在渲染帧），所以摇晃也帧率无关 |
| 卡顿 | 单帧最多追赶 4 tick，超出丢弃并计数（防死亡螺旋） |

### 与 ProjectA 的边界

`core/` 与 `camera/` 不依赖 ProjectA 的任何类型。重力方向通过 `SetUp(F3 up)` 注入，
默认世界 Y 轴向上；将来接入球形行星时每 tick 传入行星局部 up 即可复用。

---

## 6. 版本变化史

### 6.1 v0.3 相比 v0.2

**① 攀爬：从"被动吸入"改成"有条件的攀爬状态"（本轮主线）**

v0.2 的攀爬是"只要在空中按着 W 贴着墙就被吸上去，吸上就一股劲爬到顶再抛出去" ——
玩家没有任何拒绝的余地，贴墙跳一下就会被拽走。v0.3 把它改成**真正可进入 / 可退出**的状态：

| 项 | v0.2 | v0.3 |
|---|---|---|
| 触发条件 | 空中 + `MoveZ > 0.3`（按着 W 贴墙就吸） | **四条同时满足**：① 在空中（地面 / 滑铲不触发）② 面向墙，视角夹角 ≤ **45.57°** = arccos 0.7（wiki attach 容差）③ **朝墙速度 ≥ 0.5 m/s** ④ 同一面墙不重复附着（除非低于上次附着点，或新面与旧面夹角 ≥ 25.842°） |
| 吸附后 | 直接一路爬到顶 → End boost 抛出去 | **进入 `Climb` 状态**，竖直速度先归零（挂住，不自己上升） |
| 上爬 | 无条件 | **必须按住 `W`**：`ClimbSpeed` 2.0 m/s（78.7 hu/s），实测 0.5 s 正好 +1.000 m |
| 松手 | 无 | `ClimbNoInputGrace` **0.22 s** 后取消攀爬并下落（实测宽限 13 tick 内仍 Climb，超时转 Air，vy=-7.28）；侧向输入持续 1 s 也取消 |
| 到顶 | 统一 End boost | 有台面可挂 ⇒ **补爬 0.35 m 进入 `Hang`（扒墙）**，再按 `W` 上墙；无台面 ⇒ 保持 End boost 上抛 |
| 探测频率 | 每 tick 无条件 | `ClimbProbeInterval`（默认 1，可降到 2） |
| 爬墙真的会动吗 | **不会** —— `Step()` 在 Climb 分支提前 `return`，跳过位移步骤，v0.2 只赋速度不位移 | 会（运动学位移 + 每 tick 贴墙钳制） |

顺带把**翻越**也加了输入门槛：`TryTraversal` 现在要求 `MoveZ > 0.3`（wiki：mantle grab 需要
移动输入指向墙面）。原先站在墙边**完全不操作**也会被 6 m 墙翻上去。

**② 移动 tick 固定 60/s 基准，昂贵探测降到 30/s**

| 项 | v0.2 | v0.3 |
|---|---|---|
| tick 基准 | `SyncTickRateWithEngine()` 用 `Engine.PhysicsTicksPerSecond` 覆盖 `TickRate` | **固定 60/s**（`MovementConfig.PinnedTickRate`）；工程物理频率不是 60 时由 `ApexTickClock` 累加器折算 |
| 卡顿补帧 | 无上限 | 单帧最多补 **4** tick，其余丢弃并计入 `DroppedTicks` |
| 翻越探针 | 每 tick | `TraversalProbeInterval = 2`（60 Hz tick → 30 Hz 探针） |
| 攀爬探针 | 每 tick | `ClimbProbeInterval = 1`（可配） |

实测：30 fps 每帧消费 2 tick；144 fps 跑 10 秒（1440 帧）得 **599** tick，无累积漂移。

**③ 落地镜头晃动大幅收敛（对应需求"晃动过于剧烈"）**

| 项 | v0.2 | v0.3 |
|---|---|---|
| `LandingPitchImpulse` | `{0, 0.9, 2.2, 4.0, 6.5}` → 峰值 5.9° / 10.8° / 14.6°，后两档**全部撞上** `MaxKickAngle`(10.3°) ⇒ 所有大落地都是同一次 10° 甩头 | `{0, 0.25, 0.55, 0.90, 1.33}` → 峰值 **0.47° / 1.04° / 1.69° / 2.49°** |
| `MaxKickAngle` | 0.18 rad（10.3°，实际工作点） | **0.075 rad（4.3°），只作保险丝**（实测峰值 0.0435 < 0.075 × 0.9） |
| 走路 bob | 0.035 m / 0.022 m / 0.35° / 0.30° | 0.026 m / 0.016 m / 0.24° / 0.20°（实测移动峰值 0.0281 m） |
| `KickScale` | 无 | 新增总倍率（0 = 关掉所有视角踢；落地踢与手动踢都受它控制） |

**④ 打滑态 Slip（对应逆向字段 `slipSpeed` / `IsSlipping` / `m_slipAirRestrictTime` / `m_slipAirRestrictDirection`）**

新增 `MovementState.Slip` 与 `ISlipWorld` 探针接口：场景里放一个 `Node3D` 打滑体积
（`add_to_group("slip_volume")` + 子 `CollisionShape3D`），角色进入就被地面方向推着滑走。

| 项 | 行为 |
|---|---|
| 进入 | 探针命中打滑体积 ⇒ `Slip`。**只覆盖地面态**：蹲下优先转 `Slide`、按跳优先转 `Air`，环境不抢玩家状态 |
| 速度 | 沿体积方向加速到 `SlipSpeed`（默认 8 m/s，实测 8.00）；方向由体积给，没配方向时**从体积中心往外推**（对应 `C_TriggerSlipSphere` 的"站进去被挤出去"） |
| 方向限制 | 空中时用 `SlipAirRestrictConeDeg`（默认 35°）把速度压进以原方向为轴的圆锥，`m_slipAirRestrictTime` 计时结束后衰减到 0；纯数学部分 `ClampToCone` 已开放为 `public static` 供自检验证 |
| 垂直方向 | `LockToPlane` 只压掉**向上**分量（`if (upVel > 0f) _vel -= _up * upVel`），重力照常 ⇒ 空中进滑区会正常落地，**不会悬空**（这条是自检抓出来的真实 bug，见第 7 节） |
| 探测频率 | `SlipProbeInterval = 2` ⇒ 60 Hz tick 下 30 Hz 采样（实测 60 tick 只采样 30 次） |

**⑤ 独立扒墙 WallHang（对应逆向字段 `wallhang` / `m_wallHanging` / `IsWallHanging` / `automantle_disable_hang` / `ACT_MP_WALLHANG_*` / `sound_wallHang*`）**

v0.3 早先的 `Hang` 只是"攀爬到顶后的过渡态"。本轮新增真正独立的 `MovementState.WallHang`：

| 项 | 行为 |
|---|---|
| 触发 | **非强制**：空中 + 贴墙 + **按住蹲键**才挂（`automantle_disable_hang` 语义：能自动翻越的矮墙不挂）。松蹲立刻松手 |
| 姿态 | 速度恒为 0（`speed=0.00` / `vy=0.00`），身体贴到墙面（离墙 = 半径 + 皮肤，实测 z=-1.574） |
| 握力 | `WallHangGrip` 从 1.0 开始消耗（HUD `grip=`），耗尽自动脱手；按 `W` 上爬会回补（`m_wallDangleClimbProgressFloor` 语义，实测 0.81 → 0.90） |
| 移动 | 按 `W` 沿墙上爬 ≈1 m/s；按 `A`/`D` 横移 ≈0.8 m/s（`ACT_MP_WALLHANG_LEFT/RIGHT`）；不动则缓慢下滑（`sound_wallHangSlip`，实测 0.22 m） |
| 脱手 | 松蹲 / 握力耗尽 / 超 `WallHangMaxTime`(6 s) / 甩视角超过 `WallHangMaxViewAngle`(45.57°) / 按跳（蹬墙跳，实测 vz=2.69 vy=6.15） |
| 重复挂 | `_wallHangNeedRelease` 门闩：脱手后必须"离开墙面一次"才能再挂，否则按住蹲的玩家会在冷却一过被立刻抓回去，`m_wallHangTime` 永远走不到上限 |
| 探测频率 | 不按蹲时提前返回，**零射线开销**；按住蹲时每 tick 探（实测 40 tick 探 40 次） |

**⑥ v0.3 逆向补充（常量对齐）**

攀爬常量与 wiki 对齐：attach offset **100 hu**、climb 空间 cutoff **147 hu**、end boost **28 hu**、
attach 视角容差 **45.57°** = arccos(0.7)、可攀墙面角度 > **45.572°**。

### 6.2 v0.2 相比 v0.1 的机制变化

| 机制 | v0.1 | v0.2 |
|---|---|---|
| 空中摩擦 | 有 | **无**（wiki：Apex has no air friction） |
| 空中加速 | 单一 AirAccel | **AirWishSpeed = 60hu/s 硬约束** + AirAccel（air strafe 的根因） |
| 滑铲触发 | Shift+Ctrl + 速度 | **地面/空中两套阈值**（200hu/s vs 90+200hu/s） |
| 滑铲加速 | ×1.25 倍率 | **Slideboost = +150hu/s，上限 400hu/s，2s 冷却** |
| 滑铲跳 | 时间窗内保留速度 | **350hu/s 速度阈值 或 0.24s 时间窗**（Apex 真实规则） |
| 翻越分类 | Vault / Mantle 两态 | **Below / Level / Above / High 四档**（对应二进制 4 个时长字段） |
| 翻越时序 | 单段 smoothstep | **Grab → Pull 两段**（+ 可选挂边 Hang） |
| 窗口翻越 | 无 | **有**（`traversal_window_*` 六参数） |
| 贴墙爬 | 无 | **Climb 状态**（273hu 上限 / 100hu attach offset / 28hu end boost） |
| 上台阶 | 无（19cm 台阶就卡死） | **有**（Apex overwalkable，30hu） |
| 跳跃疲劳 | 无 | **有**（0.15→0.75s，高度 30%） |
| Bunnyhop 减速 | 无 | **有**（100hu/s，带 cutoff） |
| Fallstun | 无 | **有**（300hu→800hu 二次曲线） |
| 蹲下收缩方向 | 一律脚底锚定 | **地面向下缩 / 空中向中间缩**（slidehop vs bhop 差异根因） |
| 落地分档 | 无 | **Low/Medium/High/Hard**（供相机晃动） |
| 收枪加速 | 无 | **有**（×1.15） |
| **相机子系统** | 无 | **FOV 调节 + 弹簧视角踢 + 滑铲/冲刺侧倾 + 翻越相机插值 + 走路摇晃** |

---

## 7. 本轮修掉的真实 bug（根因均已实测确认）

### v0.3 新增：⑧ ⑨ ⑩ ⑪ ⑫

#### ⑪ 打滑态把垂直速度整体归零 —— 空中进滑区永远落不了地，悬在离地 22cm

`StepSlip` 的 `LockToPlane` 分支写的是 `_vel -= _up * F3.Dot(_vel, _up)` —— 这行把**垂直分量整体**
（含向下的重力速度）抹掉，于是空中进打滑体积后既下不去也上不来；同时贴地探测
（`Sweep` 向下 0.1 m）因为角色悬空 22cm 而永远打空。
症状很有迷惑性：HUD 明写 `state=Slip`，但 `ground=False`、`feet=0.22` —— 数值上看"在打滑"，
画面里人浮在半空。修法：只压掉**向上**分量

```csharp
float upVel = F3.Dot(_vel, _up);
if (upVel > 0f) _vel -= _up * upVel;      // 只杀向上，保留下落
```

并把重力 / `MaxFallSpeed` 处理从 `else if (!_onGround)` 提到独立 `if (!_onGround)`，
让 `LockToPlane` 与空中都走重力分支。回归断言："贴地滑道上仍然踩在地上（ground=True，不悬空）"
→ 实测 `OnGround=True feet=0.001`。

#### ⑫ 截图抓帧落后于物理 tick —— 图上是 3.00 m/s，控制台是 8.00 m/s

`_save()` 里 `await RenderingServer.frame_post_draw` **只等一次**就 `get_texture().get_image()`，
Vulkan 下拿到的可能是"已提交但还没显示"的旧帧。而 HUD 文本在 `_Process` 里刷新、又比物理 tick
慢一拍 ⇒ PNG 上的 `speed=3.00` 与控制台 `speed=8.00` 对不上，视觉复核会得出错误结论。
修法：等**两次** `frame_post_draw` 再抓；所有 `_save(...)` 改成 `await _save(...)`；
`_save` 里打印抓帧前后两份 HUD 文本（`HUD@capture:` / `HUD@after:`）做交叉核对。
修完两份都是 `state=Slip speed=8.00 vy=0.00 ground=True`。

> 这条不是插件 bug，但**会让人误判插件有 bug** —— 所以同样记在这里。

#### ⑧ 攀爬贴墙代码从未生效 —— 点积符号写反

`float dist = Dot(hitPoint - _pos, normal)` 在玩家位于墙外时是**负数**，而 `want = 半径 + 0.02` 是正数，
`if (dist > want)` 永不成立 ⇒ 玩家停在离墙 1 m 处；下一 tick 的到顶探针（射程 0.7 m）必然打空，
于是 `wallGone = true` → 立刻 `DetachClimbAtTop()` 给 End boost。逐 tick 轨迹判据：第 1 tick 就
`state=Air, vy=5.437`，而 `sqrt(2 × 20.78 × 0.7112) = 5.4366` —— 精确等于 End boost 的抛体初速。
修法：`dist = Dot(_pos - hitPoint, normal)`，位移 `_pos += normal * (want - dist)`
（同一个式子既处理"贴太远"也处理"已经陷进去"）。

#### ⑨ 攀爬状态跳过了位移步骤 —— "按住 W 爬墙"原地不动

`Step()` 第 4 步 `if (_state == Climb) { StepClimb(input, dt); return; }` **提前 return**，
第 8 步 `MoveWithCollision` 与第 9 步 `UpdateGroundContact` 在攀爬时从不执行；而 v0.2 的
`StepClimb` 只赋 `_vel` 不位移 ⇒ 爬墙在原地抖动。修法：攀爬改运动学位移（照抄 `StepTraversal`），
每 tick 再贴墙钳制一次。

#### ⑩ 测试替身把"刚好贴住盒顶"当成没撞到 —— 上墙后失重、钻进墙里

`AnalyticWorld.Sweep` 的盒顶 / 盒底分支用 `f >= 0f` 判命中，而贴面时 `f` 因 float 舍入得 **-5e-7**，
被判成未命中（地面分支早有容差，盒子分支漏了）。于是翻越 / 攀爬把胶囊**恰好**放到台面上之后
`onGround` 立刻变 false，角色一边"空中加速"一边钻进墙体（实测终点 `y=0.976, z=-3.244` 在墙里）。
修法：`contactEps = 1e-4` + `if (f < 0f) f = 0f`，并新增"台面上站得住 / 走出边缘会掉到地面 /
不会卡进墙体内部"三条断言锁死。

### ① 站着却完全走不动 —— 贴地吸附把胶囊压进地面

旧版每 tick 给一个 `-GroundStickSpeed*dt` 的向下速度来"保证贴地探测命中"。
这会让胶囊每 tick 下沉 0.5mm，逐渐穿透地面；一旦穿透，胶囊扫掠在"起点已接触"时
返回 `fraction=0`，于是**水平移动也被一起挡住**。

**修法**：删掉这个速度。贴地完全由 0.2 m 探针 + 吸附完成。

### ② 疲劳跳高度为 0 —— 贴地吸附在上升途中执行

贴地吸附会把胶囊直接压到接触面。若在**上升途中**执行，它会把整段跳跃初速吃掉。
满速跳（初速 7.69）第一 tick 上升 0.128 m，侥幸躲过 0.20 m 吸附距离；
**疲劳跳**（初速 4.21）第一 tick 只上升 0.071 m，第二 tick 就被吸附回地面 ⇒ 高度 0。

**修法**：`if (_state == Air && VerticalSpeed > 0.1f)` 时跳过吸附与着地判定。

> 条件里**必须**带 `_state == Air`：上坡时把速度投影到坡面后 VerticalSpeed 天然为正
> （15° 坡、4.4 m/s → +1.14 m/s）。无条件判定会让上坡被误判成跳跃，
> 角色在坡上一路弹跳（实测 15° 斜坡只爬升 0.11 m 就停住）。

### ③ 19cm 台阶就卡死 —— 缺少"上台阶"逻辑

wiki 明写 Apex 可以"直接走过"约 30hu（0.762 m）的台阶，但**胶囊扫掠不会自动爬台阶**：
底部半球会楔在台阶上沿的下侧，直接向上抬只会加深穿插。

**修法**：实现 `TryStepUp` —— **退后一个半径 → 抬升 → 水平推进（最小 0.75×半径）→ 下落找面**。
"退后"是关键：让球面脱离接触，抬升才自由。

### ④ 网格线把角色卡住

测试场地的地面网格线 / 刻度 / 标签原本都是带碰撞的 `StaticBody3D`，
形成 1~2cm 高的"台阶"。**修法**：装饰几何改用 `_deco_box()`（只有 MeshInstance3D，无碰撞）。

### ⑤ 窗口翻越落点撞在窗楣里

落点前推距离（0.90 m）小于墙厚 + 半径，胶囊头部卡进窗楣。
**修法**：`WindowForwardOffset` 提到 1.20 m，并在落点做收缩 2 cm 的重叠检查。

### ⑥ 收枪散步被误判成"冲刺" —— 用速度反推意图

`Sprinting` 原本写成 `HorizontalSpeed > WalkSpeed * 1.05`（4.627 m/s）。
但**收枪行走** = 173.5 × 1.15 = 199 hu/s = **5.055 m/s**，天然超过这条线 ⇒
收枪散步时相机 FOV 多 3°、冲刺侧倾多 1.5° 全部误触发。
这个 bug 只在截图复核时才暴露（自检断言没覆盖"收枪行走"这个组合）。

**修法**：`Sprinting = 冲刺意图(input.SprintHeld) ∧ 地面 ∧ 真的跑起来了`。
保留速度条件是为了覆盖"冲刺途中被 Fallstun 压下来"的情况。
新增 2 项断言锁死：收枪行走 5.068 m/s 且 `Sprinting == false`。

### ⑦ 侧倾几乎不动 —— 把"度/秒"当成指数时间常数

二进制里 `slide_viewTiltIncreaseSpeed` / `_DecreaseSpeed` / `sprinttilt*` 是
**度/秒的线性斜率**（正因为是线性斜率，才会分成"增大速率"和"减小速率"两个字段）。
但代码用 `Approach()`（指数逼近）消费它，于是 60°/s 被当成时间常数
1/1.047 ≈ 0.95 s —— 侧倾要 1 秒才到位。实测滑铲 0.2 s 时只到 **1.5°**（目标 5°）。

**修法**：新增 `MoveTowards()`（每帧最多走 `速率 × dt` 的线性趋近），侧倾改用它。
修后同样时机实测 **5.5°**，10 m/s 滑铲满值 5.00°，冲刺侧倾 2.17°。

> 教训：**"速率"这个词有歧义**。指数逼近的速率是「1/秒」，线性趋近的速率是「单位/秒」。
> 二进制字段名带 `Speed` 且成对出现（Increase/Decrease）时，几乎一定是线性斜率。

### 附带修掉的三个问题

| 问题 | 现象 | 修法 |
|---|---|---|
| `AnalyticWorld.Sweep` 把"已在表面上"当成没撞到 | 角色每 tick 被吸附下压 0.5mm 后掉进"探测不到地面"的黑洞 | 按 Godot `CastMotion` 语义返回 `fraction=0` 的命中 |
| 窗口扫描的探针上限只到头顶上方 0.4m | 窗楣在 2.5m 时扫不到，被误判成"没有窗楣" | 提到头顶上方 0.9m |
| `Sprint`/`Walk` 共用一个速度字段 | 无法表达"持枪 260 / 收枪 299" | 拆成 `SprintSpeed` + `HolsterMultiplier` |

> ⑥⑦ 都是**截图视觉复核**才发现的：自检断言只看数值、不看画面，
> 而这两个 bug 恰好是"数值在容差内、观感却错了"。这就是本轮坚持逐张 `read_image` 的原因。

---

## 8. 自检结果（实测）

### A. 确定性 + 功能 + 相机 + 攀爬/tick/探针 + 打滑/扒墙（`determinism_test.tscn`，122 项断言）

```
=== 跨帧率确定性 ===
  参考（60fps）tick 数 = 240
  60fps vs 30fps:  一致（240 ticks，逐位相等）
  60fps vs 90fps:  一致（239 ticks，逐位相等）
  60fps vs 144fps: 一致（239 ticks，逐位相等）
  60fps vs 240fps: 一致（240 ticks，逐位相等）
  60fps vs 500fps: 一致（240 ticks，逐位相等）

=== 功能验证（36 项，全 PASS）===
  行走 4.407 / 冲刺 6.604 / 收枪冲刺 7.595 / 收枪行走 5.068（且不误判冲刺）/ 蹲行 2.032
  跳跃高度 1.487（期望 1.422 ± 0.10）、滞空 0.717s（wiki 0.74 ± 0.06）
  空中无摩擦（40 tick 速度差 0.00E+000）、Air strafe 加速 +0.83 m/s
  Slideboost 11.51 m/s（含收枪 ×1.15 上限 11.68）、2s 冷却、Deadslide、连续滑铲跳 3 次
  跳跃疲劳 0.462m（满高 1.422）、0.75s 后恢复
  四档翻越 Below/Level/Above/High、6m 攀爬、7.5m 超限拒绝、窗口翻越
  Fallstun、落地四档分档、重力与 wiki 双数据自洽（g=20.7801 vs 20.7802）

=== 相机子系统（23 项，全 PASS）===
  FOV 90 基准 / 冲刺 93 / 滑铲 96 / 夹紧 120 / 玩家可调 ±2°
  tan 空间插值 vs 线性：中途差 1.599°，终值差 0.0001°
  落地踢：瞬间低头 −0.0182 rad，1.5s 后回正
  v0.3 收敛：Hard 2.49° / Low 0.47°（设计带 1°–5°）；MaxKickAngle 未成为工作点（0.0435 < 0.075×0.9）
  落地踢按档位单调（0.0082 < 0.0180 < 0.0294 < 0.0435）
  滑铲侧倾随速度增大（5m/s→3.12°，10m/s→5.00° 满值）、冲刺侧倾 1.95°
  走路摇晃：静止 0 / 移动 0.0281 m / 空中 0
  翻越相机脱开→回贴（0.997 → 0.000）、高度上限压低 0.65 m
  FOV 收敛与帧率无关（60/144/240fps 差 < 0.0005°）

=== v0.3 攀爬 / tick / 探针（29 项，全 PASS）===
  非强制：地面贴墙按 W 120 tick 不进 Climb；空中但朝墙速度 0 → 不攀爬
  成立则进 Climb（朝墙速度 6.0），挂上瞬间 vy=0；按 W 0.5s → +1.000 m（ClimbSpeed 2.0）
  松手宽限 13 tick 内仍 Climb，超时转 Air（vy=-7.28）
  到顶先进「补爬」（ClimbApproaching），再进 Hang（挂姿脚底 1.750，墙顶 2.00）
  Hang 里按 W 上墙 → 脚底 2.000；台面上站得住 / 走出边缘掉到地面 / 不卡进墙体
  同一面墙爬过后不能在同一高度以上立刻重附着
  tick：TickRate=60 / FixedDelta=1/60 / 30fps 每帧 2 tick / 144fps 10s=599 tick / 卡顿最多补 4
  探针：TraversalProbeInterval=2（60 tick 只探 30 次）/ ClimbProbeInterval=1

=== v0.3 打滑 / 扒墙（34 项，全 PASS）===
  打滑：进球 → Slip；速度拉到 8.00 m/s；方向 = 体积方向(+X)；HUD Slipping=True
        贴地滑道上仍踩在地上（OnGround=True feet=0.001，**不悬空**）
        没配方向 → 从球心往外推（vx=6.00）；滑出体积回 Ground
        方向限制计时器自行衰减到 0；ClampToCone 三例（内→原样 / 90°外→35.00° / 反向→退回轴）
        滑区里蹲 → Slide（speed=8.87）/ 滑区里跳 → Air（vy=7.69）
        探针 SlipProbeInterval=2，60 tick 只采样 30 次
  扒墙：不按蹲不挂；按蹲+贴墙 → WallHang（speed=0.00 vy=0.00，贴墙 z=-1.574，grip=0.99）
        松蹲 → Air；W 上爬 Δy=1.00；D 横移 Δx=0.80；不动下滑 Δy=0.22（grip=0.81）
        握力耗尽脱手；W 回补 grip 0.81→0.90；超 WallHangMaxTime(6s) 脱手
        按跳 → 蹬墙跳（vz=2.69 vy=6.15）；甩视角 90° → 松手
        矮墙(1.5 < AutoMantleMaxHeight 2.03)不挂（automantle_disable_hang）；离墙 2.5m 挂不上
        探针：不按蹲零开销；按住蹲 40 tick 探 40 次

=== APEX MOVEMENT SELFTEST PASS ===
```

### B. 真实物理集成（`integration_test.tscn`，44 项断言）

```
  [PASS] 落地 / 脚底贴地 0.001 / 站立身高 1.950 / 相机高度 1.850 / 基础 FOV 90.000
  [PASS] 行走 4.407 / 平均位移 4.284 / 奔跑 6.604 / 收枪奔跑 7.595
  [PASS] 蹲下 0.975 / 脚底不下沉 / 相机高度 0.875 / 站起恢复
  [PASS] 跳跃高度 1.488 / 跳后重新落地
  [PASS] 滑铲触发 9.28 m/s / Slideboost / 相机侧倾 5.73° / 抬 FOV 94.20°
  [PASS] 连续滑铲跳 3 次
  [PASS] 翻越分档 Below(0.9m) / Level(1.7m) / Above(3.0m) / High(6.0m)
  [PASS] 6m 攀爬 6.000 / 爬上 15° 斜坡 / 翻越 0.8m 矮墙
  [PASS] 攀爬不强制：地面顶墙按 W 不进 Climb（x=48.594 被挡在墙面外）
  [PASS] 跑向墙 + 空中按 W → 挂上 Climb（x=48.574）
  [PASS] 按住 W 0.75s → Δfeet 1.500 m（ClimbSpeed 2.0）
  [PASS] 松开 W → 取消攀爬并下落（state=Air, vy=-5.895）
  [PASS] 窗口翻越（state=Window）/ 穿过开口
  [PASS] 低天花板下无法站起 / 离开后站起
  [PASS] FOV 可调 100°/90° / 视角踢可触发 / 掉出世界自动复活

=== INTEGRATION PASS（44 项断言） ===
```

---

## 9. 测试场地

程序化生成（`testbed_builder.gd`）。**参与碰撞的几何全部是 `StaticBody3D`**（静态，
保证 tick 内查询确定性）；网格线/刻度/标签是 `_deco_box()`（纯 Mesh，无碰撞）。

| 区域 | 坐标 | 用途 |
|---|---|---|
| 大平地 | 200×200m，每 10m 网格线 | 基础移动 / 加速 / 摩擦 / 收枪 |
| 三条斜坡 | x∈[-48,-2], z∈[-55,-35]，15°/30°/45° | 爬坡 / 上台阶 / 下坡贴地 / 坡上滑铲 |
| 攀爬台阶梯 | x∈[-34,22], z=35，**4 档全覆盖**（0.6/0.9/1.3/1.7/2.4/3.0/4.5/6.0m） | Below/Level/Above/High 分档 + 6m 攀爬 |
| 翻越低墙 | x∈[-20,-8], z=50，高 0.4/0.8/1.0m | Below 档的"翻越"外观 |
| 窗口框架 | x∈[20,32], z=68，窗台 0.6/0.9/1.2m + 窗楣 4.5m | TRAVERSAL_WINDOW |
| 贴墙爬墙 | x=50, z∈[-12,12]，高 8m | Climb 状态 / attach offset / end boost |
| 低天花板 | x=-60, z∈[15,25]，净高 1.1m | 蹲下才能进 / 站起被头顶挡住 |
| 滑铲长道 | x=-70, 80m 带 10m 刻度 | 滑铲距离 / 连续滑铲跳 |
| 打滑体积 | x=80, z∈[-7,7]，半径 7m（`slip_volume` 组） | Slip 状态 / 方向限制 / 滑出回 Ground |
| 落差平台 | x∈[20,44], z=55，高 3/6/10/22m | 落地四档 + Fallstun |

---

## 10. 已知偏差与待校准项

| 项 | 状态 |
|---|---|
| `airAcceleration = 500` 的单位语义 | **未解**。若直接套 Source 公式会得到 12.7 m/s/tick（荒谬），说明 Apex 内部另做了缩放，该系数没读到。本插件把它归一化为可调的 `AirAccel`（B 级），并以 `AirWishSpeed = 60hu/s`（A 级）作为硬约束 |
| 相机数值（FOV 增量 / 弹簧刚度 / bob 幅度 / 侧倾角） | **全部 C 级**。逆向只证明字段存在，数值在 BSS 里读不到；wiki 也没有这些数据 |
| 走路摇晃 | Apex **没有** `cl_bob` 类 cvar（逆向负结论），摇晃来自第一人称动画。本插件自研正弦步频模型，纯手感参数 |
| `cl_fovScale` → 显示 FOV 度数的换算公式 | **未读到**。本插件直接用水平 FOV 度数，`FovBase` 可调 |
| Fallstun 精确公式 | wiki 自己标注"不确定"，本实现用二次曲线 |
| 7 种 MANTLE 分类 | 二进制里有 **10 个** `ACT_VM_MANTLE_*`（4 主档 × 蹲姿变体 + IDLE + JUMPOFF）。本插件实现 4 档 + grab/pull 两段 + 可选挂边，**蹲姿变体未实现** |
| Superglide / Wallbounce / Wallrun / 滑索 / 钩爪 / Tap strafe / Lurch | **未实现**（不在本次需求内）。逆向字段（`superjump*`、`wallrun_*`、`zipline*`、`grapple*`）已记录在 `docs/reverse-engineering-v0.2.md`，v0.3 可直接接 |
| `ClimbNoInputGrace = 0.22s` / `ClimbHangApproachDistance = 0.35m` | **自研手感参数**（C 级）。wiki 只说"向上爬 untimed、静止 1s 才脱离"，没给松手宽限；0.22 s 是为了"点一下 W 也能爬一小段"而不至于一松手就掉 |
| 打滑态（`slipSpeed` / `IsSlipping` / `m_slipAirRestrictTime`） | **已实现**（见 6.1 ④）。`SlipSpeed` / `SlipAirRestrictConeDeg` 是 C 级手感参数 —— 逆向只证明字段存在，具体数值读不到 |
| 独立扒墙 / dangle 状态机（`wallhang` / `IsWallHanging` / `ACT_MP_WALLHANG_*`） | **已实现**（见 6.1 ⑤）。`WallHangGrip` 消耗/回补速率、`WallHangMaxTime` 为 C 级手感参数 |
| 移动平台速度继承 | **未实现**（需求范围外） |
| 动画 | 无（纯物理位移 + 相机效果） |

---

## 11. 截图（逐张视觉复核过）

**20 张 1920×1080**，全部位于 [`../../docs/reference/apex_movement/`](../../docs/reference/apex_movement/)。
每张第一人称图的 HUD 会打印当时的 `fov / roll / state / vy / xBlend`，可对着数字核验。

| 文件 | 内容 | 图上可核验的数字 |
|---|---|---|
| `shot_overview.png` | 场地全景 | 状态 `Ground`、`h=1.95` |
| `shot_ramps.png` | 三条斜坡（15°/30°/45°） | — |
| `shot_mantle_steps.png` | 攀爬台阶梯 8 档，颜色分 4 档 | — |
| `shot_vault_walls.png` | 翻越低墙 0.4/0.8/1.0m | — |
| `shot_window_frames.png` | 窗口框架（窗台 + 窗楣） | — |
| `shot_climb_wall.png` | 装墙（8m，带刻度线） | — |
| `shot_slide_lane.png` | 滑铲长道（80m，10m 刻度） | — |
| `shot_drop_plats.png` | 落差平台 3/6/10/22m | — |
| `shot_player_fp.png` | 站立第一人称基准 | `fov=90.0°`、`roll=0.0°`、`bobPhase=0.00` |
| `shot_player_fov110.png` | FOV 拉宽 | `fov=109.9°` |
| `shot_player_fov70.png` | FOV 收窄 | 视野显著变窄（目标 70°） |
| `shot_player_walking.png` | 走路摇晃 + 无侧倾 | `speed=5.07`、`roll=0.2°`、`bobPhase=5.26` |
| `shot_player_slide.png` | 滑铲：侧倾 + 抬 FOV + 蹲高 | `state=Slide`、`speed=9.89`、`h=0.98`、`fov=95.1°`、`roll=5.4°` |
| `shot_player_mantle.png` | 翻越途中：相机滞后 + 高度上限 | `state=Mantle`、`mantle=High/Grab`、`xBlend=1.00`、`fov=86.4°` |
| `shot_player_climb.png` | **v0.3 攀爬状态**（3m 台阶，Above 档） | `state=Climb`、`speed=0.00`、`vy=2.00` —— 静止水平 + 只有按 W 才 2 m/s 上爬 |
| `shot_player_hang.png` | **v0.3 扒墙**（补爬结束挂边） | `state=Hang`、`speed=0.00`、`vy=0.00` —— 挂住不自己动 |
| `shot_player_climb_top.png` | **v0.3 挂边上墙**（站到 3m 台面） | `state=Ground`，控制台同时打印 `CLIMB TOP feet=3.00` |
| `shot_slip_zone.png` | **v0.3 打滑场地**（地面绿色圆盘 + 进出参考色条） | HUD `slip=no hang=no`（玩家不在圈内） |
| `shot_player_slip.png` | **v0.3 打滑态**（站在圆盘里被推着走） | `state=Slip`、`speed=8.00`、`ground=True`、`slip=SLIP dir=0.00,1.00` |
| `shot_player_wallhang.png` | **v0.3 扒墙**（第三人称看挂点与高度） | `state=WallHang`、`speed=0.00`、`vy=0.00`、`hang=WALLHANG grip=0.95 t=0.25s` |

> 攀爬三张（`climb` / `hang` / `climb_top`）是同一条链路的三个阶段：
> **挂墙 → 爬 → 补爬 → Hang → 按 W 上墙**。
>
> `shot_player_wallhang.png` 是**唯一的第三人称图**：第一人称挂在墙上时脸离墙面只有 0.43 m，
> 且 `WallHangMaxViewAngle`(45.57°) 限制视线偏离法线，无论怎么调都只能看到一片墙。
> 为此截图脚本临时生成一个**纯视觉**胶囊替身（`_build_proxy()`，无碰撞体、不参与移动），
> 按玩家真实位置同步后拍完即隐藏。
>
> 全部由 `testbed/shot_testbed.gd` 自动驱动按键与相机，不是摆拍。

---

## 12. 文档

- [`docs/wiki-reference.md`](docs/wiki-reference.md) —— **社区 wiki 数值换算表**（1hu=2.54cm、速度表、机制说明，每条带来源 URL）
- [`docs/reverse-engineering-v0.2.md`](docs/reverse-engineering-v0.2.md) —— **本轮逆向报告**（相机/FOV/晃动/翻越/滑铲/攀爬字段的 A 级证据 + 复现命令）
- [`docs/reverse-engineering.md`](docs/reverse-engineering.md) —— v0.1 逆向报告（PE 布局 / cvar 注册点 / 7 种 MANTLE 枚举）
- [`docs/selftest/v0.3-determinism.txt`](docs/selftest/v0.3-determinism.txt) —— **自检 A 实测原始输出**（122 项，逐条 PASS）
- [`docs/selftest/v0.3-integration.txt`](docs/selftest/v0.3-integration.txt) —— **自检 B 实测原始输出**（44 项，逐条 PASS）
- `../../docs/design/PLUGIN-CONVENTION.md` —— 插件化开发约定
- 截图：[`../../docs/reference/apex_movement/`](../../docs/reference/apex_movement/)
