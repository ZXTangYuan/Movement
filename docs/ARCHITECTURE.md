# 架构

> 怎么用 → [README](README.md)；参数值 → [PARAMETERS](PARAMETERS.md)；为什么这么改 → [VERSION-HISTORY](VERSION-HISTORY.md)。

## 1. 模块地图

```
core/                        ← 纯 C#，不引用 Godot（可脱离引擎单元测试）
  F3.cs                       确定性三维向量（标量 float，不走 SIMD）
  GaitModel.cs                步态模型：身高 → 腿长 → 步频/步幅/腾空比 → 起伏与点头
  PlanetProfile.cs            星球档案：常规系数（g / 空气密度）的唯一来源
  MovementConfig.cs           全部参数 + 证据等级 + 派生量 + ApplyPlanet()
  MovementTypes.cs            状态/翻越档位/落地档位/输入/快照/世界接口
                              （IMovementWorld / ISlipWorld / IFrictionWorld）
  ApexMovementCore.cs         求解器（状态机 + 加速/摩擦/跳跃/滑铲物理/三档障碍/
                              攀爬+扒墙统一状态机/打滑/疲劳/Fallstun）
  BobWaveform.cs              镜头摇晃波形（Gerono 双纽线 = 屏幕平面 ∞）
  ApexTickClock.cs            固定 tick 累加器（含死亡螺旋防护）

camera/                      ← 纯 C#，相机效果（可脱离引擎单元测试）
  CameraEffectsConfig.cs      FOV / 弹簧视角踢 / 翻越插值 / 步态摇晃 / ∞ 载体参数
  ApexCameraRig.cs            每帧算出 FOV / roll / pitch-yaw 偏移 / 眼位偏移

godot_bridge/
  GodotMovementWorld.cs       IMovementWorld + ISlipWorld + IFrictionWorld 的 Godot 实现
                              （CastMotion / GetRestInfo / QueryNormalAt / Raycast /
                                SampleSlip / SampleFriction）

player/
  ApexPlayer.cs               玩家节点：输入累积 + 固定 tick 驱动 + 相机应用 + HUD
  ApexDebugMenu.cs            `\` 调试菜单（FOV / 灵敏度 / 摇晃 / 冲击 / 星球 / 第三人称）
  ApexDebugRenderer.cs        三维调试渲染器（速度与加速度箭头 + 图例）
  apex_input_map.gd           输入动作自注册（不污染宿主项目）

testbed/
  testbed_builder.gd          程序化生成测试场地
  apex_testbed.tscn           可玩入口
  shot_testbed.gd/.tscn       多视角截图工具
  shot_debug_renderer.gd/.tscn  调试渲染器截图
  repro_stairs.gd/.tscn       上台阶顿挫复现 / A-B 对比

tests/                       ← 见 §4
```

## 2. 数据流（一个 tick）

```
渲染帧 (任意 fps)
  ├─ 累积鼠标增量、采样按键   （ApexPlayer._Process）
  └─ ApexTickClock.Tick(delta) → 0..4 次 do { 一次物理 tick }
                                   ↓
                     ApexMovementCore.Step(input, dt)      dt 恒为 1/60
                       ├─ 记录 _accelStartVel（加速度分解用）
                       ├─ 视角 → 计时器 → 独占状态（Mantle/Vault/Window、Climb/WallGrab）
                       ├─ UpdateCrouch（地面脚底锚定 / 空中中心锚定 + 脚不穿地）
                       ├─ UpdateSlip → 状态 switch（Slide/Slip/Ground/Hang/Air）
                       ├─ MoveWithCollision（扫掠 + 上台阶 + 撞墙判据）
                       ├─ UpdateGroundContact（落地切向投影 / 反穿透 / 贴地吸附）
                       ├─ ApplyStepTilt → UpdateBobPhase
                       └─ 结算 LastAccel（重力/摩擦/输入/冲量/碰撞/合计）
                                   ↓
                     MovementSnapshot（位置/速度/步态/状态/相机相位）
                                   ↓
                     ApexCameraRig.BuildOutput(snapshot)  ← 只读，不做物理
                                   ↓
                     相机变换 + HUD + ApexDebugRenderer
```

**关键：物理只在 tick 内推进，相机只读快照。** 相机不反推物理，物理不读渲染帧时间。

## 3. 帧率无关：四条硬规则

| 环节 | 做法 |
|---|---|
| 状态推进 | 只在固定 tick 内发生，`dt` 恒为 `1/TickRate`（默认 1/60） |
| 鼠标增量 | 渲染帧**累积**，tick 边界**整份消费**（总量守恒 ⇒ 累计转角一致） |
| 浮点 | 只用标量 `float` 加减乘除 + `MathF.Sqrt`，不用 SIMD 归约 |
| 走路摇晃 | 步态相位在 **tick 内**推进（不在渲染帧），所以摇晃也帧率无关 |
| 卡顿 | 单帧最多追赶 4 tick，超出丢弃并计数（防死亡螺旋） |

> 验证方式：`DeterminismTest` 用同一份输入序列在 30/60/144/240 fps 下跑，
> 断言轨迹**逐位相同**。

## 4. 测试分层（为什么要两套自检）

| 层 | 入口 | 覆盖什么 | 为什么不能合并 |
|---|---|---|---|
| **A. 纯数学自检** | `determinism_test.tscn` | 求解器 + 相机，跑在 `AnalyticWorld`（纯数学碰撞替身）上 | 毫秒级、可逐位确定性断言、能扫参数空间 |
| **B. 真实物理集成** | `integration_test.tscn` | Godot 真实物理 + 真实场地 + 真实输入事件 | 历史上「无法移动」「Ctrl 穿地」「跳跃初速被吸附吃掉」三个 bug **只存在于桥接层**，AnalyticWorld 完全复现不出来 |

`AnalyticWorld` 与 `GodotMovementWorld` 必须遵守同一份接口契约
（`IMovementWorld` / `ISlipWorld` / `IFrictionWorld`）。新增世界查询时两边都要实现，
否则 A 层绿、B 层红。

单元测试清单（每套 `Run()` 返回 `(bool Ok, string Detail, List<...> Rows)`）：

| 文件 | 项数 | 覆盖 |
|---|---|---|
| `DeterminismTest.cs` | 跨帧率 | 30/60/144/240 fps 逐位一致 |
| `FeatureTest.cs` | 44 | 核心功能（速度/跳跃/滑铲/材质/状态机） |
| `CameraTest.cs` | 26 | FOV / 弹簧 / ∞ 载体 / 滚转开关 |
| `ClimbTest.cs` | 51 | 攀爬 / 三档障碍 / tick 口径 / 疲劳跳抓墙 |
| `GaitTest.cs` | 26 | 步态推导（身高/速度 → 步频/幅度） |
| `SlipHangTest.cs` | 35 | 打滑 / 扒墙 |
| `SlideJumpTest.cs` | 38 | 滑铲跳链 / 平面速度入口 / 坡度 boost |
| `BobWaveformTest.cs` | 16 | ∞ 波形（光滑性 / 频率比 / 自交 / 退化） |
| `PhysicsTest.cs` | 32 | 重力 / 弹道 / 库仑摩擦 / 坡面合速度 / 加速度分解 |
| `FrictionTest.cs` | 30 | 体重 / 静摩擦驱动 / 冰面 / 滑铲输入 / 空中控制 |
| **合计** | **298** | |
| `integration_test.gd` | 65 | 真实物理集成 |

## 5. 状态机

```
                    ┌──────────── Space(跳) ────────────┐
                    ↓                                   │
   Ground ──蹲+快──→ Slide ──离地──→ Air ──落地──→ Ground │
     │                 │              │                 │
     │                 └──减速到蹲走──→ Ground          │
     │                                │                 │
     └──走进打滑体积──→ Slip ──────────┘                 │
                                                       │
   Air ──贴墙下落──→ WallHang ──W──→ Climb ──到顶──→ Hang ──W──→ Ground
     └──W+Space 撞墙起跳──→ Climb
   Ground/Air ──低障碍 W+Space──→ Vault/Mantle/Window（独占，期间不接受其他输入）
```

* **滑铲是纯地面状态**（v0.4.2 起）：`StepSlide` 首行就检查 `!_onGround` → 转 `Air`，
  空中任何 tick 都不可能是 `Slide`。这是「无限滑铲连跳」的结构性修复。
* **攀爬必须「跳出来的腾空」或「面向墙前进」**：`_leftGroundByJump` 豁免间隙门，
  按 `S`（`MoveZ < -0.3`）直接拒绝抓墙。
* **独占状态**：`Mantle/Vault/Window` 与 `Climb/WallGrab` 期间不接受移动输入改写速度。

## 6. 与宿主项目的边界

`core/` 与 `camera/` 不依赖 ProjectA 的任何类型，唯一与引擎耦合的两行是
`core/F3.cs` 里的 `Godot.Vector3` 隐式转换。

重力方向通过 `SetUpDirection(F3 up)` 注入（默认世界 Y 轴向上）。
内部一切运动都以 `_up` 为基准轴：

* 移动 = 投影到 ⊥up 平面
* 跳跃 = 沿 +up
* 重力 = 沿 −up
* 滑铲坡度 = 地面法线与 up 的夹角

所以接球形行星时**每 tick 传一次局部 up 即可**，不需要为每种运动写星球版。
已知限制：`AnalyticWorld` 的胶囊是固定 Y 轴形状，表达不了参考系整体旋转
（真实球形行星需要世界层 `Sweep/Raycast` 带朝向），见 `PhysicsTest ⑬` 的注释 TODO。
