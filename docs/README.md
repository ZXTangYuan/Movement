# ApexMovement 插件 v0.2

Apex 风格移动系统（**固定 tick 确定性**）+ **相机子系统** + 可玩测试场地。
逆向自 Apex Legends **v3.0.3.65** 的可执行文件（只读分析，未修改游戏安装目录），
数值标定自 [Apex Movement Wiki](https://apexmovement.tech/wiki)。

---

## 1. 一句话

复现 Apex 的移动手感（WASD / 蹲 / 跳 / 冲刺 / 收枪 / 滑铲 / 滑铲跳 / 连续滑铲跳 /
四档翻越 / 窗口翻越 / 贴墙爬 / 空中转向 / 跳跃疲劳 / Fallstun / FOV / 镜头晃动 / 走路摇晃），
但把**画面帧判定改成游戏 tick 判定** —— 同样的输入序列在 30 / 60 / 144 / 240 fps 下
产生**逐位相同**的轨迹。

---

## 2. 操作

| 按键 | 动作 |
|---|---|
| `W A S D` | 移动 |
| `Space` | 跳跃 / 抓住边缘（空中朝墙按住 W 触发攀爬） |
| `Ctrl` | 蹲下 |
| `Shift` | 冲刺 |
| `Shift + Ctrl` | 滑铲（需要速度 ≥ 200 hu/s = 5.08 m/s） |
| `Shift + Ctrl + Space` | 滑铲跳 |
| 反复「滑铲 → 跳 → 落地 → 滑铲」 | 连续滑铲跳 |
| 撞向台面（自动） | 四档翻越：Below / Level / Above / High |
| 撞向窗口（自动） | 窗口翻越 |
| 空中朝墙按住 W | 贴墙爬 |
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

# 自检 A：跨帧率确定性 + 33 项功能断言 + 20 项相机断言（纯数学，无引擎）
"$GODOT" --headless --path . res://addons/apex_movement/tests/determinism_test.tscn
# 期望：=== APEX MOVEMENT SELFTEST PASS ===

# 自检 B：39 项真实物理集成断言（跑真实 Godot 物理 + 真实场地）
"$GODOT" --headless --path . res://addons/apex_movement/tests/integration_test.tscn
# 期望：=== INTEGRATION PASS（39 项断言） ===

# 截图（需要窗口/真实渲染）
"$GODOT" --path . --rendering-driver vulkan --resolution 1920x1080 \
         res://addons/apex_movement/testbed/shot_testbed.tscn
# 出 14 张图到 user://shot_*.png：
#   8 张自由机位（overview / ramps / mantle_steps / vault_walls /
#                  window_frames / climb_wall / slide_lane / drop_plats）
#   6 张玩家第一人称（fp / fov110 / fov70 / walking / slide / mantle）
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
  MovementTypes.cs            状态/翻越档位/落地档位/输入/快照/世界接口
  ApexMovementCore.cs         求解器（状态机 + 加速/摩擦/跳跃/滑铲/翻越/攀爬/疲劳/Fallstun）
  ApexTickClock.cs            固定 tick 累加器（含死亡螺旋防护）

camera/                      ← 纯 C#，相机效果（可脱离引擎单元测试）
  CameraEffectsConfig.cs      FOV / 弹簧视角踢 / 侧倾 / 翻越插值 / 走路摇晃 参数
  ApexCameraRig.cs            每帧算出 FOV / roll / pitch-yaw 偏移 / 眼位偏移

godot_bridge/
  GodotMovementWorld.cs       IMovementWorld 的 Godot 实现（CastMotion / GetRestInfo / Raycast）

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
  FeatureTest.cs              33 项功能断言（纯数学）
  CameraTest.cs               20 项相机断言（纯数学）
  determinism_test.tscn       自检 A 入口
  integration_test.gd/.tscn   39 项真实物理集成断言（自检 B）
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

## 6. v0.2 相比 v0.1 的机制变化

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

### 附带修掉的三个问题

| 问题 | 现象 | 修法 |
|---|---|---|
| `AnalyticWorld.Sweep` 把"已在表面上"当成没撞到 | 角色每 tick 被吸附下压 0.5mm 后掉进"探测不到地面"的黑洞 | 按 Godot `CastMotion` 语义返回 `fraction=0` 的命中 |
| 窗口扫描的探针上限只到头顶上方 0.4m | 窗楣在 2.5m 时扫不到，被误判成"没有窗楣" | 提到头顶上方 0.9m |
| `Sprint`/`Walk` 共用一个速度字段 | 无法表达"持枪 260 / 收枪 299" | 拆成 `SprintSpeed` + `HolsterMultiplier` |

---

## 8. 自检结果（实测）

### A. 确定性 + 功能 + 相机（`determinism_test.tscn`，53 项断言）

```
=== 跨帧率确定性 ===
  参考（60fps）tick 数 = 240
  60fps vs 30fps:  一致（240 ticks，逐位相等）
  60fps vs 90fps:  一致（239 ticks，逐位相等）
  60fps vs 144fps: 一致（239 ticks，逐位相等）
  60fps vs 240fps: 一致（240 ticks，逐位相等）
  60fps vs 500fps: 一致（240 ticks，逐位相等）

=== 功能验证（33 项，全 PASS）===
  行走 4.407 / 冲刺 6.604 / 收枪冲刺 7.595 / 蹲行 2.032
  跳跃高度 1.487（期望 1.422 ± 0.10）、滞空 0.717s（wiki 0.74 ± 0.06）
  空中无摩擦（40 tick 速度差 0.00E+000）、Air strafe 加速 +0.83 m/s
  Slideboost 11.51 m/s（含收枪 ×1.15 上限 11.68）、2s 冷却、Deadslide、连续滑铲跳 3 次
  跳跃疲劳 0.462m（满高 1.422）、0.75s 后恢复
  四档翻越 Below/Level/Above/High、6m 攀爬、7.5m 超限拒绝、窗口翻越
  Fallstun、落地四档分档、重力与 wiki 双数据自洽（g=20.7801 vs 20.7802）

=== 相机子系统（20 项，全 PASS）===
  FOV 90 基准 / 冲刺 93 / 滑铲 96 / 夹紧 120 / 玩家可调 ±2°
  tan 空间插值 vs 线性：中途差 1.599°，终值差 0.0001°
  落地踢：瞬间低头 −0.0888 rad，1.5s 后回正；按档位单调（0.029 < 0.072 < 0.131 < 0.180）
  滑铲侧倾随速度增大（5m/s→2.04°，10m/s→3.26°）、冲刺侧倾 1.15°
  走路摇晃：静止 0 / 移动 0.0378m / 空中 0
  翻越相机脱开→回贴（0.997 → 0.000）、高度上限压低 0.65m
  FOV 收敛与帧率无关（60/144/240fps 差 < 0.0005°）

=== APEX MOVEMENT SELFTEST PASS ===
```

### B. 真实物理集成（`integration_test.tscn`，39 项断言）

```
  [PASS] 落地 / 脚底贴地 0.001 / 站立身高 1.950 / 相机高度 1.850 / 基础 FOV 90.000
  [PASS] 行走 4.407 / 平均位移 4.284 / 奔跑 6.604 / 收枪奔跑 7.595
  [PASS] 蹲下 0.975 / 脚底不下沉 / 相机高度 0.875 / 站起恢复
  [PASS] 跳跃高度 1.488 / 跳后重新落地
  [PASS] 滑铲触发 9.28 m/s / Slideboost / 相机侧倾 1.21° / 抬 FOV 94.20°
  [PASS] 连续滑铲跳 3 次
  [PASS] 翻越分档 Below(0.9m) / Level(1.7m) / Above(3.0m) / High(6.0m)
  [PASS] 6m 攀爬 6.000 / 爬上 15° 斜坡 / 翻越 0.8m 矮墙
  [PASS] 窗口翻越（state=Window）/ 穿过开口
  [PASS] 低天花板下无法站起 / 离开后站起
  [PASS] FOV 可调 100°/90° / 视角踢可触发 / 掉出世界自动复活

=== INTEGRATION PASS（39 项断言） ===
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
| 移动平台速度继承 | **未实现**（需求范围外） |
| 动画 | 无（纯物理位移 + 相机效果） |

---

## 11. 文档

- [`docs/wiki-reference.md`](docs/wiki-reference.md) —— **社区 wiki 数值换算表**（1hu=2.54cm、速度表、机制说明，每条带来源 URL）
- [`docs/reverse-engineering-v0.2.md`](docs/reverse-engineering-v0.2.md) —— **本轮逆向报告**（相机/FOV/晃动/翻越/滑铲/攀爬字段的 A 级证据 + 复现命令）
- [`docs/reverse-engineering.md`](docs/reverse-engineering.md) —— v0.1 逆向报告（PE 布局 / cvar 注册点 / 7 种 MANTLE 枚举）
- `../../docs/design/PLUGIN-CONVENTION.md` —— 插件化开发约定
- 截图：[`../../docs/reference/apex_movement/`](../../docs/reference/apex_movement/)
