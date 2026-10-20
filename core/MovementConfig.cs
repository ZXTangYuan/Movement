using System;

namespace ApexMovement.Core;

/// <summary>
/// 移动系统的全部可调参数（单一事实来源）。
///
/// ## 数值口径（v0.2）
///
/// | 类别 | 来源 | 说明 |
/// |---|---|---|
/// | **角色尺寸** | **需求方口径** | 站立 1.95 m / 蹲下 0.975 m / 眼位 = 头顶 −0.10 m。**优先级最高，不随 Apex 改动** |
/// | 速度 / 高度 / 角度 | **Apex 实机实测（wiki）** | 全部按 1 hu = 2.54 cm 换算，见 docs/wiki-reference.md |
/// | 加速度 / 摩擦 / 时长 | **引擎谱系（B）** | Source 2 / Titanfall 谱系默认值 |
/// | 相机 / 手感曲线 | **设计选择（C）** | 无可靠证据，全部可调 |
///
/// **证据等级标注**：
///   A = 二进制直接观测（字段/枚举存在）或 wiki 实测数值
///   B = 由引擎谱系推导，未直接读到该值
///   C = 设计选择 / 需实机手感校准
///
/// **单位约定**：米 / 秒（Godot 世界单位 = 1m）。注释里给出 Source 单位（hu）原始值。
/// </summary>
public sealed class MovementConfig
{
    // ═════════════════════════════ 单位换算 ═════════════════════════════

    /// <summary>[A] hammer unit → 米。1 hu = 2.54 cm（wiki 引擎篇 + 游戏文件）。</summary>
    public const float HuToMeter = 0.0254f;

    /// <summary>hu/s → m/s 的便捷换算。</summary>
    public static float Hu(float huPerSecond) => huPerSecond * HuToMeter;

    // ═════════════════════════════ 重力与下落 ═════════════════════════════

    /// <summary>
    /// [A] 重力加速度 (m/s²)。
    ///
    /// **不是拍的**：由 wiki 的两个独立实测值反解 ——
    /// 跳跃高度 h = 56 hu = 1.4224 m，滞空时间 t = 0.74 s。
    /// 由 <c>h = g·t²/8</c> 得 <c>g = 8h/t² = 8×1.4224/0.5476 = 20.7802</c>。
    ///
    /// 顺带交叉验证：Source 默认 sv_gravity = 800 u/s² × 0.0254 = 20.32 m/s²，两者仅差 2.3%，
    /// 说明 Apex 确实沿用了 Source 量级但略有调高。
    /// </summary>
    public float Gravity = 20.7802f;

    /// <summary>[B] 终端下落速度上限 (m/s)。Source sv_maxvelocity = 3500 u/s ≈ 88.9。</summary>
    public float MaxFallSpeed = 88.9f;

    // ═════════════════════════════ 地面速度 ═════════════════════════════

    /// <summary>[A] 行走速度 (m/s) = 173.5 hu/s。wiki Ground：「a walk peaks at a velocity of 173.5 hu/s」。</summary>
    public float WalkSpeed = 4.40690f;

    /// <summary>[A] 冲刺速度 (m/s) = 260 hu/s（持枪）。wiki Sprint：「it peaks at a velocity of 260 hu/s」。</summary>
    public float SprintSpeed = 6.60400f;

    /// <summary>[A] 蹲行速度 (m/s) = 80 hu/s（持枪）。wiki Crouch：「Crouchwalk peaks at 92hu/s」（收枪值）。</summary>
    public float CrouchWalkSpeed = 2.03200f;

    /// <summary>
    /// [A] 收枪加速倍率 = 1.15。wiki Holster：「Holster Speedboost is 15% i.e a factor of 1.15」。
    /// 收枪冲刺 = 299 hu/s = 7.5946 m/s（这是玩家实际感知的"Apex 冲刺速度"）。
    /// </summary>
    public float HolsterMultiplier = 1.15f;

    /// <summary>
    /// [B] 地面加速度。Source sv_accelerate 默认 10。
    ///
    /// wiki 明确：**蹲行 / 行走 / 冲刺的加速度完全相同，只有顶速不同**。
    /// 因此三个状态共用一个加速度值（拆成三个字段只是为了将来单独调）。
    /// </summary>
    public float GroundAccel = 10f;

    /// <summary>[B] 冲刺加速度（wiki：与行走相同，保留独立字段便于调参）。</summary>
    public float SprintAccel = 10f;

    /// <summary>[B] 蹲行加速度（wiki：与行走相同）。</summary>
    public float CrouchAccel = 10f;

    /// <summary>
    /// [B] 空中加速度系数。
    ///
    /// ⚠️ **这里做了一个必须说明的工程决定**：wiki 给出的游戏内变量是
    /// `Air acceleration = 500`、`Air speed = 60 (hu/s)`。
    /// 若直接套 Source 的 <c>accelspeed = accel × dt × wishSpeed</c>，
    /// 得到 500 × (1/60) × 1.524 = **12.7 m/s 每 tick** —— 荒谬（一 tick 加满）。
    /// 说明 Apex 内部对 `airAcceleration` 另做了一层单位缩放，而这个缩放系数**没有读到**。
    ///
    /// 因此本插件把 `airAcceleration` 归一化为本字段（默认取 Source 谱系默认 10，B 级），
    /// 而把**唯一有 A 级证据的量** `AirWishSpeed = 60 hu/s` 作为硬约束。
    /// 真正决定 air strafe 手感的是 `AirWishSpeed`（它限制了每 tick 最多能加多少投影速度）。
    /// </summary>
    public float AirAccel = 10f;

    /// <summary>
    /// [A] 空中 wishspeed (m/s) = 60 hu/s = 1.524。wiki Air strafe：「Air speed=60」。
    ///
    /// **这是 air strafe 的物理根因**：加速公式里有
    /// <c>addSpeed = wishSpeed − dot(velocity, wishDir)</c>，
    /// 而 wishSpeed 只有 1.524 m/s，所以只有当你的速度在 wishDir 上的投影小于 1.524 时才加速。
    /// 必须同步转视角让 wishDir 一直"贴"在速度矢量侧前方，才能持续吃到那点加速度。
    /// </summary>
    public float AirWishSpeed = 1.52400f;

    /// <summary>[B] 地面摩擦系数。Source sv_friction 默认 4。</summary>
    public float Friction = 4f;

    /// <summary>[A] 空中**没有**摩擦。wiki 引擎篇：「Apex does not feature any Air friction」。</summary>
    public float AirFriction = 0f;

    /// <summary>[A] 停止速度基准 (m/s) = 100 hu/s = 2.54。Source sv_stopspeed 同值。</summary>
    public float StopSpeed = 2.54000f;

    /// <summary>
    /// [C] 贴地吸附长度 (m)，等价 Godot 的 <c>floor_snap_length</c>。
    ///
    /// 两个作用：① 接地探测距离；② 命中后把胶囊**压到接触面**（留 1 mm 皮肤）。
    /// 只靠"向下速度"贴地时，探测距离远大于单 tick 下降量，角色会悬停在离地几厘米处落不下去。
    /// </summary>
    public float GroundSnapLength = 0.20f;

    /// <summary>[A] 可行走坡面法线点积阈值 = 0.7（= arccos(0.7) = 45.572°）。</summary>
    public float WalkableNormalDot = 0.7f;

    // ═════════════════════════════ 跳跃 ═════════════════════════════

    /// <summary>[A] 跳跃高度 (m) = 56 hu = 1.4224。wiki Jump。</summary>
    public float JumpHeight = 1.42240f;

    /// <summary>[C] 跳跃缓冲窗口 (s)：落地前按跳，落地瞬间自动触发。</summary>
    public float JumpBufferTime = 0.12f;

    /// <summary>[A] 土狼时间 (s) = 0.2。wiki Coyote time：「Coyote time lasts for 0.2sec」。</summary>
    public float CoyoteTime = 0.20f;

    // ── 跳跃疲劳（wiki 直接给出的游戏内变量） ──

    /// <summary>[A] 疲劳最短窗口 (s) = `antiMultiJumpTimeMin` = 0.15000006。</summary>
    public float JumpFatigueMinTime = 0.15f;

    /// <summary>[A] 疲劳衰减完成时间 (s) = `antiMultiJumpTimeMax` = 0.75。</summary>
    public float JumpFatigueMaxTime = 0.75f;

    /// <summary>[A] 疲劳最低跳跃高度比例 = `antiMultiJumpHeightFrac` = 0.30。</summary>
    public float JumpFatigueHeightFrac = 0.30f;

    /// <summary>[A] Bunnyhop 判定窗口 (s)：落地后此时长内离地算 bhop（游戏内 `skip_time` = 1s）。</summary>
    public float BunnyHopWindow = 1.0f;

    /// <summary>[A] Bunnyhop 减速上限 (m/s) = 100 hu/s = 2.54（wiki：超过冲刺速度时每次 bhop 扣这么多）。</summary>
    public float BunnyHopSpeedPenalty = 2.54000f;

    /// <summary>[A] 跳跃 grace 的 cutoff 速度 (m/s)：超过它就不再施加 bhop 减速（`jump_grace_cutoff_speed` 的语义）。</summary>
    public float JumpGraceCutoffSpeed = 12.0f;

    /// <summary>[C] 落地后多久内"按住蹲再跳"算作 slidehop (s)。</summary>
    public float SlideHopWindowAfterLanding = 0.20f;

    // ═════════════════════════════ 体型（需求方口径，优先） ═════════════════════════════

    /// <summary>**需求方口径**：站立胶囊高度 1.95 m。</summary>
    public float StandHeight = 1.95f;

    /// <summary>**需求方口径**：蹲下胶囊高度 = 站立一半 = 0.975 m。</summary>
    public float CrouchHeight = 0.975f;

    /// <summary>**需求方口径**：眼位 = 胶囊顶端 − 0.10 m（站立时 1.85 m，蹲下时 0.875 m）。</summary>
    public float EyeHeightOffset = 0.10f;

    /// <summary>[A] 胶囊半径 (m) = 16 hu = 0.4064。wiki 引擎篇。</summary>
    public float CapsuleRadius = 0.40640f;

    /// <summary>[C] 蹲下/站起过渡时间 (s)。</summary>
    public float CrouchTransitionTime = 0.12f;

    /// <summary>
    /// [A] 空中蹲下时胶囊**向中间收缩**（脚底上升）；地面上蹲下**向底部收缩**（脚底不动）。
    /// wiki 引擎篇：「Crouching on the ground shrinks it towards the bottom... mid air ... towards the middle」。
    /// 这是 slidehop 与 bhop 手感不同的根因。关掉它 = 一律脚底锚定（旧版行为）。
    /// </summary>
    public bool AirCrouchShrinksToMiddle = true;

    // ═════════════════════════════ 滑铲 ═════════════════════════════

    /// <summary>[A] 地面滑铲最低水平速度 (m/s) = 200 hu/s = 5.08（`slideRequiredStartSpeed`）。</summary>
    public float SlideMinStartSpeed = 5.08000f;

    /// <summary>[A] 空中滑铲最低水平速度 (m/s) = 90 hu/s = 2.286（`slideRequiredStartSpeedAir` 的水平部分）。</summary>
    public float SlideMinStartSpeedAirHorizontal = 2.28600f;

    /// <summary>[A] 空中滑铲最低向下垂直速度 (m/s) = 200 hu/s = 5.08（垂直部分，**必须同时满足**）。</summary>
    public float SlideMinStartSpeedAirVertical = 5.08000f;

    /// <summary>[A] Slideboost 增量 (m/s) = 150 hu/s = 3.81。wiki Slide：「Slideboost is: 150hu/s * (...)」。</summary>
    public float SlideBoostSpeed = 3.81000f;

    /// <summary>[A] 滑铲速度上限 (m/s) = 400 hu/s = 10.16。wiki Slide：「capped at: 400hu/s * (...)」。</summary>
    public float SlideMaxSpeed = 10.16000f;

    /// <summary>[A] Slideboost 冷却 (s) = 2.0。wiki Slide：「the Slideboost has a 2sec cooldown」。</summary>
    public float SlideBoostCooldown = 2.0f;

    /// <summary>[C] 滑铲摩擦（远低于地面摩擦 —— 这是能滑远的原因）。</summary>
    public float SlideFriction = 0.9f;

    /// <summary>[C] 滑铲自动站起的最低速度 (m/s)。低于它自动结束滑铲。</summary>
    public float SlideMinSpeed = 2.5f;

    /// <summary>[C] 滑铲最长持续时间 (s)。wiki 说滑铲本身**没有**计时器，这里只是防卡死兜底。</summary>
    public float SlideMaxDuration = 6.0f;

    /// <summary>[C] 滑铲期间的转向能力（相对正常加速的比例）。wiki：短暂方向输入几乎被忽略。</summary>
    public float SlideSteerFactor = 0.15f;

    /// <summary>[C] 滑铲期间反向输入的额外摩擦倍率。wiki：反向输入减速"非常剧烈"。</summary>
    public float SlideReverseInputPenalty = 3.0f;

    // ── 滑铲跳（slide jump / slidehop） ──

    /// <summary>
    /// [A] Slidejump 速度阈值 (m/s) = 350 hu/s = 8.89。
    /// wiki：跳跃时**只有**速度低于此值 **或** 进入滑铲已超过 <see cref="SlideJumpTimeWindow"/> 才叠加跳跃初速。
    /// </summary>
    public float SlideJumpSpeedThreshold = 8.89000f;

    /// <summary>[A] Slidejump 时间窗 (s) = 0.24。wiki：0.24sec after initiating the Slide。</summary>
    public float SlideJumpTimeWindow = 0.24f;

    /// <summary>[C] 滑铲跳速度保留系数（跳跃时水平速度乘以此值）。</summary>
    public float SlideJumpSpeedRetention = 1.0f;

    // ═════════════════════════════ 翻越 / 攀爬 ═════════════════════════════

    /// <summary>[A] 可"走过"的台阶高度 (m) = 30 hu = 0.762。wiki Ground：overwalkable。</summary>
    public float OverwalkableHeight = 0.76200f;

    /// <summary>[A] Auto-mantle 高度上限 (m) = 80 hu = 2.032。wiki Auto mantle。</summary>
    public float AutoMantleMaxHeight = 2.03200f;

    /// <summary>[A] 攀爬总高度上限 (m) = 273 hu = 6.9342。wiki 引擎篇 + Climb（climb space cutoff）。</summary>
    public float MantleMaxHeight = 6.93420f;

    /// <summary>[A] 攀爬附着偏移 (m) = 100 hu = 2.54（Attach offset，"个人体力上限"）。</summary>
    public float ClimbAttachOffset = 2.54000f;

    /// <summary>[A] 攀爬结束额外上抛 (m) = 28 hu = 0.7112（End boost）。</summary>
    public float ClimbEndBoost = 0.71120f;

    /// <summary>[A] 判定"向上爬"的垂直速度阈值 (m/s) = 10 hu/s = 0.254。</summary>
    public float ClimbUpwardSpeedThreshold = 0.25400f;

    /// <summary>[A] 非向上爬的脱离计时 (s) = 1.0。wiki Climb。</summary>
    public float ClimbNonUpwardTimer = 1.0f;

    /// <summary>[C] 向上攀爬速度 (m/s)。</summary>
    public float ClimbSpeed = 2.0f;

    /// <summary>[A] 允许触发翻越的最大视角偏角 (deg) = 50。wiki Mantle grab：「within 50° to each side」。</summary>
    public float MantleMaxViewAngle = 50f;

    /// <summary>[A] Mantle grab 时长 (s) = 0.45。wiki Mantle。</summary>
    public float MantleGrabTime = 0.45f;

    /// <summary>[A] Mantle pull 时长 (s) = 0.45。wiki Mantle。</summary>
    public float MantlePullTime = 0.45f;

    /// <summary>[C] grab 占整段翻越时长的比例（两段式轨迹的切分点）。</summary>
    public float MantleGrabFraction = 0.45f;

    /// <summary>[C] <see cref="MantleClass.Below"/> 档总时长 (s)（对应 `mantleDurationBelow`）。</summary>
    public float MantleDurationBelow = 0.55f;

    /// <summary>[C] <see cref="MantleClass.Level"/> 档总时长 (s)（对应 `mantleDurationLevel`）。</summary>
    public float MantleDurationLevel = 0.75f;

    /// <summary>[C] <see cref="MantleClass.Above"/> 档总时长 (s)（对应 `mantleDurationAbove`）。</summary>
    public float MantleDurationAbove = 0.90f;

    /// <summary>[C] <see cref="MantleClass.High"/> 档总时长 (s)（对应 `mantleDurationHigh`）。</summary>
    public float MantleDurationHigh = 1.20f;

    /// <summary>[C] 分档阈值①：台面高度 ≤ 此值 → <see cref="MantleClass.Below"/>。</summary>
    public float MantleLevelHeight = 0.975f;

    /// <summary>[C] 分档阈值②：台面高度 ≤ 此值 → <see cref="MantleClass.Level"/>。</summary>
    public float MantleAboveHeight = 1.95f;

    /// <summary>[C] 分档阈值③：台面高度 ≤ 此值 → <see cref="MantleClass.Above"/>，否则 <see cref="MantleClass.High"/>。</summary>
    public float MantleHighHeight = 3.45f;

    /// <summary>[C] 翻越结束时的前推距离 (m)。</summary>
    public float MantleForwardOffset = 0.55f;

    /// <summary>[C] 触发翻越/攀爬的最大前方探测距离 (m)。</summary>
    public float TraversalReach = 0.9f;

    /// <summary>[C] 翻越/攀爬冷却 (s)。</summary>
    public float TraversalCooldown = 0.2f;

    /// <summary>[C] 松手后是否进入挂边（Mantle hang）而不是自动完成。</summary>
    public bool EnableMantleHang = true;

    // ── 窗口翻越（TRAVERSAL_WINDOW） ──

    /// <summary>[C] 是否启用窗口翻越。</summary>
    public bool WindowTraversalEnabled = true;

    /// <summary>[C] 窗口翻越时长 (s)（对应 `traversal_window_duration`）。</summary>
    public float WindowDuration = 0.70f;

    /// <summary>[C] 穿过窗口后的前推距离 (m)（对应 `traversal_window_forward_offset`）。</summary>
    public float WindowForwardOffset = 1.20f;

    /// <summary>[C] 穿过窗口时的侧向偏移 (m)（对应 `traversal_window_sideways_offset`）。</summary>
    public float WindowSidewaysOffset = 0.0f;

    /// <summary>[C] 手部垂直偏移 (m)（对应 `traversal_window_hand_vertical_offset`）。</summary>
    public float WindowHandVerticalOffset = 0.0f;

    /// <summary>[C] 窗口翻越的收尾角度 (deg)（对应 `traversal_window_finish_angle`）。</summary>
    public float WindowFinishAngle = 0.0f;

    /// <summary>[C] 窗口开口的最小宽度 (m)（对应 `traversal_hand_required_width`）。</summary>
    public float WindowRequiredWidth = 1.0f;

    // ═════════════════════════════ 落地与 Fallstun ═════════════════════════════

    /// <summary>[A] 落地分档：低 (m)。对应 `landDistLow` 的量级。</summary>
    public float LandDistLow = 2.0f;

    /// <summary>[A] 落地分档：中 (m)。对应 `landDistMedium`。</summary>
    public float LandDistMedium = 5.0f;

    /// <summary>[A] 落地分档：高 (m)。对应 `landDistHigh`。</summary>
    public float LandDistHigh = 10.0f;

    /// <summary>[A] 硬着陆阈值 (m)。对应 `hardFallDist`。</summary>
    public float HardFallDist = 20.32f;

    /// <summary>[A] Fallstun 起始下落高度 (m) = 300 hu = 7.62。wiki Fallstun。</summary>
    public float FallStunMinHeight = 7.62000f;

    /// <summary>[A] Fallstun 满值下落高度 (m) = 800 hu = 20.32。wiki Fallstun。</summary>
    public float FallStunMaxHeight = 20.3200f;

    /// <summary>[A] Fallstun 最长持续时间 (s) = 1.0。wiki Fallstun。</summary>
    public float FallStunMaxDuration = 1.0f;

    /// <summary>[C] Fallstun 期间的顶速比例。wiki：从 0 到"完全减速"。</summary>
    public float FallStunSpeedScale = 0.35f;

    // ═════════════════════════════ 固定 tick ═════════════════════════════

    /// <summary>[C] 固定 tick 频率 (Hz)。60 与 Godot 默认物理频率一致。</summary>
    public int TickRate = 60;

    /// <summary>[C] 单帧最多追赶的 tick 数（防死亡螺旋）。</summary>
    public int MaxCatchUpTicks = 4;

    /// <summary>[C] 是否在 <see cref="ApexMovementCore.Step"/> 内自动尝试翻越/攀爬。</summary>
    public bool AutoTraverse = true;

    /// <summary>固定 tick 时长 (s)。</summary>
    public float FixedDelta => 1.0f / TickRate;

    public MovementConfig Clone() => (MovementConfig)MemberwiseClone();

    /// <summary>由跳跃高度反解初速：v = sqrt(2·g·h)。</summary>
    public float JumpVelocity => MathF.Sqrt(2f * Gravity * JumpHeight);

    /// <summary>理论滞空时间 (s) = 2v/g（用于自检交叉验证 wiki 的 0.74s）。</summary>
    public float JumpAirTime => 2f * JumpVelocity / Gravity;

    /// <summary>根据台面相对脚底的高度分类翻越档位。</summary>
    public MantleClass ClassifyMantle(float heightAboveFeet)
    {
        if (heightAboveFeet <= MantleLevelHeight) return MantleClass.Below;
        if (heightAboveFeet <= MantleAboveHeight) return MantleClass.Level;
        if (heightAboveFeet <= MantleHighHeight) return MantleClass.Above;
        return MantleClass.High;
    }

    /// <summary>取某档翻越的总时长 (s)。</summary>
    public float MantleDurationFor(MantleClass cls) => cls switch
    {
        MantleClass.Below => MantleDurationBelow,
        MantleClass.Level => MantleDurationLevel,
        MantleClass.Above => MantleDurationAbove,
        _ => MantleDurationHigh,
    };

    /// <summary>按下落高度给落地分档。</summary>
    public LandingTier ClassifyLanding(float fallDistance)
    {
        if (fallDistance < LandDistLow) return LandingTier.Low;
        if (fallDistance < LandDistMedium) return LandingTier.Medium;
        if (fallDistance < LandDistHigh) return LandingTier.High;
        return LandingTier.Hard;
    }

    /// <summary>
    /// Fallstun 时长 (s)。wiki：0 s @ 300 hu → 1 s @ 800 hu，且"非线性（可能是二次）"。
    /// 这里用 <c>t = frac² × max</c>。
    /// </summary>
    public float FallStunDuration(float fallDistance)
    {
        if (fallDistance <= FallStunMinHeight) return 0f;
        float span = MathF.Max(1e-4f, FallStunMaxHeight - FallStunMinHeight);
        float frac = Math.Min(1f, (fallDistance - FallStunMinHeight) / span);
        return frac * frac * FallStunMaxDuration;
    }

    // ═════════════════════════════ 预设 ═════════════════════════════

    /// <summary>
    /// **默认预设**：角色尺寸用需求方口径（1.95 / 0.975 / 0.10），其余用 Apex 实机实测值。
    /// </summary>
    public static MovementConfig Apex() => new();

    /// <summary>
    /// **Legacy 预设**：v0.1 的速度口径（行走 5.4、冲刺 ×1.37 = 7.4、攀爬 6.5、跳跃 1.15）。
    /// 保留它是因为项目里已有的关卡/手感基准是按这套数值调的，一行即可切回。
    /// </summary>
    public static MovementConfig Legacy()
    {
        var c = new MovementConfig
        {
            WalkSpeed = 5.4f,
            SprintSpeed = 5.4f * 1.37f,
            CrouchWalkSpeed = 5.4f * 0.5f,
            HolsterMultiplier = 1.0f,
            MantleMaxHeight = 6.5f,
            JumpHeight = 1.15f,
            CoyoteTime = 0.10f,
            SlideMinStartSpeed = 5.0f,
            SlideBoostSpeed = 3.0f,
            SlideMaxSpeed = 14.0f,
            SlideBoostCooldown = 0.35f,
            SlideJumpSpeedThreshold = 14.0f,
            SlideJumpTimeWindow = 0.55f,
            MantleLevelHeight = 1.1f,
            MantleAboveHeight = 2.2f,
            MantleHighHeight = 4.0f,
        };
        return c;
    }

    // ═════════════════════════════ 校验 ═════════════════════════════

    public void Validate()
    {
        if (TickRate <= 0) throw new ArgumentOutOfRangeException(nameof(TickRate));
        if (FixedDelta <= 0f) throw new ArgumentOutOfRangeException(nameof(TickRate));
        if (CapsuleRadius <= 0f) throw new ArgumentOutOfRangeException(nameof(CapsuleRadius));
        if (StandHeight < CrouchHeight)
            throw new ArgumentException("StandHeight 必须 >= CrouchHeight");
        if (CrouchHeight < 0f)
            throw new ArgumentOutOfRangeException(nameof(CrouchHeight));
        if (StandHeight <= CapsuleRadius * 2f)
            throw new ArgumentException("StandHeight 必须大于胶囊直径 2×CapsuleRadius（否则半高为负）");
        if (CrouchHeight <= CapsuleRadius * 2f)
            throw new ArgumentException("CrouchHeight 必须大于胶囊直径 2×CapsuleRadius（否则蹲下时半高为负）");
        if (GroundSnapLength <= 0f)
            throw new ArgumentOutOfRangeException(nameof(GroundSnapLength));
        if (EyeHeightOffset < 0f || EyeHeightOffset >= CrouchHeight)
            throw new ArgumentOutOfRangeException(
                nameof(EyeHeightOffset), "EyeHeightOffset 必须在 [0, CrouchHeight) 内");
        if (MantleMaxHeight < MantleAboveHeight)
            throw new ArgumentException("MantleMaxHeight 必须 >= MantleAboveHeight");
        if (MantleLevelHeight > MantleAboveHeight || MantleAboveHeight > MantleHighHeight)
            throw new ArgumentException("翻越分档阈值必须单调递增：MantleLevelHeight <= MantleAboveHeight <= MantleHighHeight");
        if (JumpFatigueMinTime > JumpFatigueMaxTime)
            throw new ArgumentException("JumpFatigueMinTime 必须 <= JumpFatigueMaxTime");
        if (JumpFatigueHeightFrac <= 0f || JumpFatigueHeightFrac > 1f)
            throw new ArgumentOutOfRangeException(nameof(JumpFatigueHeightFrac));
        if (FallStunMaxHeight <= FallStunMinHeight)
            throw new ArgumentException("FallStunMaxHeight 必须 > FallStunMinHeight");
        if (WalkableNormalDot <= 0f || WalkableNormalDot >= 1f)
            throw new ArgumentOutOfRangeException(nameof(WalkableNormalDot));
        if (MantleGrabFraction <= 0f || MantleGrabFraction >= 1f)
            throw new ArgumentOutOfRangeException(nameof(MantleGrabFraction));
    }
}
