# 可调参数总表（单一事实源）

> 本文是**唯一**记录参数当前值的文档。改代码里的默认值时必须同步改本文（见 MAINTENANCE.md）。

扫描范围（v0.5.3 工作树）：
- addons/apex_movement/core/MovementConfig.cs（主配置）
- addons/apex_movement/core/GaitModel.cs（GaitParams 步态常数表）
- addons/apex_movement/camera/CameraEffectsConfig.cs（相机效果）
- addons/apex_movement/core/PlanetProfile.cs（星球预设）

## 0. 怎么读这张表

| 列 | 含义 |
|---|---|
| 参数 | 代码里的字段名（或常量名），可直接在源码里搜到 |
| 值 | **代码里的默认值原文**（去掉 f 后缀，不做四舍五入）；运行时计算出来的量写「运行时计算」并给公式 |
| 单位 | m / m/s / m/s² / s / deg / Hz / kg / kg/m³ / tick / ×（倍率）/ —（无量纲或布尔） |
| 等级 | A = 二进制/官方直接观测，或 wiki 实测数值；B = 引擎谱系推导，未直接读到该值；Target = 设计目标（玩家可感知的效果，**不随星球变**）；派生 = 由别的量算出来，**禁止手调**；需求方口径 = 需求原文给定的角色尺寸；常量 = 代码 const 或被运行时强制；— = 注释未标等级（多为需求驱动）；已废弃 = 字段仍在但已无代码路径读取 |
| 说明 | 一句话用途；带推导注释的按原注释抄录（含推导公式与反推条件） |

三条读表约定：
1. **只有「等级」列不是 A/B/Target/派生 的行才是可以直接拧的旋钮**；派生行改不动，它跟着常规系数与设计目标自动变。
2. **目标（Target）与常规系数（§10 星球）是解耦的**：切星球只改常规系数，Target 不变，派生量自动重算。
3. 本表按「主题分节 + 节内保持代码字段顺序」排列，便于和源码逐行对照。

## 1. 核心速度与几何

### 1.1 单位换算与求解器时间轴

| 参数 | 值 | 单位 | 等级 | 说明 |
|---|---|---|---|---|
| HuToMeter | 0.0254 | m/hu | A | 1 hu = 2.54 cm；wiki 的 hu/s 数值全部靠它换算成 m/s |
| PinnedTickRate | 60 | Hz | 常量 | 钉死的移动 tick 频率（需求：固定 60 tick/s，作为求解器时间轴的定义，不是可调手感参数） |
| PinnedMaxCatchUpTicks | 4 | tick | 常量 | 钉死的追赶上限；60 Hz 下单帧最多补 4 tick ≈ 66 ms 卡顿，防死亡螺旋 |
| TickRate | 60 | Hz | C | 求解器实际 tick 频率，默认取 PinnedTickRate；ApexPlayer 会拒绝被改大/改小 |
| MaxCatchUpTicks | 4 | tick | C | 单帧最多追赶的 tick 数 |
| AutoTraverse | true | — | C | 是否在 ApexMovementCore.Step 内自动尝试翻越/攀爬 |
| TraversalProbeInterval | 2 | tick（2 tick = 30 Hz） | C | 翻越/窗口探针抽样间隔（最贵的一段，1 = 每 tick，4 = 15 Hz） |
| ClimbProbeInterval | 1 | tick（= 60 Hz） | C | 攀爬附着探针间隔（一次性且决定贴墙瞬抓不抓，故不降频） |
| ClimbWallProbeInterval | 2 | tick（= 30 Hz） | C | 攀爬到顶探针间隔（持续开销，墙顶不会一帧消失） |
| SlipProbeInterval | 2 | tick（= 30 Hz） | C | 打滑体积采样间隔（体积静态，降频不影响判定） |

### 1.2 地面速度

| 参数 | 值 | 单位 | 等级 | 说明 |
|---|---|---|---|---|
| WalkSpeed | 4.407 | m/s | A | 行走顶速 = 173.5 hu/s（wiki Ground：a walk peaks at 173.5 hu/s） |
| SprintSpeed | 6.604 | m/s | A | 冲刺顶速 = 260 hu/s（持枪；wiki Sprint） |
| CrouchWalkSpeed | 2.0 | m/s | A | 蹲行顶速；注释标 80 hu/s（= 2.032），代码实际取 2.0 |
| HolsterMultiplier | 1.15 | × | A | 收枪加速 15%：6.604 × 1.15 = 7.5946 m/s = 299 hu/s（玩家感知的 Apex 冲刺速度） |
| GroundAccel | 14 | m/s² | Target | 地面驱动加速度（v0.5.3 由 10 提到 14）；到顶速 6.604/14 = 0.47 s；受抓地条件 μs·g·cosθ ≥ 本值约束 |
| SprintAccel | 14 | m/s² | Target | 冲刺加速度（wiki：与行走相同，保留独立字段便于单独调） |
| CrouchAccel | 14 | m/s² | Target | 蹲行加速度（wiki：与行走相同） |

### 1.3 角色几何（需求方口径，优先级最高）

| 参数 | 值 | 单位 | 等级 | 说明 |
|---|---|---|---|---|
| StandHeight | 1.90 | m | 需求方口径 | 站立胶囊总高（v0.5 需求 m00952 由 1.95 → 1.90） |
| CrouchHeight | 0.95 | m | 需求方口径 | 蹲下胶囊总高 = 站立一半 |
| EyeHeightOffset | 0.10 | m | 需求方口径 | 眼位 = 胶囊顶端 − 0.10 m（站立 1.80 m / 蹲下 0.85 m） |
| CapsuleRadius | 0.406 | m | A | 胶囊半径；注释标 16 hu = 0.4064，代码实际取 0.406 |
| CrouchTransitionTime | 0.12 | s | C | 蹲下/站起过渡时间 |
| AirCrouchShrinksToMiddle | true | — | A | 空中蹲下胶囊向中间收缩（脚底上升），地面蹲下向底部收缩（脚底不动）；这是 slidehop 与 bhop 手感不同的根因 |

## 2. 跳跃与重力

| 参数 | 值 | 单位 | 等级 | 说明 |
|---|---|---|---|---|
| Gravity | 9.80665 | m/s² | A（v0.5 改为地球真值） | 重力加速度；注释里的 20.7802 是 Apex 等效值（由 h=56hu、t=0.74s 反解得 8h/t² = 20.7802），v0.5 起改用地球真实值，Apex 等效值保留在 PlanetProfile.ApexDefault |
| Planet | Earth | — | 配置 | 当前星球档案（常规系数来源），默认 Earth（g=9.80665、ρ=1.225）；切星球一律走 ApplyPlanet，它会同步 Gravity |
| MaxFallSpeed | 88.9 | m/s | B | 终端下落速度上限（实现是硬夹）；Source sv_maxvelocity = 3500 u/s ≈ 88.9 |
| JumpHeight | 0.75 | m | Target | 设计目标跳跃高度（**脚底板离地**口径，不是解析值）；v0.4.x 为 56 hu = 1.4224 |
| JumpAccelTime | 0 | s | —（需求 m00460） | 起跳的有限加速时间；0 = 瞬时冲量（Apex 现行行为，默认值）；必须落在 FixedDelta 整数倍上，否则破坏帧率不变性 |
| JumpBufferTime | 0.12 | s | C | 跳跃缓冲窗口：落地前按跳，落地瞬间自动触发 |
| CoyoteTime | 0.20 | s | A | 土狼时间（wiki Coyote time：lasts for 0.2sec） |
| JumpFatigueMinTime | 0.15 | s | A | 疲劳最短窗口 = antiMultiJumpTimeMin = 0.15000006 |
| JumpFatigueMaxTime | 0.75 | s | A | 疲劳衰减完成时间 = antiMultiJumpTimeMax = 0.75 |
| JumpFatigueHeightFrac | 0.30 | × | A | 疲劳最低跳跃高度比例 = antiMultiJumpHeightFrac = 0.30 |
| BunnyHopWindow | 1.0 | s | A | 落地后此时长内离地算 bhop（游戏内 skip_time = 1s） |
| BunnyHopSpeedPenalty | 2.54 | m/s | A | bhop 减速上限 = 100 hu/s（超过冲刺速度时每次 bhop 扣这么多） |
| JumpGraceCutoffSpeed | 12.0 | m/s | A | 跳跃 grace 的 cutoff 速度：超过它就不再施加 bhop 减速 |
| SlideHopWindowAfterLanding | 0.20 | s | C | 落地后多久内「按住蹲再跳」算作 slidehop |

## 3. 滑铲

| 参数 | 值 | 单位 | 等级 | 说明 |
|---|---|---|---|---|
| SlideMinStartSpeed | 5.0 | m/s | —（v0.5 需求 m01202） | 滑铲触发门槛：判定用**当前行走平面内**的移动速度（不含 slideboost）；原 200 hu/s = 5.08，按取整要求收敛到 5.0 |
| SlideBoostSpeed | 3.81 | m/s | —（v0.5 需求 m01202） | slideboost 增量 = 150 hu/s（**水平面口径**）；加到沿坡速度上要换算 boost_沿坡 = 本值 / cosθ |
| SlideBoostCooldown | 2.0 | s | A | slideboost 冷却（wiki Slide：2sec cooldown） |
| SlideFrictionCoefficient | 0.45 | — | C | 滑铲地面库仑摩擦系数 μ；沿坡合加速度 a = g·sinθ − μ·g·cosθ，临界角 tanθ = μ |
| SlideMaxDuration | 6.0 | s | C | 滑铲最长持续时间（wiki 说滑铲本身没有计时器，这里只是防卡死兜底） |
| SlideSteerFactor | 0.15 | × | C | 滑铲期间的转向能力（相对正常加速的比例） |
| SlideReverseInputPenalty | 3.0 | × | C | 滑铲期间反向输入的额外摩擦倍率（wiki：反向输入减速非常剧烈） |
| SlideJumpSpeedThreshold | 8.89 | m/s | A | Slidejump 速度阈值 = 350 hu/s；只有速度低于此值**或**进入滑铲超过 0.24 s 才叠加跳跃初速 |
| SlideJumpTimeWindow | 0.24 | s | A | Slidejump 时间窗（wiki：0.24sec after initiating the Slide） |
| SlideJumpSpeedRetention | 1.0 | × | C | 滑铲跳水平速度保留系数（跳跃时水平速度乘以此值） |

滑铲的「结束速度」不是独立字段：SlideExitSpeed => CrouchWalkSpeed（2.0 m/s，见 §11）。

## 4. 摩擦（静/动）与刹车

| 参数 | 值 | 单位 | 等级 | 说明 |
|---|---|---|---|---|
| GroundFrictionCoefficient | 0.7 | — | Target | 地面动摩擦 μk：a_brake = μ·g·cosθ，平地 0.7×9.80665 = 6.8647 m/s²，冲刺 6.604 m/s 松键停距 v²/(2a) = 3.18 m；坡度越陡摩擦越小（法向力小），星球重力越大刹车越狠 |
| GroundStaticFrictionCoefficient | 1.9 | — | Target | 最大静摩擦 μs；由「抓地条件 μs·g·cosθ ≥ GroundAccel」反推：30° 坡 μs ≥ 14/(9.80665×0.866) = 1.65，取 1.9 留 15% 余量（45° 坡 μs·g·cosθ = 13.2 < 14 ⇒ 开始轻微打滑）。改 GroundAccel 必须同步改它 |
| BrakeRampTime | 0.25 | s | —（v0.5.3 需求 m02395） | 刹车从最大制动线性衰减到滑动摩擦所需时间；a(t) = μk·g·cosθ + (a₀ − μk·g·cosθ)·(1 − min(1, t/T))，a₀ = min(μs·g·cosθ, GroundAccel)（平地 14 m/s²） |
| ActiveBrakeMultiplier | 2.0 | × | C | 主动急停加成：输入方向与速度方向相反时 μ 乘此倍率 ⇒ a_brake = 2.0×0.7×g = 13.73 m/s²，6.6 m/s 停距 ≈ 1.59 m。滑铲**不吃**这个加成 |
| SlopeSpeedTimeConstant | 0.25 | s | C | 跑动坡面合速度时间常数 τ：v_target = v_base + τ·g·sinθ·(下坡方向·期望方向)；走（4.407）与蹲走（2.0）不吃这个加成 |
| StopSpeed | 2.54 | m/s | A | 停止速度基准 = 100 hu/s（Source sv_stopspeed 同值） |
| BodyBmi | 21.7 | kg/m² | —（需求 m01928） | BMI 取正常区间 18.5–24.9 的中位数 21.7；体重 = BMI × 身高² = 78.337 kg |
| BodyMassOverride | 0 | kg | — | 体重覆盖值；>0 时忽略 BMI 计算（用于特殊角色/装备） |

体重在纯移动系统里**会在摩擦加速度里约掉**（a = μ·N/m = μ·g·cosθ），所以不改走跑滑的加减速曲线；它只在「力」的地方起作用（调试渲染器显示 N、未来刚体冲量 Δv = J/m），派生见 §11 的 BodyMass / NormalForce / MaxStaticFrictionForce / MaxKineticFrictionForce。

## 5. 空中

### 5.1 空中控制

| 参数 | 值 | 单位 | 等级 | 说明 |
|---|---|---|---|---|
| AirAccel | 10 | — | B | 空中加速度系数（wiki 的 Air acceleration = 500 有未读到的单位缩放，直接套 Source 公式会得到 12.7 m/s 每 tick，故归一化为 Source 谱系默认 10） |
| AirWishSpeed | 1.524 | m/s | A | 空中 wishspeed = 60 hu/s；这是 air strafe 的物理根因：addSpeed = wishSpeed − dot(velocity, wishDir)，只有投影小于 1.524 才加速 |
| AirControlAccel | 1.2 | m/s² | —（需求） | 空中输入加速度；只负责微调方向，不再能把 6.6 m/s 加到 7.95 m/s |
| AirSteerSpeedAllowance | 0.5 | m/s | —（需求） | 空中允许比离地时多出的速度：水平速度不会超过「离地速度 + 本值」，把「空中只能靠惯性」做成硬不变量 |
| AirFriction | 0 | — | A | 空中**没有**摩擦（wiki 引擎篇：Apex does not feature any Air friction） |

### 5.2 落地分档与 Fallstun

| 参数 | 值 | 单位 | 等级 | 说明 |
|---|---|---|---|---|
| LandDistLow | 2.0 | m | A | 落地分档：低（对应 landDistLow 的量级），下落距离 < 此值 → LandingTier.Low |
| LandDistMedium | 5.0 | m | A | 落地分档：中（对应 landDistMedium） |
| LandDistHigh | 10.0 | m | A | 落地分档：高（对应 landDistHigh） |
| HardFallDist | 20.32 | m | A | 硬着陆阈值（对应 hardFallDist）= 800 hu |
| FallStunMinHeight | 7.62 | m | A | Fallstun 起始下落高度 = 300 hu |
| FallStunMaxHeight | 20.32 | m | A | Fallstun 满值下落高度 = 800 hu |
| FallStunMaxDuration | 1.0 | s | A | Fallstun 最长持续时间（wiki Fallstun） |
| FallStunSpeedScale | 0.35 | × | C | Fallstun 期间的顶速比例（wiki：从 0 到完全减速） |

## 6. 攀爬 / 翻越 / 贴墙

### 6.1 障碍分级与台阶

| 参数 | 值 | 单位 | 等级 | 说明 |
|---|---|---|---|---|
| OverwalkableHeight | 0.25 | m | C | 台阶高度上限（v0.4 需求方口径）：≤ 0.25 m 视为台阶，自动平滑跨越、速度大小不变（只倾斜方向）；三档分级为 ≤0.25 台阶 / (0.25, 1.0] 翻越 / >1.0 墙。v0.3 及之前是 0.762（Apex 的 30 hu overwalkable） |
| StepTiltTime | 0.12 | s | C | 台阶过渡倾斜时长：速度方向抬到 θ = atan(b/a) 再落回水平的总时间，约等于脚掌踩上台阶再蹬直 |
| StepTiltFootRadius | 0 | m | C | 台阶倾角的横向基准 a（脚底中心到脚底边缘）；传 ≤0 时运行时用 CapsuleRadius |
| VaultMaxHeight | 1.0 | m | C | 小障碍高度上限（v0.4 需求方口径）；高于它就是墙（贴墙按 W 只撞停，必须 W+Space 起跳后走攀爬） |
| VaultSpeedRetention | 0.85 | × | C | 翻越小障碍时的水平速度保留系数（只损失 15%）；对比攀爬时水平速度直接掉到 0 |
| VaultRecoverTime | 0.25 | s | C | 翻越后恢复到满速的时间（这期间地面加速度放大） |
| VaultRecoverAccelBoost | 3.0 | × | C | 翻越后加速宽限期的加速度倍率 |

### 6.2 攀爬（Climb）

| 参数 | 值 | 单位 | 等级 | 说明 |
|---|---|---|---|---|
| AutoMantleMaxHeight | 2.03 | m | A | Auto-mantle 高度上限；注释标 80 hu = 2.032，代码实际取 2.03 |
| MantleMaxHeight | 6.9342 | m | A | 攀爬总高度上限 = 273 hu（wiki 引擎篇 + Climb 的 climb space cutoff） |
| ClimbAttachOffset | 2.54 | m | A | 攀爬附着偏移 = 100 hu（Attach offset，个人体力上限） |
| ClimbEndBoost | 0.7112 | m | A | 攀爬结束额外上抛 = 28 hu（End boost） |
| ClimbUpwardSpeedThreshold | 0.254 | m/s | A | 判定「向上爬」的垂直速度阈值 = 10 hu/s |
| ClimbNonUpwardTimer | 1.0 | s | A | 非向上爬的脱离计时；v0.3 起不再是主脱离计时（主计时改 ClimbNoInputGrace），本字段只保留给「按着输入但实际在向下/侧向爬」 |
| ClimbSpeed | 2.0 | m/s | C | 向上攀爬速度 |
| ClimbAttachMaxViewAngle | 45.57 | deg | A | 允许**附着**攀爬的最大视角偏角（wiki Climb L40：up to 45.57° away from a wall；= arccos(0.7)）；与 MantleMaxViewAngle（50°）刻意区分 |
| ClimbMinApproachSpeed | 0.5 | m/s | C | 附着攀爬所需的最小**朝墙速度**（≈20 hu/s）；决定粘不粘墙的是朝墙动量而不是有没有按 W |
| ClimbNoInputGrace | 0.22 | s | C | 松 W 后仍留在墙上的宽限时长（点一下 W 再松仍能完成一次抓附） |
| ClimbHangApproachDistance | 0.35 | m | C | 「即将扒墙」时额外补爬的距离：让手正好够到台沿再切 Hang |
| ClimbHangApproachTime | 0.12 | s | C | 上面那段补爬的时长（距离/时长 = 该阶段爬升速度） |
| ClimbReattachNormalAngle | 25.842 | deg | A | 重附着豁免角（wiki Climb L172-175）：新表面与旧表面法线夹角 ≥ 此值视为换了面，豁免「必须掉回上次 attach point 以下才能再附着」 |
| ClimbToHangEnabled | true | — | C | 是否允许从攀爬过渡到扒墙（Hang）；关掉退回「到顶直接弹上去」 |

### 6.3 翻越（Mantle）

| 参数 | 值 | 单位 | 等级 | 说明 |
|---|---|---|---|---|
| MantleMaxViewAngle | 50 | deg | A | 允许触发翻越的最大视角偏角（wiki Mantle grab：within 50° to each side） |
| MantleGrabTime | 0.45 | s | A | Mantle grab 时长（wiki Mantle） |
| MantlePullTime | 0.45 | s | A | Mantle pull 时长（wiki Mantle） |
| MantleGrabFraction | 0.45 | × | C | grab 占整段翻越时长的比例（两段式轨迹的切分点） |
| MantleDurationBelow | 0.55 | s | C | MantleClass.Below 档总时长（对应 mantleDurationBelow） |
| MantleDurationLevel | 0.75 | s | C | MantleClass.Level 档总时长（对应 mantleDurationLevel） |
| MantleDurationAbove | 0.90 | s | C | MantleClass.Above 档总时长（对应 mantleDurationAbove） |
| MantleDurationHigh | 1.20 | s | C | MantleClass.High 档总时长（对应 mantleDurationHigh） |
| MantleLevelHeight | 0.975 | m | C | 分档阈值①：台面高度 ≤ 此值 → Below |
| MantleAboveHeight | 1.95 | m | C | 分档阈值②：台面高度 ≤ 此值 → Level |
| MantleHighHeight | 3.45 | m | C | 分档阈值③：台面高度 ≤ 此值 → Above，否则 High |
| MantleForwardOffset | 0.55 | m | C | 翻越结束时的前推距离 |
| TraversalReach | 0.9 | m | C | 触发翻越/攀爬的最大前方探测距离 |
| TraversalCooldown | 0.2 | s | C | 翻越/攀爬冷却 |
| EnableMantleHang | true | — | C | 松手后是否进入挂边（Mantle hang）而不是自动完成 |

### 6.4 窗口翻越（TRAVERSAL_WINDOW）

| 参数 | 值 | 单位 | 等级 | 说明 |
|---|---|---|---|---|
| WindowTraversalEnabled | true | — | C | 是否启用窗口翻越 |
| WindowDuration | 0.70 | s | C | 窗口翻越时长（对应 traversal_window_duration） |
| WindowForwardOffset | 1.20 | m | C | 穿过窗口后的前推距离（traversal_window_forward_offset） |
| WindowSidewaysOffset | 0.0 | m | C | 穿过窗口时的侧向偏移（traversal_window_sideways_offset） |
| WindowHandVerticalOffset | 0.0 | m | C | 手部垂直偏移（traversal_window_hand_vertical_offset） |
| WindowFinishAngle | 0.0 | deg | C | 窗口翻越的收尾角度（traversal_window_finish_angle） |
| WindowRequiredWidth | 1.0 | m | C | 窗口开口的最小宽度（traversal_hand_required_width） |

### 6.5 扒墙 / 悬垂（Wall hang / dangle）

| 参数 | 值 | 单位 | 等级 | 说明 |
|---|---|---|---|---|
| WallHangEnabled | true | — | C | 是否启用扒墙（关闭后空中按蹲不再抓墙） |
| WallHangRequiresCrouch | false | — | C | 扒墙是否必须按住蹲键；v0.4 起默认 false（需求：默认扒墙，不需要按任何按键） |
| WallHangIdleFallTime | 1.2 | s | C | 扒墙待机时多久没有任何输入就掉下去（是无输入计时，不是握力计时） |
| WallHangLateralFallTime | 6.0 | s | C | 只横移（A/D）不往上爬时，握力从满到空要多久 |
| WallHangGripRestoreTime | 2.5 | s | C | 按 W 上爬时握力从空回满要多久（对应 m_wallDangleClimbProgressFloor） |
| WallHangMaxViewAngle | 45.57 | deg | A | 扒墙的最大视角偏角，与攀爬同源 = arccos(0.7) |
| WallHangReach | 0.9 | m | C | 扒墙的前向抓取距离（在胶囊半径之外还要够到多远） |
| WallGrabMaxRecedeSpeed | 0.05 | m/s | C | 抓墙时允许的「背离墙面」速度上限（v0.4.2 新增）。判据 approach = −dot(水平速度, 墙面法线)：approach > 0 抓；approach < −本值 不抓；|approach| ≤ 本值 也抓（保留下落自动扒住）。按着 W 时无条件放行 |
| WallGrabMinGroundClearance | 0.30 | m | C | 抓墙要求的最小离地间隙：脚底到脚下地面距离小于此值就不抓（防止贴墙走下小坡被凭空吸到墙上）。v0.5 二次调整后**只对非跳跃腾空生效**，跳出来的腾空完全豁免 |
| WallHangShimmySpeed | 0.8 | m/s | C | 沿墙左右横移的速度（ACT_MP_WALLHANG_LEFT/RIGHT） |
| WallHangMaxTime | 6.0 | s | C | 单次扒墙的最长持续时间（m_wallHangTime 的上限） |
| WallHangSlipSpeed | 0.22 | m/s | C | 松手后的下滑速度（对应 sound_wallHangSlip：挂着不动会缓慢下滑） |
| WallHangMaxSlipDistance | 1.2 | m | C | 累计下滑多少米后脱手（对应 m_wallDangleClimbProgressFloor 的进度下限） |
| WallHangClimbRestoresGrip | true | — | C | 扒墙时按 W 是否能回补握力（抵消下滑） |
| WallHangDisableWhenAutoMantleable | true | — | C | 是否禁止在可自动翻越的矮墙上扒墙（对应 automantle_disable_hang） |
| WallHangReattachCooldown | 0.35 | s | C | 脱手后再次扒墙的冷却 |
| WallHangCompleteForwardSpeed | 1.2 | m/s | C | 从扒墙「完成」（翻上去）时给的前推速度（sound_wallHangComplete） |

## 7. 台阶与贴地

| 参数 | 值 | 单位 | 等级 | 说明 |
|---|---|---|---|---|
| GroundSnapLength | 0.20 | m | C | 贴地吸附长度（等价 Godot floor_snap_length）：① 接地探测距离；② 命中后把胶囊压到接触面（留 1 mm 皮肤） |
| WalkableNormalDot | 0.7 | — | A | 可行走坡面法线点积阈值 = arccos(0.7) = 45.572° |

台阶的倾斜几何由 StepTiltTime / StepTiltFootRadius 控制（见 §6.1）。

## 8. 打滑（slip）

逆向依据：docs/REVERSE-ENGINEERING.md §2.5.10（DT_TriggerSlipSphere / C_TriggerSlipSphere，字段 m_slipSpeed / m_slipAcceleration / m_defaultSlipDirection；玩家侧 m_slipAirRestrictTime 等）。语义：走进打滑体积 → 摩擦失效、被体积的 speed/accel 拽向体积方向；离开后一段时间内空中转向被限制在离开方向附近。

| 参数 | 值 | 单位 | 等级 | 说明 |
|---|---|---|---|---|
| SlipAirRestrictDuration | 0.6 | s | C | 离开打滑体积后「空中方向限制」持续多久（slipAirRestrictDuration；数值未读到，取 0.6 s 手感值） |
| SlipMinSpeedToRecordDirection | 1.0 | m/s | C | 记录「离开方向」所需的最小水平速度；太慢就沿用体积方向，免得记录出抖动的零向量 |
| SlipAirRestrictConeDeg | 35 | deg | C | 方向限制的圆锥半角：离开滑区后的空中加速只能落在这个圆锥内（完全锁死会让玩家觉得失控） |
| SlipSteerFactor | 0.15 | × | C | 打滑态的转向能力（0 = 完全不能转向，1 = 和地面一样） |
| SlipDragRefSpeed | 1.0 | m/s | —（需求） | 打滑耗散的滑移速度参考值：drag = μk·g·cosθ·(1 − grip)·(1 + v/本值)；(1 + v/vRef) 项让打滑地面有稳定终端速度（冰面 μk=0.05 实测终端速度 ≈ 1.1 m/s） |

## 9. 步态与镜头摇晃

### 9.1 GaitParams（`core/GaitModel.cs`，默认值在 `MovementConfig.Gait` 里给）

| 参数 | 值 | 单位 | 等级 | 说明 |
|---|---|---|---|---|
| `LegLengthRatio` | 0.53 | × 身高 | C | 腿长 / 身高（人体测量学 0.53）。 |
| `FroudePref` | 0.45 | — | C | 偏好行走速度的 Froude 数（人类实测 0.45）。 |
| `RunStartFroude` | 1.9 | × | C | 走→跑切换阈值（相对 vPref 的倍数，实测 1.9）。 |
| `WalkCadenceRef` | 1.9 | Hz | C | v = vPref 时的行走步频 (steps/s)。 |
| `WalkCadenceExp` | 0.6 | — | C | 行走步频随速度的指数（文献 0.5~0.7）。 |
| `RunCadenceBase` | 2.75 | Hz | C | 跑步基础步频 (steps/s)。 |
| `RunCadenceGain` | 0.14 | Hz | C | 跑步步频随速度的线性增益 (steps/s per m/s)。 |
| `GaitBlendWidth` | 0.15 | — | C | 走/跑混合带的半宽（相对 vRunStart，0.15 = ±15%）。 |
| `MaxFlightFraction` | 0.38 | — | C | 冲刺时的腾空相占比上限。 |
| `WalkAbsorb` | 0.16 | — | C | 行走时垂直起伏的保留系数（膝踝缓冲吸收掉其余部分）。 |
| `RunCompression` | 0.05 | — | C | 奔跑支撑相的腿部压缩（相对腿长）。 |
| `VerticalScale` | 0.24 | — | C | 垂直起伏总缩放（舒适度总闸）。 v0.4.2：0.40 → 0.24。冲刺（7.6 m/s，带收枪 1.15 倍）峰到峰从 2.0 cm 降到 ≈1.2 cm —— 需求方反馈「走路/奔跑的持续摇晃还是太剧烈」， 这一步是主降幅旋钮。 |
| `LateralPerStride` | 0.004 | — | C | 侧向摆动 / 步幅。 |
| `LateralMin` | 0.0025 | m | C | 侧向摆动下限 (m)。 |
| `LateralMax` | 0.010 | m | C | 侧向摆动上限 (m)。 |
| `RampInSpan` | 0.90 | s | C | 起步软着陆跨度 (m/s)：幅度在 MinSpeed → MinSpeed + RampInSpan 之间用 smoothstep 从 0 升到满值，避免「啪」地开始晃。 |
| `BobCadenceMaxHz` | 3.0 | Hz | C | 相机摇晃使用的步频上限 (Hz)。相位推进用 min(CadenceHz, BobCadenceMaxHz)： 冲刺真实步频 3.4 Hz 直接用在画面上会「嗡嗡」抖，压到 3.0 Hz 仍然跟得上 步点，但明显更稳。 本身保持真实值（HUD 用）。 |
| `PitchPerCm` | 0.20 | deg/cm | C | 点头幅度 (deg per cm 垂直起伏)。 |
| `RollPerCm` | 0.16 | deg/cm | C | 侧倾幅度 (deg per cm 侧向摆动)。 |
| `MaxPitchDeg` | 0.45 | deg | C | 点头幅度上限 (deg)。 |
| `MaxRollDeg` | 0.30 | deg | C | 侧倾幅度上限 (deg)。 |
| `MinSpeed` | 0.35 | m/s | C | 低于此水平速度完全不晃 (m/s)。 |
| `CrouchCadenceDrop` | 0.35 | — | C | 蹲行时步频的下降比例。 |
| `CrouchAmplitudeDrop` | 0.20 | — | C | 蹲行时幅度的下降比例。 |

> 这一组是 **v0.4 引入的步态模型常数（C 级）**：频率与身高/速度的耦合关系有文献依据
> （腿长比例、Froude 数、走跑切换阈值），但绝对幅度是照需求方舒适档拟合出来的。
> 逐条推导见 [VERSION-HISTORY.md](VERSION-HISTORY.md) 的 v0.4 一节与 `core/GaitModel.cs` 的注释。

### 9.2 CameraEffectsConfig（`camera/CameraEffectsConfig.cs`）

| 参数 | 值 | 单位 | 等级 | 说明 |
|---|---|---|---|---|
| `FovBase` | 90 | deg | C | [C] 基础水平 FOV (deg)。Apex 游戏内 FOV 设置范围是 70–110。 |
| `FovMin` | 60 | deg | C | [C] FOV 允许范围（会被夹住）。 |
| `FovMax` | 120 | deg | C |  |
| `FovSprintAdd` | 3 | deg | C | [C] 冲刺时叠加的 FOV (deg)。 |
| `FovSlideAdd` | 6 | deg | C | [C] 滑铲时叠加的 FOV (deg)。 |
| `FovTraversalAdd` | -4 | deg | C | [C] 攀爬/翻越时的 FOV 收缩 (deg，负值 = 变窄)。 |
| `FovSmoothing` | 8 | 1/s | C | [C] FOV 平滑速度（1/s，越大越快跟上目标）。 |
| `FovUserStep` | 2 | deg | C | [C] 玩家可用按键调节的 FOV 步长 (deg/次)。 |
| `SpringStiffness` | 180 | 1/s | C | [C] 弹簧刚度 k（`viewkick_spring`）。运动方程 a = −k·x − c·v。 ω = sqrt(k) = 13.4 rad/s ≈ 2.1 Hz，接近"落地一顿"的观感。 |
| `SpringDamping` | 18 | — | C | [C] 弹簧阻尼 c（`viewkick_spring_cooldown_fadetime` 的对应物）。 |
| `KickScale` | 1.0 | — | C | [C] **视角踢总幅度倍率**（v0.3 新增）。 需求「镜头晃动过于剧烈」的**手感总闸**：所有冲量（落地 / 跳跃）在 入口统一乘上它，所以调这一个数就能 整体放大/缩小镜头晃动，不必逐档改数组。 默认 1.0（各项已在下面的数组里按目标幅度标定好）；0 = 完全关闭视角踢； 想更"硬核"可调到 1.3 左右。 |
| `JumpPitchImpulse` | 0.10 | — | C | [C] 跳跃时给一点向上的视角踢（起跳"抬镜头"）。峰值 ≈ 0.28°。 |
| `MaxKickAngle` | 0.075 | deg | C | [C] 视角踢的总幅度上限 (rad)，防止极端情况把镜头甩飞。 v0.3 从 0.18 rad (10.3°) 收到 0.075 rad (4.3°)：让它只当**保险丝**， 而不是像 v0.2 那样成为"每次大落地都撞到"的实际工作点。 |
| `SlideMaxRollDeg` | 5.0 | deg | C | [C] 滑铲最大侧倾角 (deg)（`slide_viewTiltSide` 的幅度）。 |
| `SlideRollIncreaseSpeed` | 60 | 1/s | C | [C] 滑铲侧倾增大速率 (deg/s)（`slide_viewTiltIncreaseSpeed`）。 |
| `SlideRollDecreaseSpeed` | 35 | 1/s | C | [C] 滑铲侧倾减小速率 (deg/s)（`slide_viewTiltDecreaseSpeed`）。 |
| `SlideRollPlayerSpeedRef` | 8.0 | m/s | C | [C] 侧倾角达到最大时的参考速度 (m/s)（`slide_viewTiltPlayerSpeed`）。 |
| `SlideRollSide` | 1 | — | C | [C] 侧倾方向（+1 右倾 / −1 左倾）。 |
| `SprintRollEnabled` | false | — | C | 跑步侧倾（绕前向轴的滚转）开关。默认 **false**（v0.4.4 起按需求关掉）。 打开即恢复 v0.4.3 的 行为。 |
| `SprintMaxRollDeg` | 1.5 | deg | C | [C] 冲刺最大侧倾角 (deg)（`sprinttiltMaxRoll`）。受 控制。 |
| `BobRollEnabled` | false | — | C | 走路摇晃的**滚转**通道（bobRoll）开关。默认 **false**（同上，按需求关掉）。 |
| `BobAngularRefDistance` | 1.0 | m | C | ∞ 的载体：**只有角度模式**（v0.5 定稿，需求 m01070）。 眼位不动，**视线方向**在垂直于前向的截面上画 ∞。 需求方 m01070：「镜头方面就用新的 ∞ 载体开关的这个方式，我很满意， 把这个调成默认的镜头晃动效果吧，原先的那种可以删了」—— 所以 v0.4.3 的"移动眼位"平移模式**已整体删除**，没有开关、没有分支。 屏幕中心点始终对应当前视线方向，∞ 是"看的方向在画"。 |
| `SprintRollSpeed` | 12 | 1/s | C | [C] 冲刺侧倾的跟随速度 (deg/s)。 |
| `TraversalLerpOutDuration` | 0.12 | — | C | [C] 翻越时相机"脱开"（lerp out）的时长 (s)（`traversal_viewLerpOutPos` / `OutAngle`）。 0 = 相机完全跟随（生硬但无延迟）。 |
| `TraversalLerpInDuration` | 0.20 | — | C | [C] 翻越结束后相机"回贴"（lerp in）的时长 (s)（`traversal_viewLerpInDuration`）。 |
| `TraversalCapCameraHeight` | 2.20 | m | C | [C] 翻越期间相机允许的最大离地高度 (m)（`traversal_mantle_cap_camera_height`）。 翻越高墙时相机不会跟着飞到很高，避免眩晕。 |
| `TraversalPositionLag` | 0.45 | — | C | [C] 翻越期间相机位置滞后的比例（0 = 不滞后，1 = 完全不动）。 |
| `TraversalAngleLag` | 0.35 | — | C | [C] 翻越期间相机俯仰滞后的比例。 |
| `BobGaitScale` | 1.0 | — | C | [C] **步态摇晃倍率**（v0.4 新增，这是唯一仍然生效的摇晃幅度旋钮）。 1.0 = 步态模型推导出的物理值；0.0 = 完全关掉走路摇晃；2.0 = 翻倍（对比/调试）。 运行时可通过调试菜单（`\` 键）实时拖动，滑条范围 0–2、步进 0.05。 它线性乘在 BuildOutput 的 bob 幅度上（垂直 / 侧向 / 点头 / 侧倾四个通道同乘）， 由 tests/GaitTest.cs 的 ㉕ 端到端实测确认（k=0.5 恰好得到 k=1 的一半）。 v0.4.2 的物理值（1.0×，站立身高 1.95 m，H = 1.95 m）实测： 冲刺 6.604 m/s 垂直峰到峰 12.4 mm、点头 0.25°、侧向 16.0 mm、相位 3.00 Hz； 收枪 7.595 m/s 垂直峰到峰 12.1 mm、点头 0.24°、侧向 17.7 mm、相位 3.00 Hz。 舒适档判据（v0.4.2 收紧后）：**垂直峰到峰 ≤ 1.5 cm、点头峰到峰 ≤ 0.30°**。 |
| `BobAmplitude` | 0.026 | m | C | [C] 走路摇晃的垂直幅度 (m)。**v0.4 起不再生效**（改由步态模型推导）。 ⚠️ **Apex 没有 `cl_bob` 类 cvar**（逆向负结论），走路摇晃来自第一人称动画， 因此这里**没有任何证据可依**，纯手感参数。 v0.3：0.035 → 0.026（配合下面几项一起收，需求是"镜头晃动过于剧烈"）。 |
| `BobLateralAmplitude` | 0.016 | m | C | [C] 走路摇晃的横向幅度 (m)。**v0.4 起不再生效**。v0.3：0.022 → 0.016。 |
| `BobSprintMultiplier` | 1.6 | — | C | 冲刺时摇晃幅度倍率。**v0.4 起不再生效**（腾空比已含在步态模型里）。 |
| `BobPitchDeg` | 0.24 | deg | C | [C] 摇晃的俯仰幅度 (deg)。**v0.4 起不再生效**。v0.3：0.35 → 0.24。 |
| `BobRollDeg` | 0.20 | deg | C | [C] 摇晃的滚转幅度 (deg)。**v0.4 起不再生效**。v0.3：0.30 → 0.20。 |
| `BobMinSpeed` | 0.6 | m/s | C | [C] 摇晃开始生效的最低速度 (m/s)。 |
| `BobVerticalPhaseDeg` | 0 | deg | C | [C] **竖直起伏的相位偏移 (deg)**（v0.4.3 新增，0 = ∞ 默认）。 波形是 sin(2φ + offset)，φ 为步幅相位。 · **0°（默认）** = 屏幕平面轨迹是横放的 **∞**（需求方要的形状， 物理图像是头部相对质心有约 1/4 周期滞后）； · **90°** = 退化成**抛物线**（y 是 x 的单值函数，两侧高、中间低， = 纯质心口径），没有中心交叉。留作对比用。 |
| `BobPitchPhaseDeg` | 0 | deg | C | [C] **点头相对竖直起伏的相位偏移 (deg)**（v0.4.3 新增，0 = 同相）。 波形是 sin(2φ + offset)，φ 为步幅相位。0° = 身体最低点时低头最多； 90° = 在升降速度最大时点头最多（"滞后感"）。默认 0 偏"扎实"， 想要头部跟不上的柔软感可以往 30~60 调。 |
| `BobRollPhaseDeg` | 0 | deg | C | [C] **侧倾相对左右摆动的相位偏移 (deg)**（v0.4.3 新增，0 = 同相）。 波形是 sin(φ + offset)。0° = 身体摆到最外侧时倾斜最多（重心侧压）， 90° = 摆到正中时倾斜最多（会明显"晕"）。默认 0。 |
| `SprintEyeForwardOffset` | 0.06 | m | C | [C] 冲刺时的相机前移量 (m)（对应 `sprintViewOffset`）。 |
| `CrouchEyeForwardOffset` | 0.03 | m | C | [C] 蹲下时的相机前移量 (m)（蹲下身体前倾）。 |
| `SlideEyeDrop` | 0.10 | m | C | [C] 滑铲时的相机下沉量 (m)（贴地感）。 |

## 10. 星球预设（`core/PlanetProfile.cs`）

| 预设 | Name | SurfaceGravity (m/s²) | AirDensity (kg/m³) | 说明 |
|---|---|---|---|---|
| `PlanetProfile.Earth` | Earth | 9.80665 | 1.225 | **当前默认** |
| `PlanetProfile.Moon` | Moon | 1.62 | 0 | 低重力：跳得高、落得慢 |
| `PlanetProfile.Mars` | Mars | 3.711 | 0.02 | 中间档 |
| `PlanetProfile.ApexDefault` | Apex-like | 20.7802 | 1.225 | v0.4 及以前的历史口径，留作对照 |

切星球用 `MovementConfig.ApplyPlanet(PlanetProfile)`：**只改 Gravity，不改任何设计目标**
（跳跃高度、速度、滑铲门槛都是 [Target]，切星球只变实际效果，不变设计值）。
运行时对应 `ApexPlayer.ApplyPlanetPreset(int)`（调试菜单里的星球下拉框）。

## 11. 派生量（禁止手调）

| 派生属性 | 公式 | 说明 |
|---|---|---|
| `SlideExitSpeed` | `CrouchWalkSpeed` | **滑铲结束速度** = 蹲走基础移速（保底）。 |
| `FixedDelta` | `1.0f / TickRate` |  |
| `JumpApex` | `JumpApexOnPlanet(Gravity)` |  |
| `JumpAirTime` | `2f * EffectiveJumpVelocity / Gravity` |  |
| `JumpAccelTicks` | `Math.Clamp((int)MathF.Round(JumpAccelTime / FixedDelta), 0, 60)` |  |
| `JumpAccelTimeActual` | `JumpAccelTicks * FixedDelta` |  |
| `EffectiveJumpVelocity` | `JumpAccelTicks <= 0 ? JumpVelocity : JumpRampExitVelocity` | 起跳实际使用的**离地速度**（含加速时间修正）。DoJump 用这个值。 |
| `AccelTimeToTopSpeed` | `GroundAccel > 1e-4f ? 1f / GroundAccel : float.PositiveInfinity` | 地面加速到顶速所需时间 (s) ≈ 1/。 |
| `TerminalDragK` | `Gravity / MathF.Max(1e-4f, MaxFallSpeed * MaxFallSpeed)` | 把  硬夹换算成**二次阻力系数** k (1/m)： |

## 12. 已删除 / 已废弃的参数

| 参数 | 曾经的用途 | 现状 |
|---|---|---|
| `GroundAccel/SprintAccel/CrouchAccel` 的旧值 10 | Source 谱系默认加速度 | **v0.5.3 改为 14**（见 §1） |
| `Friction`（粘性摩擦系数 4.0） | v0.4 及以前的粘性摩擦模型 | v0.5 换成库仑摩擦，已删除，见 §4 |
| `SlideMaxSpeed`（10.16） | Source 的 400 hu/s 滑铲上限 | v0.5 删除（需求：取消滑铲速度上限），数值留在注释里 |
| `SlideMinSpeed`（2.5） | 滑铲结束速度 | 改为派生属性 `SlideExitSpeed => CrouchWalkSpeed`（达标即转蹲走） |
| `SlideMinStartSpeedAir*`（水平 2.286 / 垂直 5.08） | 空中进滑铲的门槛 | v0.4.2 删除（滑铲成为纯地面状态），数值留在注释里 |
| `BobAngularMode`（bool） | ∞ 载体二选一开关 | v0.5 删除：只保留角度模式，见 §9.2 |

<!-- 本文由代码扫描 + 人工分节生成，最后核对：v0.5.3 -->
