# ApexMovement 插件 v0.4.1

**自研移动系统**（固定 tick 确定性）+ 相机子系统 + 可玩测试场地。
起点是逆向 Apex Legends **v3.0.3.65**（只读分析，未修改游戏安装目录）并按
[Apex Movement Wiki](https://apexmovement.tech/wiki) 标定的数值；
**从 v0.4 起不再对标 Apex** —— 手感规则按需求方口径重写，Apex 数值只作参考。

---

## 1. 一句话

复现 Apex 的移动手感（WASD / 蹲 / 跳 / 冲刺 / 收枪 / 滑铲 / 滑铲跳 / 连续滑铲跳 /
四档翻越 / 窗口翻越 / 贴墙爬 / 空中转向 / 跳跃疲劳 / Fallstun / FOV / 镜头晃动 / 走路摇晃），
但把**画面帧判定改成游戏 tick 判定** —— 同样的输入序列在 30 / 60 / 144 / 240 fps 下
产生**逐位相同**的轨迹。

v0.4.1 的两个主打变化（在 v0.4 之上）：

1. **滑铲按真实物理走**：沿坡合加速度 `a = g·sinθ − μ·g·cosθ`。平地匀减速、坡越陡越滑越快、
   临界角 `tanθ = μ` 一过就再也停不下来。摩擦系数 μ **按地面材质取**（`IFrictionWorld`），
   默认 0.45，冰面 0.05 / 粗糙 0.90 只是给碰撞体挂一个 meta，不用改求解器。
2. **修掉「翻越 1.3 m 平台时悬空卡死」**：扫掠在接触边缘拿不到真实法线时会退回
   「运动方向反向」的伪法线，把水平与竖直速度一起吃掉 —— 人悬在半空、vy 掉到 −41 而纹丝不动。

v0.4 的五个主打变化：

1. **镜头抖动改为推导出来**：由「身高 → 腿长 → 偏好步速 → 步频/步幅 → 腾空比 → 起伏与点头」一条链算出来，
   频率与幅度不再是手拍常数；滑铲镜头**与下蹲完全一致**（侧倾 / FOV 加宽 / 额外下沉全部删掉）。
2. **攀爬不再是一按 W 就吸附**：地面贴墙按 W 只会撞停；必须 `W` 贴墙 + `Space` 跳起才抓墙。
3. **扒墙默认化**：空中下落贴墙**不需要按任何键**就会自动扒住；攀爬必须持续按 `W`，松 `W` 就转为扒墙；
   扒墙中无输入 1.2 s 掉落、`A/D` 横移、`W+A/W+D` 斜移、`S` 立刻松手垂直下落、下落中按 `W` 可再扒住。
4. **三档障碍分级**：≤0.25 m 台阶直接迈过（只改方向不改速度大小）/ 0.25–1 m 小障碍 `W+Space` 直接翻越 /
   >1 m 是墙，撞停后靠 `W+Space` 攀爬。
5. **`\` 键调试菜单**：FOV / 鼠标灵敏度 / 步态摇晃倍率 / 落地冲击倍率 / 相机效果开关 / HUD 开关，实时生效。

---

## 2. 操作

| 按键 | 动作 |
|---|---|
| `W A S D` | 移动 |
| `Space` | 跳跃；地面朝小障碍按 = 翻越（见下） |
| `Ctrl` | 蹲下 |
| `Shift` | 冲刺 |
| `Shift + Ctrl` | 滑铲（需 ≥5.08 m/s）。**镜头与下蹲完全一致**：无侧倾、无 FOV 加宽、无额外下沉。**v0.4.1 起按真实物理减速**：`a = g·sinθ − μ·g·cosθ`，平地被 μ 刹住、下坡越陡越滑越快、μ 由地面材质决定 |
| `Shift + Ctrl + Space` | 滑铲跳 |
| **在坡上滑铲** | 30° 坡（`tanθ=0.577 > μ=0.45`）**越滑越快**；15° 坡（`tanθ=0.268 < μ`）仍会被刹停。临界角 `tanθ = μ` = 24.23° |
| **在冰面 / 粗糙地面滑铲** | μ 由碰撞体 meta `friction` 决定（冰 0.05 / 默认 0.45 / 粗糙 0.90）；同样起滑条件下 0.5 s 后速度 11.06 / 6.07 / 2.34 m/s |
| 反复「滑铲 → 跳 → 落地 → 滑铲」 | 连续滑铲跳 |
| **走向 ≤0.25 m 台阶** | 直接迈过去：**无速度衰减**，水平速度临时变成斜向上再变回水平，只改方向不改大小 |
| **走向 0.25–1 m 小障碍 + `W` + `Space`** | **直接翻越**（`Vault`，不触发攀爬）；翻完 0.25 s 内加速倍率 ×3，快速回到满速 |
| **走向 >1 m 墙 + 只按 `W`** | 撞墙停住 / 贴着墙朝 W 方向蹭 —— **不会被吸上墙** |
| **`W` 贴墙 + `Space` 跳起** | 起跳后抓住墙，进入攀爬（`Climb`） |
| 攀爬中**按住 `W`** | 以 2 m/s 上爬；**松开 `W` → 转入扒墙（`WallHang`），不是直接掉下来** |
| **空中下落贴墙（不按任何键）** | **自动扒住（扒墙默认化）**；上升途中不会被强行抓住 |
| 扒墙中 `W` / `A` `D` / `W+A` `W+D` | 上爬 2 m/s（同时回补握力）/ 横移 0.8 m/s / 斜向移动 |
| 扒墙中**完全无输入** | 1.2 s 后握力耗尽掉落（`WallHangIdleFallTime`） |
| 扒墙中 `S` | **立刻取消扒墙、垂直下落**；下落过程中按 `W` 可以重新扒住 |
| 扒墙中 `Space` | 蹬墙跳（离墙 + 向上） |
| 爬到墙顶（有台面） | 自动补爬 0.35 m 进入 `Hang`，再按 `W` 上墙 |
| **走进打滑体积** | 打滑（`Slip`）：被推向 8 m/s；蹲下优先转滑铲、按跳优先起跳 |
| **`\`** | **打开 / 关闭调试菜单**（FOV、鼠标灵敏度、步态摇晃倍率、落地冲击倍率、相机效果、HUD） |
| `H` | 收枪 / 持枪切换（收枪 = ×1.15 速度） |
| `[` / `]` | FOV −2° / +2° |
| `R` | 回到出生点 |
| `Esc` | 释放鼠标（调试菜单打开时 = 关闭菜单） |

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
| 台阶档上限 | **0.25 m** | **需求方口径（v0.4）**：≤0.25 m 直接迈过、无速度衰减 |
| 小障碍翻越上限 | **1.0 m**（`VaultMaxHeight`） | **需求方口径（v0.4）**：(0.25, 1.0] m 按 `W+Space` 直接翻越 |
| 翻越速度保留 / 恢复 | ×0.85，落地后 0.25 s 内加速倍率 ×3 | **需求方口径（v0.4）**：「翻完快速恢复满速」 |
| 步态模型常数 | 腿长 0.53×身高 / Froude 0.45 / 走跑切换 1.9×v_pref | 人体测量学 + 步态文献（v0.4，见 `core/GaitModel.cs`） |
| 摇晃舒适档 | 垂直峰到峰 ≤2.5 cm、点头 ≤0.8°、落地冲击 1.5–2.5° | **需求方口径（m02752）** |
| Fallstun | 7.62 m（300 hu）起算 → 20.32 m（800 hu）满值 1 s，二次曲线 | wiki Fallstun |
| 打滑速度 / 方向限制锥 | 8 m/s / 35° | **C 级手感参数**（逆向只证明 `slipSpeed`、`m_slipAirRestrictDirection` 字段存在） |
| 扒墙爬升 / 横移 / 下滑 | 2.0（`ClimbSpeed`）/ 0.8 / 0.22 m/s | **需求方口径（v0.4）**：上爬与攀爬同速，一个旋钮一个方向 |
| 扒墙无输入掉落 / 横移流失 / 上爬回补 | 1.2 s / 6 s / 2.5 s | **需求方口径（v0.4）**：握力 0–1，三种流失/回补各有时间常数 |
| 滑铲摩擦系数 μ | **0.45**（`SlideFrictionCoefficient`） | **需求方口径（v0.4.1）**：默认值；地面挂了 meta `friction` 时按材质取（冰 0.05 / 常规 0.45 / 粗糙 0.90） |
| 滑铲坡度加速度 | `a = g·sinθ − μ·g·cosθ` | **需求方口径（v0.4.1）**：μ=0.45 ⇒ 临界角 24.23° |

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

# 自检 A：跨帧率确定性 + 43 项功能（含滑铲物理 / 地面材质）+ 24 项相机
#         + 47 项攀爬/tick/三档 + 23 项步态推导 + 35 项打滑/扒墙（纯数学，无引擎）= 172 项断言
"$GODOT" --headless --path . res://addons/apex_movement/tests/determinism_test.tscn
# 期望：=== APEX MOVEMENT SELFTEST PASS ===

# 自检 B：49 项真实物理集成断言（跑真实 Godot 物理 + 真实场地）
"$GODOT" --headless --path . res://addons/apex_movement/tests/integration_test.tscn
# 期望：=== INTEGRATION PASS（49 项断言） ===

# 截图（需要窗口/真实渲染）
"$GODOT" --path . --rendering-driver vulkan --resolution 1920x1080 \
         res://addons/apex_movement/testbed/shot_testbed.tscn
# 出 30 张图到 user://shot_*.png：
#  11 张自由机位（overview / ramps / mantle_steps / vault_walls / step_stairs /
#                  window_frames / climb_wall / slide_lane / drop_plats / slip_zone /
#                  friction_lanes）
#  17 张玩家视角（fp / fov110 / fov70 / walking / slide / mantle /
#                  climb / hang / climb_top / slip / wallhang / steps /
#                  slide_slope / slide_slope_fast /
#                  friction_ice / friction_norm / friction_rough）
#   2 张调试菜单（debug_menu / debug_menu_fov110）
#   ← climb / hang / climb_top 是攀爬三阶段；slip / wallhang 是打滑与扒墙
#   ← step_stairs / steps 是 v0.4 三档障碍第一档（≤0.25m 台阶）
#   ← slide_slope* 是 v0.4.1 的"下坡越滑越快"；friction_* 是三种地面材质
#   wallhang / slide_slope* 是第三人称图（第一人称贴墙只有一片墙；
#   第一人称下坡时画面全是坡面，看不出倾角）
#   HUD 会打印当时的 fov / roll / state / gait / step / speed —— 这是相机、步态
#   与滑铲物理效果的视觉证据
```

**为什么需要两套自检**：`determinism_test` 跑在 `AnalyticWorld`（纯数学碰撞替身）上，
覆盖不到 Godot 桥接层与相机应用层。历史上「无法移动」「Ctrl 穿地」「跳跃初速被吸附吃掉」
三个 bug 都只存在于桥接层，AnalyticWorld 完全复现不出来。

---

## 5. 架构

```
core/                        ← 纯 C#，不引用 Godot（可脱离引擎单元测试）
  F3.cs                       确定性三维向量（标量 float，不走 SIMD）
  GaitModel.cs                步态模型：身高 → 腿长 → 步频/步幅/腾空比 → 起伏与点头（v0.4 新增）
  MovementConfig.cs           全部参数 + 证据等级 + Apex/Legacy 双预设
  MovementTypes.cs            状态/翻越档位/落地档位/输入/快照/世界接口（ISlipWorld 打滑 / IFrictionWorld 地面材质）
  ApexMovementCore.cs         求解器（状态机 + 加速/摩擦/跳跃/滑铲物理/三档障碍/攀爬+扒墙统一状态机/打滑/疲劳/Fallstun）
  ApexTickClock.cs            固定 tick 累加器（含死亡螺旋防护）

camera/                      ← 纯 C#，相机效果（可脱离引擎单元测试）
  CameraEffectsConfig.cs      FOV / 弹簧视角踢 / 冲刺侧倾 / 翻越插值 / 步态摇晃倍率 参数
  ApexCameraRig.cs            每帧算出 FOV / roll / pitch-yaw 偏移 / 眼位偏移

godot_bridge/
  GodotMovementWorld.cs       IMovementWorld + ISlipWorld + IFrictionWorld 的 Godot 实现
                              （CastMotion / GetRestInfo / QueryNormalAt / Raycast / SampleSlip / SampleFriction）

player/
  ApexPlayer.cs               玩家节点：输入累积 + 固定 tick 驱动 + 相机应用 + HUD
  ApexDebugMenu.cs            `\` 调试菜单（FOV / 灵敏度 / 摇晃 / 冲击 / 开关，事件回调解耦）
  apex_input_map.gd           输入动作自注册（不污染宿主项目）

testbed/
  testbed_builder.gd          程序化生成测试场地
  apex_testbed.tscn           可玩入口
  shot_testbed.gd/.tscn       多视角截图工具

tests/
  AnalyticWorld.cs            纯数学碰撞世界（测试替身，契约与 Godot 桥接层一致）
  DeterminismTest.cs          跨帧率确定性
  FeatureTest.cs              43 项功能断言（纯数学，含 v0.4.1 滑铲物理与地面材质）
  CameraTest.cs               24 项相机断言（纯数学）
  ClimbTest.cs                47 项攀爬/三档障碍/tick/探针断言（纯数学）
  GaitTest.cs                 23 项步态模型推导断言（纯数学，v0.4 新增）
  SlipHangTest.cs             35 项打滑/扒墙断言（纯数学）
  determinism_test.tscn       自检 A 入口
  integration_test.gd/.tscn   49 项真实物理集成断言（自检 B，含下坡滑铲与 1.3m 卡死回归）
```

### 关键设计：帧率无关

| 环节 | 做法 |
|---|---|
| 状态推进 | 只在固定 tick 内发生，`dt` 恒为 `1/TickRate` |
| 鼠标增量 | 渲染帧**累积**，tick 边界**整份消费**（总量守恒 ⇒ 累计转角一致） |
| 浮点 | 只用标量 `float` 加减乘除 + `MathF.Sqrt`，不用 SIMD 归约 |
| 走路摇晃 | 步频相位在 **tick 内**推进（不在渲染帧），所以摇晃也帧率无关 |
| 步态推导 | 每次 tick 由 `GaitModel.Evaluate(速度, 身高, 冲刺速度, 蹲伏量)` 求值写进快照，相机只读不拍 |
| 滑铲物理 | `a = g·sinθ − μ·g·cosθ` 在 tick 内按 `dt` 积分；θ 由 `_groundNormal` 与 up 的夹角给出，μ 由 `IFrictionWorld` 按地面材质取 |
| 卡顿 | 单帧最多追赶 4 tick，超出丢弃并计数（防死亡螺旋） |

### 与 ProjectA 的边界

`core/` 与 `camera/` 不依赖 ProjectA 的任何类型。重力方向通过 `SetUp(F3 up)` 注入，
默认世界 Y 轴向上；将来接入球形行星时每 tick 传入行星局部 up 即可复用。

---

## 6. 版本变化史

### 6.1 v0.4.1 相比 v0.4（滑铲物理化 + 悬空卡死修复）

需求原话：「滑铲要按物理走（坡道越陡越快）」，以及一张「1.3 m 平台翻越时角色悬在半空、
W 按着也上不去」的截图。

**① 滑铲改成真实物理（`a = g·sinθ − μ·g·cosθ`）**

v0.4 及之前：滑铲用一条与坡度无关的 `SlideFriction = 0.9` 做指数衰减，
所以**下坡反而比平地减速更快**，与直觉完全相反。

v0.4.1 的解法（`ApexMovementCore.StepSlide`）：

```
a = g·sinθ − μ·g·cosθ          θ = 地面法线与 up 的夹角
v -= a·dt  （沿坡面切线方向）
```

- **平地**（θ=0）：`a = −μ·g`，匀减速 —— 与旧行为量级一致，手感不突变。
- **下坡**（θ>0）：`g·sinθ` 把速度往回加。θ 越大加速越猛 —— 这就是「坡越陡越快」。
- **临界角**：`tanθ = μ`，μ=0.45 ⇒ **24.23°**。比这更陡的坡上，滑铲**永远停不下来**
  （只受 `SlideMaxSpeed = 10.16 m/s` 封顶）。
- **上坡**（θ<0）：两项同向减速，滑铲很快被 `SlideMinSpeed = 2.5 m/s` 终止。

**② μ 按地面材质取（`IFrictionWorld`）**

需求原话：「保留这套公式，默认 0.45，摩擦系数改成可调的，便于游戏未来添加不同材质不同摩擦系数的地面」。

```csharp
public interface IFrictionWorld
{
    bool SampleFriction(F3 position, out float coefficient);   // 取不到就返回 false
}
```

- 求解器每次滑铲 tick 先问世界「脚下这块地 μ 是多少」，问不到才用 `SlideFrictionCoefficient = 0.45`。
- Godot 侧实现（`GodotMovementWorld.SampleFriction`）：沿 up 打一条 2 m 射线，
  从命中节点起沿父链逐级找 meta `friction`（float，最多 4 层）。
  **场地里加材质只是给 `StaticBody3D` 挂一个 meta，不用改求解器、不用重编译**。
- 测试场地新增三条材质带（`friction_lanes`）：冰 `0.05` / 常规 `0.45`（不挂 meta） / 粗糙 `0.90`。

**③ 不自动滑**（需求答复：「不自动，必须先蹲下滑铲」）
滑铲入口仍是 `Shift` 冲刺 + `Ctrl`，`BeginSlide` 前置条件没动。物理化只改了"进入之后怎么减速"。

**④ 修掉「1.3 m 平台翻越时悬空卡死」**

根因不在翻越逻辑，在碰撞桥接层的法线兜底：

```
GodotMovementWorld.QueryNormalAt(contactPos, r, hh, fallback)
  → GetRestInfo 在"扫掠停在接触面边缘"时打空（ProbeDepth 只有 2 cm，胶囊其实还没接触）
  → return fallback.Normalized()          ← fallback = -delta.Normalized()
```

于是法线变成「运动方向的反向」这种伪法线。`MoveWithCollision` 拿它做
`_vel = F3.ProjectOnPlane(_vel, normal)`，把**水平与竖直分量一起吃掉** ⇒
人悬在半空、`vy` 一路掉到 −41 而位置纹丝不动（实测轨迹：
`t=67 post vy=-0.000 vz=-0.000` 之后每 tick `post y` 恒定 1.8466）。

修法（`GodotMovementWorld.QueryNormalAt`）：`GetRestInfo` 打空时不再直接返回伪法线，而是两级兜底：

1. **原地把胶囊整体放大 `ProbeDepth`**（半径与两端半球同时外扩 2 cm，不位移）再查一次。
   保证与"刚刚接触的那个面"产生 2 cm 的重叠，从而拿到真法线；不位移所以法线仍是接触点的法线。
   典型场景（胶囊圆柱段贴着竖直墙面往上滑）走的就是这一级。
2. 仍取不到则退回**"竖直墙"启发式**：把 `-fallback` 投影到水平面再归一化，只挡水平分量。
   宁可当成墙（最差只是少走一点）也不要用 `-delta` 当法线（会把竖直速度一起抹掉、永久卡死）。

**⑤ 顺带修掉的分段攀爬**：到顶脱手不再被"反作弊黑名单"永久拉黑，高墙可以分多段爬上去
（根因与修法见 §7 的 ㉑）。

---

### 6.2 v0.4 相比 v0.3（**自研手感，不再对标 Apex**）

需求原话：「从现在开始，不参照 apex 的移动系统，而是打造我们自己的移动系统」。
所以这一节不是"把 Apex 抄得更准"，而是**按需求方口径重写手感规则**，Apex 数值只当参考。

**① 镜头抖动：从"手拍常数"改成"步态模型推导"（需求 1）**

v0.3 的摇晃是拍出来的：`步频 = 1.2 + 速度 × 0.55` Hz、`幅度 = clamp(速度/5, 0, 1.4) × 26 mm`。
两个问题：① 频率与身高、步幅毫无关系，冲刺时算出 **4.83 Hz** —— 人类跑步步频上限约 3.3 Hz，
这个数字物理上不存在；② 幅度随速度线性堆到 73 mm 峰到峰，再乘 1.6 倍"冲刺加成"。

v0.4 换成一条**生物力学推导链**（`core/GaitModel.cs`，纯函数、可脱离引擎单测）：

```
身高 H ──0.53──▶ 腿长 L ──Froude 0.45──▶ 偏好步速 v_pref = 0.45·sqrt(g_earth·L)
     ──走跑切换 1.9·v_pref──▶ 步频 f(速度) ──L_stride = v/f──▶ 摆腿角 theta = asin(L_stride/2L)
     ──▶ 垂直起伏 = L(1−cos theta)·吸收系数 + 腾空弹道项 ──▶ 点头 / 侧倾
```

> 注意这里用**地球重力 9.81**，不是游戏重力 20.78 —— 腾空相时长要按真人跑步算，
> 游戏重力只负责"跳起来多高"。两者混用会让腾空比虚高一个量级。

| 速度 | 步态 | 步频 | 步幅 | 腾空比 | 垂直半幅（峰到峰） | 点头半幅 |
|---|---|---|---|---|---|---|
| 1.43 m/s（偏好步速） | 走 | 1.90 Hz | 0.75 m | 0 | 4.6 mm（9 mm） | 0.18° |
| 4.41 m/s（本插件行走） | 跑 | 2.99 Hz | 1.48 m | 0.165 | 3.7 mm（7 mm） | 0.15° |
| 6.60 m/s（本插件冲刺） | 跑 | 3.29 Hz | 2.01 m | 0.380 | 10.3 mm（21 mm） | 0.41° |

**奔跑腾空相**不是拍一个系数：`腾空比 = clamp01((v − v_runStart)/(v_sprint − v_runStart)) × 0.38`，
再由 `半腾空时长 = 腾空比 × 周期 / 2` 算弹道自由落体高度 `0.5·g·t²` —— 所以"跑得越快腾空越久、
起伏越大"是算出来的，不是调出来的。走路（腾空比 0）的起伏只来自摆腿压缩，所以走路比跑步**更稳**。

| 判据（需求方口径 m02752「舒适档」） | v0.3 实测 | v0.4 实测 |
|---|---|---|
| 垂直起伏峰到峰 ≤ **2.5 cm** | 冲刺 73 mm（超 3 倍） | 冲刺 **21 mm** ✅ |
| 点头 ≤ **0.8°** | 冲刺 0.83°（且频率 4.83 Hz） | 冲刺 **0.41°** ✅ |
| 落地冲击 1.5–2.5° | 2.49°（v0.3 已收敛） | 2.49°（未改） ✅ |
| 频率是否物理可能（人类跑步 ≤3.3 Hz） | 4.83 Hz ❌ | **3.29 Hz** ✅ |

实现要点：

- 模型每次 tick 求值写进 `MovementSnapshot.Gait`，**相机只读不拍**（`ApexCameraRig` 里原来的
  `bobAmp = clamp(speed/5, 0, 1.4)` 斜坡与 `BobSprintMultiplier` 已删除）。
- 唯一仍生效的总闸是 `CameraEffectsConfig.BobGaitScale`（默认 1.0，调试菜单可实时拖 0–2×）。
- **滑铲时步态按蹲行速度算**，不跟 10 m/s 滑行速度飙（否则 4 Hz 抖动又回来了）。
- 空中默认不晃，但**奔跑腾空相保留摇晃**（`s.OnGround || s.Gait.FlightFraction > 0.05`）——
  真人跑步腾空时视线仍在随步频起伏，砍掉反而不自然。

**② 滑铲镜头与下蹲完全一致（需求 1 后半）**

需求原话：「滑铲时镜头我希望不要倾斜，而是与下蹲状态一致」。所以不是"把 5° 调小"，而是**全删**：

| 项 | v0.3 | v0.4 |
|---|---|---|
| 滑铲侧倾 | `SlideMaxRollDeg = 5°`（实测满值 5.00°，截图 `roll=5.4°`） | **恒为 0**（自检 `5m/s→0.00°、10m/s→0.00°`；集成测试 `roll=0.73°` 只是冲刺余量） |
| 滑铲 FOV 加宽 | `FovSlideAdd = +6°`（实测 96°） | **不加宽**（自检 `fov=90.00`；集成测试 `92.37°` 也只是冲刺余量） |
| 滑铲额外下沉 | `SlideEyeDrop = 10 cm` | **不再下沉**（画面只有视点降低 = 蹲下高度） |

`SlideMaxRollDeg` / `FovSlideAdd` / `SlideEyeDrop` 三个字段保留在配置里只为兼容旧资产，
`ApexCameraRig` 已不再读取（代码里有注释说明），**调它们不会有任何效果**。

**③ 攀爬门控：必须 `W` 贴墙 + `Space` 跳起（需求 2）**

| 场景 | v0.3 | v0.4 |
|---|---|---|
| 地面贴墙按住 `W` | **会被吸上墙**（v0.3 门槛是"在空中 + 朝墙速度"，但离开地面那一 tick 就满足了） | **撞墙停住 / 贴着墙蹭**，永不进 `Climb`（实测 120 tick 仍 `Ground`，停在 `z=-1.594`） |
| 跳起后贴墙 + 按 `W` | 抓墙 | 抓墙（`Climb`，脚底持续上升） |
| 空中上升但不按 `W` | 抓墙 | **不抓**（上升途中不打断） |
| 空中**下落**贴墙、不按任何键 | 抓墙（如果按着蹲） | **自动扒住**（`WallHang`，扒墙默认化） |
| 离墙 > `WallHangReach`(0.9 m) | 抓不到 | 抓不到（实测 2.5 m 拒） |
| 脚下 0.5 m 内有地面 | 抓 | **不抓**（`WallGrabMinGroundClearance`，否则走下小台阶会被凭空吸上墙） |

**④ 扒墙默认化 + 攀爬/扒墙合并成一套状态机（需求 3）**

v0.3 有两套独立状态机（`Climb` 靠 `TryAttachClimb/StepClimb/DetachClimb`，`WallHang` 靠
`TryAttachWallHang/StepWallHang/DetachWallHang`），共 12 个函数，语义还互相打架
（一个"松手就掉"、一个"按蹲才挂"）。v0.4 **合并成一套**：

| 新函数 | 职责 |
|---|---|
| `TryAttachWallGrab` | 唯一附着入口：离地 + 脚下无地 + (下落 或 按 `W`) + 够得到墙 + 陡墙 + 视角容差 + 不是矮墙 + 重附着规则 → 按"是否按 W"决定进 `Climb` 还是 `WallHang` |
| `StepWallGrab` | 唯一推进入口：蹬墙跳 / `S` 直落 / 视角甩开 / 探墙与到顶 / 输入解析 / 握力 / 贴墙钳制 |
| `DetachWallGrab` | 唯一脱离入口（`jumped` / `drop` 两种） |
| `DetachWallGrabAtTop` | 到顶无台面时的 End boost |
| `TryCompleteWallGrab` | 到顶有台面 → 补爬 → `Hang` → 上墙 |

需求逐条落点：

| 需求 | 实现 | 实测 |
|---|---|---|
| 默认扒墙、不需按键 | 空中下落贴墙即挂（`WallHangRequiresCrouch = false`，蹲键不再是条件） | `state=WallHang` |
| 攀爬必须持续按 `W` | `vUp = 按W ? ClimbSpeed(2.0) : -WallHangSlipSpeed(0.22)` | 0.5 s 爬 0.35–0.65 m；按 `W` 上爬 2 m/s |
| 松 `W` → 转入扒墙 | 同一 tick 里 `_state = pressingW ? Climb : WallHang`，不再"松手就掉" | 松 `W` 3 tick 后 `WallHang`，缓慢下滑 |
| 一小段时间无输入就掉落 | 握力 `_wallHangGrip` 按 `WallHangIdleFallTime = 1.2 s` 消耗 | 5–100 tick 内脱手（实测约 72 tick） |
| `A`/`D` 横移 | `WallHangShimmySpeed = 0.8 m/s` | Δx = ±0.40 m / 0.5 s |
| `W+A`/`W+D` 斜移 | 上爬与横移同时生效 | Δx>0.25 且 Δy>0.35 |
| `W` 继续向上爬 | 同上（同时回补握力，`WallHangGripRestoreTime = 2.5 s`） | grip 0.12 → 0.32 |
| **`S` 取消扒墙、直接垂直下落** | `input.MoveZ < -0.3` → `DetachWallGrab(drop: true)`，速度清零 + 立即 `Air` | 立刻 `Air`，12 tick 掉 >0.3 m，`vy < -2` |
| **下落中按 `W` 可再扒住** | `_wallGrabNeedW` 门闩：脱手后必须按 `W` 才能再抓；`drop` 冷却只 0.12 s | 下落中按 `W` 重抓成功 |
| 蹬墙跳 | `Space` → 离墙 + 向上（`vz=2.69, vy=6.15`） | 通过 |
| 甩视角松手 | `WallHangMaxViewAngle = 45.57°` | 甩 90° 立刻脱手 |

握力三种流失/回补（一个方向一个旋钮，都在 `MovementConfig` 且 `Validate()` 校验 >0）：

| 情况 | 时间常数 | 含义 |
|---|---|---|
| 完全无输入 | `WallHangIdleFallTime = 1.2 s` | 挂着发呆 1.2 s 掉下去 |
| 只横移 | `WallHangLateralFallTime = 6.0 s` | 横移很费握力，但能挪一会儿 |
| 按 `W` 上爬 | `WallHangGripRestoreTime = 2.5 s` | 上爬回补握力（爬得动就一直挂着） |

**⑤ 三档障碍分级（需求 5，需求方逐字口径）**

| 高度 | 判定 | 行为 | 实现 |
|---|---|---|---|
| **≤ 0.25 m** | 台阶 | 直接平滑迈过，**无速度衰减**；水平速度临时变成斜向上再变回水平，**只改方向不改大小**；不触发任何动作 | `TryStepUp` + `StepTiltAngle = atan(b/a)`（a = 脚底中心到边缘的横向距离 `CapsuleRadius`，b = 台阶高），`StepTiltTime = 0.12 s` 内线性衰减 |
| **(0.25, 1.0] m** | 小障碍 | 只按 `W` 撞住；**`W+Space` 直接翻越**（`Vault`，不触发攀爬）；翻越结束**快速恢复满速** | `TryVaultObstacle`（只在 `StepGround` 的跳跃分支调用）+ `_vaultExitSpeed = 意图速度 × 0.85` + 落地后 `VaultRecoverTime = 0.25 s` 内加速倍率 ×3 |
| **> 1.0 m** | 墙 | 撞上去停住；需要 `W+Space` 跳起后攀爬（攀爬时水平速度 = 0） | `TryAttachWallGrab`（`VaultMaxHeight` 以上才允许挂） |

> **v0.4 删掉了"自动翻越探测"**：v0.3 每 2 tick 从地面向前打一次射线，只要探到台面就自动翻 ——
> 于是"贴墙按 W 就爬墙 / 走过台面就自己翻"同时发生，玩家没法拒绝。现在翻越**只由 `W+Space` 触发**，
> `TraversalProbeCount` 恒为 0（属性保留只为兼容自检历史）。
>
> 顺带修掉 v0.3 留下的 **1.0–2.03 m 死区**：v0.3 的"太矮不挂"探针高度用 `AutoMantleMaxHeight`(2.03)，
> 于是 1–2 m 的墙既翻不过去（> `VaultMaxHeight`）又挂不住 —— 玩家撞上去干瞪眼。
> v0.4 探针改为**相对脚底** `VaultMaxHeight × 0.6`，2.5 m 墙能正常挂住（自检 ⑰b）。

**⑥ `\` 调试菜单（需求 4）**

新增 `player/ApexDebugMenu.cs`（`CanvasLayer`，纯 C#，事件回调解耦，不引用玩家类型）：

| 控件 | 范围 | 效果 |
|---|---|---|
| 视野 FOV | 60–120°，步 1 | `_rig.SetBaseFov` + 同步 `FovBase` |
| 鼠标灵敏度 | 0.0005–0.0100 | `MouseSensitivity` |
| 步态摇晃倍率 | 0–2×，步 0.05 | `CameraEffectsConfig.BobGaitScale`（v0.4 摇晃总闸） |
| 落地冲击倍率 | 0–2×，步 0.05 | `CameraEffectsConfig.KickScale` |
| 相机效果 | 开/关 | 整块相机效果旁路 |
| 调试 HUD | 开/关 | HUD 显隐 |
| 关闭（`\`） | 按钮 | 与再按一次 `\` 等价 |

- `\` 键**自注册**：`ApexPlayer.EnsureAction("apex_debug_menu", Key.Backslash)`，
  不依赖宿主项目跑过 `apex_input_map.gd` 的 `ensure_all()`（用 `PhysicalKeycode`，非 US 布局也在同一物理键位）。
- 打开时**释放鼠标**（可以直接拖滑条），并冻结移动/蹲/冲刺/跳跃输入（拖滑条时人不会往前跑）；
  关闭时恢复捕获。菜单打开时 `Esc` 也是关闭菜单（不再是"释放鼠标"）。
- 为什么之前"没看到调试菜单"：v0.3 只有一个**只读 HUD Label**，从来没有菜单。v0.4 才真正做出来。

**⑦ 顺带的一致性修正**

| 项 | 改动 |
|---|---|
| 上爬速度 | 删除 `WallHangClimbSpeed`(1.0)，攀爬与扒墙上爬统一用 `ClimbSpeed`(2.0) —— 一个旋钮一个方向 |
| 自动翻越 | 探测调用整体移除（见 ⑤），`MovementState.Window` 改由 `W+Space` → `TryVaultObstacle` 内部优先尝试窗口 |
| `_wallHangNeedRelease` | 更名 `_wallGrabNeedW`，语义从"必须离开墙一次"改成"必须按 W 才能再抓" |
| 探针计数 | 删 `_wallHangProbeCount` / `_climbNonUpwardTimer`（合并后失去使用者），`WallHangProbeCount => _climbProbeCount` |

### 6.3 v0.3 相比 v0.2

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

### 6.4 v0.2 相比 v0.1 的机制变化

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

### v0.4.1 新增：⑱ ⑲ ⑳ ㉑

#### ⑱ 翻越 1.3 m 平台时角色悬在半空、vy 掉到 −41 却纹丝不动

**症状**（需求方截图 + 探针复现）：贴墙 → `Space` → 按住 `W`，人停在脚底 0.87 m 处，
`state=Air`、`onGround=false`，`vy` 每 tick 更负（一路到 −41），但 `pos` 完全不变。

**排除项**：向下射线命中地面在 y=0（距离 1.847）、前后上三向射线全 `none`、
胶囊 `intersect_shape` 的 overlap count = **0** ⇒ 不是被几何体卡住，是**速度被吃光**。

**根因**：`GodotMovementWorld.QueryNormalAt` 在"扫掠刚好停在接触面边缘"时
`GetRestInfo` 打空（`ProbeDepth` 只有 2 cm，胶囊其实还没真正重叠），于是走了
`return fallback.Normalized()` 兜底 —— 而 `fallback = -delta.Normalized()`，
即"运动方向的反向"这种**伪法线**。`MoveWithCollision` 拿它做
`_vel = F3.ProjectOnPlane(_vel, normal)`，把水平与竖直分量**一起**投影掉。

逐帧插桩（`TRACE pre/post`）证据：

```
TRACE pre  t=67 y=1.8397 z=32.5936 vy=4.918 vz=0.254
TRACE post t=67 y=1.8467 z=32.5940 vy=-0.000 vz=-0.000     ← vy 被吃掉
TRACE pre  t=68 y=1.8467 z=32.5940 vy=-0.346 vz=0.254
TRACE post t=68 y=1.8466 z=32.5940 vy=-0.345 vz=0.000      ← 位置不再变化
```

**修法**：两级兜底（见 §6.1 ④）—— ① 原地把胶囊半径与半球外扩 `ProbeDepth` 再查一次；
② 仍失败则把 `-fallback` 投影到水平面当"竖直墙"法线，只挡水平分量。
**宁可当成墙（最差少走一点），也不要把竖直速度抹掉（永久卡死）。**

回归：`ClimbTest` 新增 1.3 m 平台用例；`integration_test` 新增「1.3 m 平台 W+Space 能站上去」。

#### ⑲ 滑铲在坡上是"越陡越慢"，与直觉相反

v0.4 及之前滑铲用一条与坡度无关的 `SlideFriction = 0.9` 做指数衰减，
重力分量被 `_vel = F3.ProjectOnPlane(_vel, _groundNormal)` 直接投掉 ⇒
**下坡反而比平地减速更快**。

修法：改成真实物理 `a = g·sinθ − μ·g·cosθ`（先加下坡重力分量、再减库仑摩擦），
θ 由 `_groundNormal` 与 up 的夹角给出。副产品：`tanθ = μ` 的临界角自动成立，
μ=0.45 ⇒ 24.23°，比这更陡的坡上滑铲永远停不下来 —— 这是物理的必然，不是特判。

回归：`FeatureTest` 新增「平地下坡上坡三种坡度下的滑铲加速度符号与量级」；
`integration_test` 新增「15° 下坡滑铲 1 s 后比平地更快」。

#### ⑳ `SampleFriction` 的 meta 回退链缺失，只有碰撞体挂 meta 时才生效

需求要"不同材质不同摩擦系数"，第一版只在命中节点自己身上读 meta `friction`，
但场地里通常把材质 meta 挂在 `StaticBody3D`（或再包一层的分组节点）上，
而 `IntersectRay` 返回的 collider 可能是子级的 `CollisionShape3D` 宿主 ⇒ 读不到、静默用默认值。

修法：`GodotMovementWorld.SampleFriction` 沿父链**逐级回退最多 4 层**
（够覆盖"分组节点 → 刚体 → 碰撞体"，但不一路爬到场景根，避免误捡全局 meta），
任一级取到就用。这样"给 `StaticBody3D` 挂一个 meta `friction`"就够了，
不用给每个 `CollisionShape3D` 单独配。

```gdscript
# 场地里加一种材质只要这三行（testbed_builder.gd 的 _build_friction_lanes）
var body := StaticBody3D.new()
body.set_meta("friction", 0.05)      # 冰面
add_child(body)                       # 挂个带 CollisionShape3D 的 MeshInstance3D 即可
```

#### ㉑ 高墙爬不上去：到顶脱手被"反作弊规则"永久拉黑

**症状**：6 m 以上的墙，爬到 `ClimbAttachOffset`(2.54 m) 处脱手后**再也抓不回去**，
人贴墙往下掉，按 W 也没反应 —— 高墙永远上不去。

**根因**：`DetachWallGrabAtTop()` 里调了 `RememberClimbSurface()`。
而重附着规则是"必须先掉回上次 attach 点以下才能再抓"（wiki L172-175，防刷攀爬），
于是"到顶脱手"这个**不是玩家放弃**的动作，被当成"玩家主动脱手"记进了黑名单 ⇒
后续每一次重抓都在 `here > _lastClimbAttachY` 处被拒。实测卡在 2.54 m。

**修法**：`DetachWallGrabAtTop()` **故意不调** `RememberClimbSurface()`。
理由写进代码注释：到顶脱手是"爬升距离到上限"，玩家还按着 W，应该能落回去继续爬；
反作弊规则只针对玩家主动脱手（`S` / 跳 / 甩视角），那些走 `DetachWallGrab`。

**回归**：`FeatureTest` 两条 —— 「单段爬升不超过 `ClimbAttachOffset`（不是瞬移到顶）」
实测单段最大 2.91 m；「到顶脱手后能重新抓墙，分段爬到台面」实测 7.5 m 墙能登顶。

---

### v0.4 新增：⑬ ⑭ ⑮ ⑯ ⑰

#### ⑬ 扒墙默认化后，贴着墙走下小台阶会被"凭空吸上墙"

`WallHangRequiresCrouch` 改成 `false` 之后，扒墙的进入条件只剩"离地 + 够得到墙 + 正在下落"。
而**离开地面的那一 tick `_onGround` 已经是 false，但人其实还站在台沿上**，
于是"贴着墙往下走一小步"这种最普通的动作被判成"空中下落贴墙" ⇒ 瞬间被吸上墙。
自检 ① 报的症状是 `state=Climb onGround=True z=-1.574` —— 一边说在地面上，一边说在攀爬。

修法：附着判定里加一道**脚下地面余量**门槛（`WallGrabMinGroundClearance = 0.5 m`）：

```csharp
F3 feetNow = _pos - _up * (_currentHeight * 0.5f);
if (_world.Raycast(feetNow, -_up, _cfg.WallGrabMinGroundClearance, out _, out F3 belowN) &&
    F3.Dot(belowN, _up) >= _cfg.WalkableNormalDot)
    return false;      // 脚下半米内还是地面 → 这是"走下去"，不是"掉下去"
```

#### ⑭ 贴墙下滑到底会压进地面 —— 落地后彻底走不动

贴墙态是**运动学积分**（`_pos += _up * (vUp * dt)`），不走 `MoveWithCollision`，
所以一路下滑到地面时会把胶囊压进地里。一旦重叠，后续 `Sweep` 从已重叠的位置起扫返回
`fraction = 0` ⇒ 水平移动也被挡住，表现为"落地后原地卡死"。
修法：下滑分支里先向下打一条 `slideDown + 0.02 m` 的射线，命中可行走法线就**贴到地面并直接脱离**：

```csharp
if (vUp < 0f)
{
    F3 feetNow = _pos - _up * (_currentHeight * 0.5f);
    float slideDown = -vUp * dt;
    if (_world!.Raycast(feetNow, -_up, slideDown + 0.02f, out F3 groundHit, out F3 groundN) &&
        F3.Dot(groundN, _up) >= _cfg.WalkableNormalDot)
    {
        _pos = groundHit + _up * (_currentHeight * 0.5f);
        DetachWallGrab(jumped: false, drop: true);
        return;
    }
}
```

#### ⑮ 翻越小障碍后几乎原地不动 —— 出口速度用的是"已经归零的当前速度"

`_vaultExitSpeed = hSpeed * VaultSpeedRetention` 里的 `hSpeed` 取自 `_vel`，
但走到障碍前时 `MoveWithCollision` 早已把速度**投影到立面**上 ⇒ 水平分量≈0，
于是翻完只留下 0.62 m/s，与需求"削减较小、翻完快速恢复满速"正好相反。
修法：用**意图速度**（`WishSpeed`）兜底 —— 玩家的意图不会因为撞墙而消失：

```csharp
float wish = WishSpeed(input);
if (wish > hSpeed) { horiz = WishDirection(input) * wish; hSpeed = wish; }
_vaultExitSpeed = hSpeed * _cfg.VaultSpeedRetention;
```

#### ⑯ 1.0–2.03 m 是"死区" —— 既翻不过去，又挂不住

"太矮不挂"探针原来量的是 `AutoMantleMaxHeight`（2.03 m），而翻越上限是 `VaultMaxHeight`（1.0 m）⇒
**1.0–2.03 m 之间的障碍两套机制都拒绝**，玩家撞上去只能干瞪眼。
修法：探针改成按需求口径量 `VaultMaxHeight`，且相对**脚底**而不是胶囊中心
（跳跃贴墙那一瞬间脚底才 0.03 m 高，按中心算会平白多探 1 m），取 0.6 倍给 1.5 m 这种刚过档的墙留余量：

```csharp
F3 highOrigin = FeetPosition + _up * (_cfg.VaultMaxHeight * 0.6f);
if (!_world.Raycast(highOrigin, fwd, reach, out _, out _)) return false;   // 上方没墙 → 矮坎，不挂
```

#### ⑰ 蹲下反而晃得更厉害 —— 把蹲伏修正混进了几何项

步态模型里若先用"降频后的步频"算步幅（`L = v / f`），蹲下会让步幅**变长** ⇒ 摆腿角变大
⇒ 垂直起伏反而变大，方向完全反了（自检 ⑱ 抓到的真实 bug）。
修法：几何项（步幅 / 摆腿角 / 腾空弹道）一律用**站立步频**推，蹲伏只按比例缩小最终幅度：

```csharp
float strideStand = speed / cadenceStand;                  // 几何项专用
float cadence     = cadenceStand * (1f - p.CrouchCadenceDrop * crouch);   // 只影响相位速度
...
vertical *= 1f - p.CrouchAmplitudeDrop * crouch;           // 蹲伏只缩幅度
```

> 另有三个**工具链**层面的坑（不是插件逻辑 bug，但都会让人误判插件有 bug）：
> ① `CheckButton` 在构造器里设 `ButtonPressed` 不会重算按下态图标（截图里该开却是关）→ 进树后 `SetPressedNoSignal`；
> ② `CanvasLayer` 暴露给 GDScript 的属性表里没有继承来的 `visible`，跨语言要读普通 bool 属性；
> ③ 截图脚本拍完第三人称图没把 `current` 切回玩家相机，导致两张"FOV 对比图"差异只有 0.2%（全在 HUD 区域）。

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

### A. 确定性 + 功能 + 相机 + 贴墙/三档/tick + 步态 + 打滑（`determinism_test.tscn`，172 项断言）

```
=== 跨帧率确定性 ===
  参考（60fps）tick 数 = 240
  60fps vs 30fps:  一致（240 ticks，逐位相等）
  60fps vs 90fps:  一致（239 ticks，逐位相等）
  60fps vs 144fps: 一致（239 ticks，逐位相等）
  60fps vs 240fps: 一致（240 ticks，逐位相等）
  60fps vs 500fps: 一致（240 ticks，逐位相等）

=== 功能验证（43 项，全 PASS）===
  行走 4.407 / 冲刺 6.604 / 收枪冲刺 7.595 / 收枪行走 5.068（且不误判冲刺）/ 蹲行 2.032
  跳跃高度 1.487（期望 1.422 ± 0.10）、滞空 0.717s
  空中无摩擦（40 tick 速度差 0.00E+000）、Air strafe 加速 +0.82 m/s
  Slideboost 11.53 m/s、2s 冷却、Deadslide、连续滑铲跳 3 次、跳跃疲劳 0.462m
  v0.4 三档：小障碍 0.8m 只撞停 / W+Space 翻越（Below）；墙 2.5m W+Space 抓墙 → 爬到台面；
            6m 高墙按住 W 每秒爬 ≈2m（是状态不是瞬移）；7.5m 超限爬不上去但能分段爬到顶；
            窗口翻越（W+Space 穿开口）
  v0.4.1 滑铲物理（5 项）：15° 下坡 −3.65 m/s² / 30° 下坡 +2.29 m/s² / 临界角 24.23°（a=0.0000）；
            合加速度随坡角单调上升（15° −3.65 < 20° −1.68 < 30° +2.29）；
            默认 μ=0.45 减速 9.351 ≈ μg；冰面 μ=0.05 减速 1.039；粗糙 μ=0.90 减速 18.35（≈2×默认）；
            三种材质 0.4s 后速度：冰面 9.71 > 默认 6.11 > 粗糙 2.20 m/s
  重力与 wiki 双数据自洽（g=20.7801 vs 20.7802）

=== 相机子系统（24 项，全 PASS）===
  FOV 90 基准 / 冲刺 93 / **滑铲不抬 FOV（=90，v0.4 已移除）** / 夹紧 120 / 玩家可调 ±2°
  tan 空间插值 vs 线性：中途差 1.599°，终值差 0.0000°
  落地踢：瞬间低头 −0.0182 rad，1.5s 后回正
  落地砸镜头：Hard 2.49° / Low 0.47°；MaxKickAngle 只作保险丝（0.0435 < 0.075×0.9）
  落地踢按档位单调（0.0082 < 0.0180 < 0.0294 < 0.0435）
  **滑铲不侧倾（5m/s→0.00°，10m/s→0.00°，v0.4 已移除）**、冲刺侧倾 1.50°
  走路摇晃：静止 0 / 移动 0.0063 m（**0.63 cm，落在舒适档 ≤2.5 cm**）/ 空中 0
  翻越相机脱开→回贴（0.997 → 0.000）、高度上限压低 0.65 m
  蹲下前倾 0.030 m、冲刺眼位前移 0.0599 m、FOV 收敛与帧率无关（差 < 0.0005°）

=== v0.4 贴墙（攀爬 / 扒墙） / 三档障碍 / tick / 探针（47 项，全 PASS）===
  需求2 门控：地面贴墙按 W 120 tick 不抓（z 停在 −1.594 = 胶囊半径）；
              W+Space 起跳后抓住且脚底升 >0.2m；空中**上升**+不按 W 不抓
  需求3 输入：按 W 抓住 = Climb；按 W 0.5s 爬 ≈1m（ClimbSpeed 2.0）；松 W → WallHang；
              挂住 speed/vy=0、贴墙 z=−1.574；D/A 0.5s 横移 ±0.40；W+D 斜移；
              无输入 5–100 tick 掉落；S → Air 且 12 tick 掉 >0.3m；下落中按 W 重新抓住；
              Space 蹬墙跳；甩视角 90° 松手；矮坎(0.5m)不挂 / 2.5m 墙能挂（v0.3 死区已修）
  需求5 三档：0.20m 台阶无任何动作 + 脚底抬到 0.20 + 最大向上速度 >0.3 + |v| 守恒；
              0.80m 小障碍只撞停 → W+Space 进 Vault，翻完速度 > WalkSpeed×0.85×0.8；
              6m 墙撞停 → W+Space 走抓墙；TryTraversal 1.6m → Mantle/Level；7.5m 拒绝
  边界：OverwalkableHeight=0.25 / VaultMaxHeight=1.00 / 顺序自洽
  tick：TickRate=60 / FixedDelta=1/60 / 30fps 每帧 2 tick / 144fps 10s≈600 tick / 卡顿最多补 4
  探针：墙探针 60 tick≈30 次（ClimbWallProbeInterval=2）；附着探针 40 tick 40 次；
        地面 60 tick 自动翻越探针 = 0（v0.4 已移除自动翻越探测）

=== v0.4 步态模型 / 镜头抖动推导（23 项，全 PASS）===
  ① 腿长 = 0.53 × 身高（H=1.95 → 1.0335 m）
  ② Froude 偏好步速 1.433 m/s（人类实测 1.4）  ③ 走→跑阈值 2.722 m/s（人类 2.0–2.7）
  ④ 个子越高偏好步速越快（1.60m→1.298 / 2.20m→1.522 m/s）
  ⑤ < MinSpeed 全零  ⑥ 偏好步速是「走」：1.90 Hz、腾空 0
  ⑦ 步幅 = 速度 / 步频（4.407 / 2.986 = 1.476 m）
  ⑧ 行走 4.407 m/s 已是「跑」：2.99 Hz / 1.48 m / 腾空 0.165
  ⑨ 冲刺 6.604 m/s：3.29 Hz / 腾空 0.380（= MaxFlightFraction）
  ⑩ 步频单调且 < 4 Hz（1.0→1.53 / 4.41→2.99 / 6.60→3.29）
  ⑪ 腾空比 走 0 → 跑随速度涨 → 冲刺封顶 0.38
  ⑫ 垂直起伏半幅：慢走 4.6 mm / 冲刺 10.3 mm  ⑬ 冲刺峰到峰 20.7 mm ≤ 25 mm
  ⑭ 点头半幅 0.413°（上限 0.80°）  ⑮ 侧摆夹在 [4, 14] mm  ⑯ 侧倾 ≤0.35°
  ⑰ 同速下越高步频越低  ⑱ 蹲行：步频 ×0.65、幅度 ×0.80
  ⑲ 兜底不出 NaN/Inf  ⑳ 常数都在文献区间内
  ㉑ 快照 Gait 来自模型求值  ㉒ 相位每秒推进 = 步频周期数（帧率无关）
  ㉓ 滑铲时步态按蹲行速度算（≤2.0 Hz，不跟着 10 m/s 飙）

=== v0.4 打滑 / 扒墙（滑区与握力）（35 项，全 PASS）===
  打滑：进球 → Slip；速度拉到 8.00 m/s；方向 = 体积方向(+X)；
        贴地滑道上仍踩在地上（OnGround=True feet=0.001，**不悬空**）；
        没配方向 → 从球心往外推（vx=6.00）；滑出体积回 Ground；
        ClampToCone 三例；滑区里蹲 → Slide；滑区里跳 → Air（vy=7.69）；
        SlipProbeInterval=2，60 tick 只采样 30 次
  扒墙（v0.4 默认化）：**空中下落贴墙 + 不按任何键 → 自动挂住（WallHang）**；
        **空中上升贴墙 + 不按 W → 不挂**（上升不打断）；
        挂住速度恒 0、贴墙 z=−1.574、D 横移 Δx=0.80、不动下滑 Δy=0.22；
        握力耗尽脱手；W 回补握力；超 WallHangMaxTime(6s) 脱手；蹬墙跳 vz=2.69 vy=6.15；
        甩视角 90° 松手；矮墙不挂；离墙 2.5m 挂不上；探针 40 tick 40 次

=== APEX MOVEMENT SELFTEST PASS ===
```

### B. 真实物理集成（`integration_test.tscn`，49 项断言）

```
  [PASS] 落地 / 脚底贴地 0.001 / 站立身高 1.950 / 相机高度 1.850 / 基础 FOV 90.000
  [PASS] 行走 4.407 / 平均位移 4.284 / 奔跑 6.604 / 收枪奔跑 7.595
  [PASS] 蹲下 0.975 / 脚底不下沉 / 相机高度 0.875 / 站起恢复
  [PASS] 跳跃高度 1.488 / 跳后重新落地
  [PASS] 滑铲触发 9.22 m/s / Slideboost
  [PASS] **滑铲不再有专属侧倾**（roll=0.72° ≤ SprintMaxRollDeg 1.5）
  [PASS] **滑铲不再抬 FOV**（fov=92.35° ≤ 90+3 冲刺余量）
  [PASS] 连续滑铲跳 3 次
  [PASS] **v0.4.1 滑铲物理**：30° 坡（tanθ>μ）实测 +1.98 m/s²（理论 +2.29）v 4.32→6.50
  [PASS] **v0.4.1 滑铲物理**：15° 坡（tanθ<μ）实测 −3.53 m/s²（理论 −3.65）v 3.75→2.51
  [PASS] 同样起滑速度下，陡坡越滑越快、缓坡被刹停
  [PASS] 小障碍 0.9m：W+Space 直接翻越（档位 Below，脚底最高 1.49）
  [PASS] 墙 1.7 / 3.0 / 6.0m：W+Space 抓墙 → 按住 W 爬到台面上（脚底最高 = 台面高）
  [PASS] 6m 攀爬（High 档）6.000
  [PASS] 地面顶着墙按 W 不触发攀爬（state=Ground）
  [PASS] 顶墙被挡住、没有穿墙（x=48.594，墙面 x=49.0）
  [PASS] 跳跃后贴墙会挂上攀爬（state=Climb x=48.574）
  [PASS] 按住 W 持续上爬 0.75s → Δfeet 1.500（ClimbSpeed 2.0）
  [PASS] **松开 W 后转入扒墙（WallHang）并缓慢下滑**（feet 2.221 → 2.112）
  [PASS] 爬上 15° 斜坡 / 翻越 0.8m 矮墙（W+Space）/ 窗口翻越 + 穿过开口
  [PASS] **v0.4.1 回归**：1.3m 平台 W+Space 能站上台面（feet=1.30，max_feet=1.49）
  [PASS] **v0.4.1 回归**：1.3m 平台全程没有「空中速度很大却不动」的死锁（最长连续卡死帧数 = 0）
  [PASS] 低天花板下无法站起 / 离开后站起
  [PASS] FOV 可调 100°/90° / 视角踢可触发 / 掉出世界自动复活

=== INTEGRATION PASS（49 项断言） ===
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
| 台阶区 | x∈[-30,-6], z=-70 与 x=10, z∈[-70,-60]，每级 +0.20m | **v0.4 三档第一档**：≤0.25m 台阶只改方向不改大小 |
| 窗口框架 | x∈[20,32], z=68，窗台 0.6/0.9/1.2m + 窗楣 4.5m | TRAVERSAL_WINDOW |
| 贴墙爬墙 | x=50, z∈[-12,12]，高 8m | Climb 状态 / attach offset / end boost |
| 低天花板 | x=-60, z∈[15,25]，净高 1.1m | 蹲下才能进 / 站起被头顶挡住 |
| 滑铲长道 | x=-70, 80m 带 10m 刻度 | 滑铲距离 / 连续滑铲跳 |
| 打滑体积 | x=80, z∈[-7,7]，半径 7m（`slip_volume` 组） | Slip 状态 / 方向限制 / 滑出回 Ground |
| 落差平台 | x∈[20,44], z=55，高 3/6/10/22m | 落地四档 + Fallstun |
| 地面材质带 | x=-20, z∈[62,74]，三条并排 4×12m（`friction_lanes`） | **v0.4.1 滑铲物理**：meta `friction` = 冰 0.05 / 常规 0.45 / 粗糙 0.90 |

---

## 10. 已知偏差与待校准项

| 项 | 状态 |
|---|---|
| `airAcceleration = 500` 的单位语义 | **未解**。若直接套 Source 公式会得到 12.7 m/s/tick（荒谬），说明 Apex 内部另做了缩放，该系数没读到。本插件把它归一化为可调的 `AirAccel`（B 级），并以 `AirWishSpeed = 60hu/s`（A 级）作为硬约束 |
| 相机数值（FOV 增量 / 弹簧刚度 / bob 幅度 / 侧倾角） | **全部 C 级**。逆向只证明字段存在，数值在 BSS 里读不到；wiki 也没有这些数据 |
| 走路摇晃 | Apex **没有** `cl_bob` 类 cvar（逆向负结论），摇晃来自第一人称动画。**v0.4 起改为自研步态模型**（`core/GaitModel.cs`）：腿长/Froude/走跑阈值来自人体测量学与步态文献，`WalkAbsorb`(0.16)、`RunCompression`(0.05)、`VerticalScale`(0.40)、`PitchPerCm`(0.40) 仍是拟合出来的手感系数（C 级；0.40/0.40 是照 m02752「垂直峰到峰 ~2 cm、点头 ≤0.8°」的舒适档定的），但**频率与身高、速度的耦合关系是有依据的**，且 23 项自检把每个常数夹在文献区间内 |
| 台阶倾斜角 `tan(θ)=b/a` | **需求方口径（v0.4）**。`a` 默认取胶囊半径 0.4064 m（`StepTiltFootRadius` 可覆盖为真实脚宽），`θ` 在 `StepTiltTime`(0.12 s) 内线性衰减。速度**只旋转不改大小**，自检 ⑲ 用 `|v|` 守恒验证 |
| `WallGrabMinGroundClearance = 0.5 m` | **自研手感参数**（C 级）。作用是区分"走下台沿"与"掉下墙"，0.5 m ≈ 半个身位；见 7 ⑬ |
| 调试菜单各滑条范围（FOV 60–120 / 灵敏度 0.0005–0.0100 / 摇晃 0–2× / 冲击 0–2×） | **自研**（无逆向依据，纯 UX 选择）。摇晃与冲击倍率直接乘在步态模型输出与 `KickScale` 上，不改变模型本身 |
| 滑铲摩擦系数 μ（默认 0.45） | **需求方口径（v0.4.1）**：用户指定默认 0.45，并要求按地面材质可调。实现为 `IFrictionWorld.SampleFriction` + 节点 meta `friction`（逐级回退最多 4 层），取不到才用默认值。μ=0.45 ⇒ 临界角 24.23°（`tanθ=μ`），是物理推论不是特判 |
| 滑铲坡度项 `g·sinθ` | **物理公式**（非逆向、非手感拍数）。θ 由 `_groundNormal` 与 up 的夹角给出；自检用 15°/30° 两个坡度实测加速度与解析解比对（−3.53 vs −3.65、+1.98 vs +2.29 m/s²） |
| `WallHangIdleFallTime = 1.2s` / `WallHangLateralFallTime = 6.0s` / `WallHangGripRestoreTime = 2.5s` | **自研手感参数**（C 级）。需求只说"一小段时间没有输入就掉落"，没给具体时长；三个时间常数分别对应"完全不动"/"只横移"/"按 W 回补" |
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

**30 张 1920×1080**，全部位于 [`../../docs/reference/apex_movement/`](../../docs/reference/apex_movement/)。
每张第一人称图的 HUD 会打印当时的 `fov / roll / state / vy / gait / step / speed`，可对着数字核验。

| 文件 | 内容 | 图上可核验的数字 |
|---|---|---|
| `shot_overview.png` | 场地全景 | 状态 `Ground`、`h=1.95` |
| `shot_ramps.png` | 三条斜坡（15°/30°/45°） | — |
| `shot_mantle_steps.png` | 攀爬台阶梯 8 档，颜色分 4 档 | — |
| `shot_vault_walls.png` | 翻越低墙 0.4/0.8/1.0m | — |
| `shot_step_stairs.png` | **v0.4 台阶区**（每级 +0.20m） | — |
| `shot_window_frames.png` | 窗口框架（窗台 + 窗楣） | — |
| `shot_climb_wall.png` | 装墙（8m，带刻度线） | — |
| `shot_slide_lane.png` | 滑铲长道（80m，10m 刻度） | — |
| `shot_slip_zone.png` | 打滑场地（地面绿色圆盘 + 进出参考色条） | HUD `slip=no hang=no` |
| `shot_friction_lanes.png` | **v0.4.1 地面材质带**（三条并排：冰 / 常规 / 粗糙） | — |
| `shot_drop_plats.png` | 落差平台 3/6/10/22m | — |
| `shot_player_fp.png` | 站立第一人称基准 | `fov=90.0°`、`roll=0.0°`、`bobPhase=0.00` |
| `shot_player_fov110.png` | FOV 拉宽 | `fov=109.9°` |
| `shot_player_fov70.png` | FOV 收窄 | 视野显著变窄（目标 70°） |
| `shot_player_walking.png` | **v0.4 步态摇晃**（由步频/步幅推导） | `gait=…Hz stride=…m fly=…`、`roll≈0.2°`、`bobPhase=…` |
| `shot_player_steps.png` | **v0.4 上台阶**（≤0.25m 只改方向不改大小） | `step=25.7deg`、`speed=4.70`、`vy=1.91`、`feet=0.20` |
| `shot_player_slide.png` | **v0.4 滑铲：不侧倾、不抬 FOV、等同下蹲** | `state=Slide`、`speed=9.89`、`h=0.98`、`fov=90.0°`、`roll=0.0°` |
| `shot_player_mantle.png` | 翻越途中：相机滞后 + 高度上限 | `state=Mantle`、`mantle=High/Grab`、`xBlend=1.00`、`fov=86.4°` |
| `shot_player_climb.png` | **v0.4 攀爬态**（W 贴墙 + Space 起跳后） | `state=Climb`、`speed=0.00`、`hang=WALLHANG grip=1.00` |
| `shot_player_hang.png` | 补爬结束挂边 | `state=Hang`、`speed=0.00`、`vy=0.00` |
| `shot_player_climb_top.png` | 挂边上墙（站到台面） | `state=Ground`，控制台同时打印 `CLIMB TOP feet=3.00` |
| `shot_player_slip.png` | 打滑态（站在圆盘里被推着走） | `state=Slip`、`speed=8.00`、`ground=True`、`slip=SLIP dir=0.00,1.00` |
| `shot_player_wallhang.png` | **v0.4 扒墙**（第三人称看挂点与高度） | `state=WallHang`、`speed=0.00`、`vy=0.00`、`hang=WALLHANG grip=0.68 t=0.38s` |
| `shot_player_slide_slope.png` | **v0.4.1 30° 下坡滑铲**（第三人称侧视看倾角） | `state=Slide`、`speed=5.26`、`vy=-3.04`、`fov=90.0°`、`roll=0.0°` |
| `shot_player_slide_slope_fast.png` | 同一趟滑铲 1 秒后（**下坡越滑越快**） | `state=Slide`、`speed=6.29`（控制台 `v0=5.13 → v1=6.16`）、`roll=0.0°` |
| `shot_player_friction_ice.png` | **冰面 μ=0.05** 滑铲（几乎不减速） | `state=Slide`、`speed=11.06`（0.5 s 内 11.58 → 11.08） |
| `shot_player_friction_norm.png` | **默认 μ=0.45** 滑铲（明显减速） | `state=Slide`、`speed=6.07`（0.5 s 内 10.75 → 6.23） |
| `shot_player_friction_rough.png` | **粗糙 μ=0.90** 滑铲（很快被刹停） | `state=Ground`、`speed=2.34`（0.5 s 内 9.81 → 2.34，已退出滑铲） |
| `shot_debug_menu.png` | **v0.4 调试菜单**（按 `\` 打开） | 面板：FOV 90°/灵敏度/摇晃倍率/冲击倍率/两个开关；HUD `fov=90.0°` |
| `shot_debug_menu_fov110.png` | 调试菜单里把 FOV 拖到 110° | 面板数值 `110°`、HUD `fov=110.0°`、画面视野明显变宽 |

> 攀爬三张（`climb` / `hang` / `climb_top`）是同一条链路的三个阶段：
> **W+Space 起跳挂墙 → 按住 W 爬 → 补爬 → Hang → 按 W 上墙**。
>
> `shot_player_wallhang.png` 是**唯一的第三人称图**：第一人称挂在墙上时脸离墙面只有 0.43 m，
> 且 `WallHangMaxViewAngle`(45.57°) 限制视线偏离法线，无论怎么调都只能看到一片墙。
> 为此截图脚本临时生成一个**纯视觉**胶囊替身（`_build_proxy()`，无碰撞体、不参与移动），
> 按玩家真实位置同步后拍完即隐藏。
>
> `shot_debug_menu_fov110.png` 走的是玩家拖滑条**同一条** `ValueChanged → FovChanged` 回调链
> （`ApexDebugMenu.SetFovSliderValue`），不是旁路改状态；两张图整画面差异 **47.68%**，
> 证明改的确实是视野而不只是 HUD 文字。
>
> `shot_player_slide_slope*.png` 与 `shot_player_friction_*.png` 都是**第三人称**：
> 第一人称下坡时画面全是坡面，看不出 30° 倾角；三条材质带都是平地，第一人称也分辨不出。
> 这两组图共用同一个纯视觉胶囊替身（`_build_proxy()`）。
>
> 三条材质带的 HUD `speed=` 是「同一起滑条件下滑 0.5 秒后」的读数，可以直接横向对比：
> **冰 11.06 > 默认 6.07 > 粗糙 2.34（已退出滑铲）**，正好是 μ 的单调反向。
> 理论依据：平地 θ=0 ⇒ `a = −μg`，减速度与 μ 成正比（自检 A 里三条断言分别实测
> 1.039 / 9.351 / 18.35 m/s²，比值 ≈ μ 比值）。
>
> 全部由 `testbed/shot_testbed.gd` 自动驱动按键与相机，不是摆拍。

---

## 12. 文档

- [`docs/wiki-reference.md`](docs/wiki-reference.md) —— **社区 wiki 数值换算表**（1hu=2.54cm、速度表、机制说明，每条带来源 URL）
- [`docs/reverse-engineering-v0.2.md`](docs/reverse-engineering-v0.2.md) —— **本轮逆向报告**（相机/FOV/晃动/翻越/滑铲/攀爬字段的 A 级证据 + 复现命令）
- [`docs/reverse-engineering.md`](docs/reverse-engineering.md) —— v0.1 逆向报告（PE 布局 / cvar 注册点 / 7 种 MANTLE 枚举）
- [`docs/selftest/v0.4.1-determinism.txt`](docs/selftest/v0.4.1-determinism.txt) —— **v0.4.1 自检 A 实测原始输出**（172 项，逐条 PASS）
- [`docs/selftest/v0.4.1-integration.txt`](docs/selftest/v0.4.1-integration.txt) —— **v0.4.1 自检 B 实测原始输出**（49 项，逐条 PASS）
- [`docs/selftest/v0.4-determinism.txt`](docs/selftest/v0.4-determinism.txt) —— v0.4 自检 A 原始输出（165 项）
- [`docs/selftest/v0.4-integration.txt`](docs/selftest/v0.4-integration.txt) —— v0.4 自检 B 原始输出（44 项）
- [`docs/selftest/v0.3-determinism.txt`](docs/selftest/v0.3-determinism.txt) —— v0.3 自检 A 原始输出（122 项）
- [`docs/selftest/v0.3-integration.txt`](docs/selftest/v0.3-integration.txt) —— v0.3 自检 B 原始输出（44 项）
- `../../docs/design/PLUGIN-CONVENTION.md` —— 插件化开发约定
- 截图：[`../../docs/reference/apex_movement/`](../../docs/reference/apex_movement/)
