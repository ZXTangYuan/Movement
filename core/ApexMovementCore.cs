using System;

namespace ApexMovement.Core;

/// <summary>
/// 自研移动求解器 v0.4.2 —— **固定 tick 确定性**实现。
///
/// ## 与 Apex 原版的关系
/// Apex（Source 2 / Titanfall 谱系）按**渲染帧**积分：两次画面帧之间的输入动量会叠加，
/// 因此帧率不同 ⇒ 加速度曲线不同 ⇒ 轨迹不同。
/// 本实现改为**固定 tick 积分**：所有状态推进只发生在 <see cref="Step"/> 内，
/// 且 dt 恒为 <see cref="MovementConfig.FixedDelta"/>。
/// 结论：同样的输入序列，在 30/60/144/240fps 下产生**同一条轨迹**。
///
/// ## v0.2 相比 v0.1 的机制变化
///
/// | 机制 | v0.1 | v0.2 |
/// |---|---|---|
/// | 空中摩擦 | 有 | **无**（wiki：Apex has no air friction） |
/// | 空中加速 | 单一 AirAccel | **AirWishSpeed = 60hu/s 硬约束** + AirAccel（air strafe 根因） |
/// | 滑铲触发 | Shift+Ctrl + 速度 | **地面/空中两套阈值**（200hu/s vs 90+200hu/s） |
/// | 滑铲加速 | ×1.25 倍率 | **Slideboost = +150hu/s，上限 400hu/s，2s 冷却** |
/// | 滑铲跳 | 时间窗内保留速度 | **350hu/s 速度阈值 或 0.24s 时间窗**（Apex 真实规则） |
/// | 翻越分类 | Vault / Mantle 两态 | **Below / Level / Above / High 四档**（对应二进制 4 个时长字段） |
/// | 翻越时序 | 单段 smoothstep | **Grab 0.45s → Pull 0.45s 两段**（+ 可选挂边 Hang） |
/// | 窗口翻越 | 无 | **有**（`traversal_window_*` 六参数） |
/// | 贴墙爬 | 无 | **Climb 状态**（273hu 上限 / 100hu attach offset / 28hu end boost） |
/// | 跳跃疲劳 | 无 | **有**（antiMultiJumpTime 0.15→0.75，高度 30%） |
/// | Bunnyhop 减速 | 无 | **有**（100hu/s，带 cutoff） |
/// | Fallstun | 无 | **有**（300hu→800hu 二次曲线） |
/// | 蹲下收缩方向 | 一律脚底锚定 | **地面向下缩 / 空中向中间缩**（slidehop vs bhop 差异根因） |
/// | 落地分档 | 无 | **Low/Medium/High/Hard**（供相机晃动） |
///
/// ## v0.3 相比 v0.2 的机制变化
///
/// | 机制 | v0.2 | v0.3 |
/// |---|---|---|
/// | 攀爬触发 | 空中 + `MoveZ > 0.3`（贴墙就吸） | **四条件**：空中 + 面向墙（≤45.57°）+ 朝墙速度 ≥0.5 + 不重复附着 |
/// | 攀爬上升 | 无条件爬到顶 | **必须按 W**（2 m/s）；松手 0.22 s 取消 |
/// | 翻越触发 | 无输入门槛 | `MoveZ > 0.3`（wiki：mantle grab 需要指向墙面的输入） |
/// | 打滑态 | 无 | **`Slip`**：环境体积把角色沿地面方向推到 `SlipSpeed`；空中方向限制进 35° 圆锥 |
/// | 扒墙态 | 无（`Hang` 只是攀爬到顶的过渡） | **`WallHang`**：贴墙 + 按蹲才挂（非强制），有握力 / 爬升 / 横移 / 下滑 / 蹬墙跳 |
/// | tick 基准 | 跟随引擎物理频率 | **固定 60/s**（`PinnedTickRate`），由 `ApexTickClock` 折算 |
/// | 昂贵探针 | 每 tick | `TraversalProbeInterval` / `SlipProbeInterval` 可降到 30 Hz |
///
/// ## v0.4 相比 v0.3 的机制变化（自研手感，不再对标 Apex）
///
/// | 机制 | v0.3 | v0.4 |
/// |---|---|---|
/// | 镜头抖动 | 手拍常数（步频 `1.2 + 速度×0.55`，冲刺 4.83 Hz） | **步态模型推导**（身高→腿长→偏好步速→步频/步幅→腾空比→起伏与点头），见 <see cref="GaitModel"/> |
/// | 滑铲镜头 | 侧倾 5° + FOV +6° + 下沉 10cm | **完全等同下蹲**（三项全删） |
/// | 攀爬触发 | 空中 + 面向墙 + 朝墙速度 | **W 贴墙 + Space 跳起**；地面按 W 只撞停 |
/// | 扒墙 | 贴墙 + 按蹲才挂（非强制） | **默认化**：空中下落贴墙自动挂；上升不打断；松 W 转扒墙；`S` 直接垂直下落 |
/// | 障碍分级 | 单一 `OverwalkableHeight=0.762` | **三档**：≤0.25m 台阶迈过 / ≤1.0m 小障碍 W+Space 翻越 / >1.0m 墙撞停 |
/// | 自动翻越探测 | 地面每 2 tick 探一次 | **已移除**（翻越/窗口改由 W+Space 显式触发） |
/// | 调试菜单 | 无 | **`\` 键**：FOV / 灵敏度 / 摇晃倍率 / 冲击倍率 / 开关 |
///
/// ## v0.4.1 相比 v0.4 的机制变化
///
/// | 机制 | v0.4 | v0.4.1 |
/// |---|---|---|
/// | 滑铲减速 | 与坡度无关的指数衰减（`SlideFriction=0.9`）⇒ 下坡反而更慢 | **真实物理** `a = g·sinθ − μ·g·cosθ`（<see cref="StepSlide"/>）：平地匀减速、下坡越陡越快、`tanθ=μ` 是临界角 |
/// | 摩擦系数 | 单一常数 | **按地面材质取**（<see cref="IFrictionWorld"/>，默认 `SlideFrictionCoefficient=0.45`），Godot 侧读碰撞体 meta `friction` |
/// | 碰撞法线兜底 | `GetRestInfo` 打空时退回 `-delta` 伪法线 ⇒ 把竖直速度一起投掉、角色悬空卡死 | **原地放大胶囊再查**，仍失败才退回"竖直墙"启发式（只挡水平分量） |
/// | 贴墙爬升 | 一次性冲到 `ClimbAttachOffset` | **分段爬升**：到上限就脱手，需要重新抓墙继续爬 |
///
/// ## v0.4.2 相比 v0.4.1 的机制变化
///
/// | 机制 | v0.4.1 | v0.4.2 |
/// |---|---|---|
/// | 空中滑铲 | `StepAir` 里有个入口：蹲 + 水平 ≥2.286 + 下坠 ≥5.08 ⇒ 空中 `Slide` | **整段删除**。`Slide` 自 v0.4.2 起是**纯地面状态** |
/// | 滑区转滑铲 | `StepSlip` 写 `BeginSlide(input, fromAir: !_onGround)` ⇒ 空中经过滑区按蹲也能起滑铲 | 只在 `_onGround` 时转；`BeginSlide` 的 `fromAir` 参数**整体删除**，让"空中起滑铲"在类型层面不成立 |
/// | 滑铲入口状态门 | `TryAttachWallGrab` 允许 `Air` **或** `Slide` | 收窄为只允许 `Air` |
/// | 落地衔接滑铲 | 落地 tick 只写 `Ground`，下一 tick `StepGround` **先吃一 tick 地面摩擦**（6.6 m/s 掉 ≈0.44 m/s）才起滑 ⇒ 滑铲跳链第 4 跳跌破 `SlideMinStartSpeed` | `Step()` 的 9a 段：本 tick 刚落地 + 蹲着 + 速度够 ⇒ **当场 `BeginSlide`**，链条可无限持续 |
/// | 抓墙方向 | 只有 ④b 速度门：水平速度沿墙面法线背离（< −0.05）才拒绝 | 补 ②b **意图门**：`MoveZ < -0.3`（按 S）直接拒绝 —— 覆盖"从静止按 S、速度还没建立背离分量"那段窗口 |
///
/// ## 参考的引擎公式（Source 谱系，本实现沿用）
/// - 加速：<c>accelSpeed = accel × dt × wishSpeed</c>，并夹到 <c>addSpeed</c>
/// - 摩擦：<c>drop = max(speed, stopSpeed) × friction × dt</c>
/// - 跳跃：<c>v = sqrt(2·g·h)</c>
///
/// ## 与 Godot 的边界
/// 本类**不引用任何 Godot 类型**（除 F3 的转换辅助），碰撞通过
/// <see cref="IMovementWorld"/> 注入。这样可以在无引擎环境下做确定性单元测试。
/// </summary>
public sealed class ApexMovementCore
{
    private readonly MovementConfig _cfg;
    private IMovementWorld? _world;

    // ── 位姿 ──
    private F3 _pos;
    private F3 _vel;
    private F3 _up = F3.Up;

    // ── 状态机 ──
    private MovementState _state = MovementState.Air;
    private float _yaw;
    private float _pitch;

    // ── 蹲 ──
    private float _crouchAmount;          // 0 = 站立, 1 = 全蹲
    private float _currentHeight;

    // ── 滑铲 ──
    private float _slideTime;
    private float _slideBoostCooldown;
    private float _slideEntrySpeed;
    private bool _slideBoostGranted;

    // ── 翻越 / 攀爬（traversal） ──
    private float _traversalTime;
    private float _traversalDuration;
    private F3 _traversalStart;
    private F3 _traversalMid;
    private F3 _traversalEnd;
    private float _traversalCooldown;
    private MantleClass _mantleClass;
    private MantlePhase _mantlePhase;
    private bool _isWindowTraversal;

    // ── 贴墙爬（climb） ──
    private float _climbTime;
    private float _climbAttachHeight;
    private float _climbBaseY;
    private F3 _climbWallNormal = F3.Zero;

    // ── v0.3：非强制攀爬 / 扒墙过渡 ──
    /// <summary>松 W 后已过的宽限时间 (s)；按着 W 时每 tick 归零。</summary>
    private float _climbNoInputTimer;
    /// <summary>是否处于"即将扒墙，正在补爬最后一小段"的阶段。</summary>
    private bool _climbApproaching;
    /// <summary>补爬阶段还剩多少距离 (m)。</summary>
    private float _climbApproachRemaining;
    /// <summary>补爬结束后要挂上去的台面上表面位置（Hang 的目标点来源）。</summary>
    private F3 _climbHangTop;
    /// <summary>上次攀爬的 attach point 高度（wiki 重附着规则用）。</summary>
    private float _lastClimbAttachY;
    /// <summary>上次攀爬的墙面法线（重附着豁免角度判定用）。</summary>
    private F3 _lastClimbWallNormal = F3.Zero;
    /// <summary>是否已经有过一次攀爬（决定重附着规则要不要生效）。</summary>
    private bool _hasClimbed;

    /// <summary>
    /// 最后一次探测到的墙面命中点（世界坐标）。
    ///
    /// 用途：爬到墙顶时**前向射线已经打空**，墙面平面信息随之丢失；而"找台面"
    /// 的向下扫描需要一个水平锚点（墙面所在的 X/Z），所以在这里把最后一次命中点记住。
    /// </summary>
    private F3 _climbLastWallHit;

    /// <summary>攀爬探针实际执行次数（自检用）。</summary>
    private int _climbProbeCount;

    // ── 打滑（Slip） ──
    /// <summary>当前所在滑区的推动参数（缓存，供抽样间隙复用，避免状态抖动）。</summary>
    private SlipSample _slipSample;
    /// <summary>缓存的滑区采样是否有效（上一次探针的结果）。</summary>
    private bool _slipSampleValid;
    /// <summary>离开滑区后的空中方向限制剩余时间 (s)（<c>m_slipAirRestrictTime</c>）。</summary>
    private float _slipAirRestrictTimer;
    /// <summary>空中方向限制的基准方向（<c>m_slipAirRestrictDirection</c>）。</summary>
    private F3 _slipAirRestrictDir = F3.Zero;
    /// <summary>滑区采样探针实际执行次数（自检用）。</summary>
    private int _slipProbeCount;

    // ── 扒墙 / 悬垂（WallHang） ──
    private float _wallHangTime;
    private float _wallHangSlip;
    /// <summary>
    /// 握力 0..1（v0.4 新增）：1 = 刚抓住，0 = 脱手。
    ///
    /// 这是「扒墙默认化」之后唯一的掉落下限来源 —— 需求要的是
    /// 「持续一小段时间没有按键输入，就会掉落」：
    ///   · 完全无输入 → 按 <see cref="MovementConfig.WallHangIdleFallTime"/> 的速度流失
    ///   · 只按 A/D 横移 → 按 <see cref="MovementConfig.WallHangMaxTime"/> 的速度流失
    ///   · 按 W 上爬 → 回补（`WallHangClimbRestoresGrip`）
    /// </summary>
    private float _wallHangGrip = 1f;
    private float _wallHangCooldown;
    private F3 _wallHangNormal = F3.Zero;
    private F3 _wallHangLastHit;
    /// <summary>
    /// 脱手后"必须先离开墙面一次才能再挂"的门闩。
    /// 没有它，按住蹲的玩家会在同一位置反复「抓住 → 滑落/超时脱手 → 再抓住」。
    /// </summary>
    private bool _wallGrabNeedW;

    /// <summary>
    /// 本次腾空是**跳**出来的（而不是走下台沿/被推落）。
    ///
    /// 用途（用户 m01300 BUG 2）：抓墙的"离地间隙"门限只对**非跳跃**腾空生效。
    /// 需求原话是「我希望改成只要跳了就攀爬」—— 跳了就无条件允许抓墙，
    /// 不管这一跳有多低（跳跃疲劳只有 0.23 m、超重力星球可能只有 0.1 m）。
    /// 走下台沿/被推落那种"没有起跳动作"的腾空仍然保留 0.30 m 间隙门，
    /// 否则贴着墙走下小台阶会被凭空吸到墙上。
    /// </summary>
    private bool _leftGroundByJump;
    /// <summary>攀爬到顶探针实际执行次数（自检用：验证 30 Hz 抽样）。</summary>
    private int _climbWallProbeCount;

    // ── 跳跃辅助 ──
    private bool _justJumped;
    private bool _jumpBuffered;
    private float _jumpBufferTimer;
    private float _coyoteTimer;
    /// <summary>起跳"有限加速段"剩余的 tick 数（0 = 当前没有加速段）。</summary>
    private int _jumpRampTicksLeft;
    /// <summary>起跳加速段的推力加速度 (m/s²)，已含抵消重力那一份。</summary>
    private float _jumpRampThrust;
    private bool _onGround;
    private bool _sprintIntent;                 // 本 tick 的冲刺**意图**（不是速度）
    private F3 _groundNormal = F3.Up;

    // ── 跳跃疲劳 / bhop ──
    private bool _jumpFatigueState;
    private float _timeSinceLanded = 999f;

    // ── Fallstun ──
    private float _fallStunTimer;
    private float _fallStunTotal;

    // ── 下落追踪（落地分档 / Fallstun） ──
    private float _airPeakHeight;
    private bool _wasAirborne;

    // ── 本 tick 的落地事件（供相机） ──
    private LandingTier _landingTier;
    private float _landingFallDistance;
    private float _landingImpactSpeed;

    // ── 步频相位（帧率无关的走路摇晃） ──
    private float _bobPhase;

    // ── v0.4：步态推导（身高 → 步幅 → 步频） ──
    /// <summary>
    /// 本 tick 的步态求值结果。<see cref="GaitModel"/> 按「身高 + 水平速度 +
    /// 冲刺参考速度 + 蹲伏量」推出步频/步幅/腾空比与各方向抖动半幅，
    /// 相机只负责把它变成眼位偏移与角度 —— **不再自己拍频率与幅度**。
    /// </summary>
    private GaitSample _gait;

    // ── v0.4：台阶倾斜（≤0.25m 台阶"只改方向不改大小"） ──
    /// <summary>本次台阶过渡的倾角 (rad)：tan(θ) = b / a。</summary>
    private float _stepTiltAngle;
    /// <summary>台阶倾斜的剩余时间 (s)；0 = 没有倾斜。</summary>
    private float _stepTiltTimer;

    // ── v0.4：翻越后的加速宽限（需求"翻越结束快速恢复满速"） ──
    private float _vaultRecoverTimer;
    /// <summary>翻越小障碍前记下的水平速度（翻越结束时按 VaultSpeedRetention 还回去）。</summary>
    private float _vaultExitSpeed;
    /// <summary>翻越结束时的水平方向。</summary>
    private F3 _vaultExitDir = F3.Zero;

    // ── 统计 ──
    private int _tickCount;
    private int _slideHopCount;
    private int _bunnyHopCount;
    private int _mantleCount;

    public ApexMovementCore(MovementConfig? config = null)
    {
        _cfg = config ?? MovementConfig.Apex();
        _cfg.Validate();
        _currentHeight = _cfg.StandHeight;
    }

    // ═══════════════════════════ 只读访问器 ═══════════════════════════

    public MovementConfig Config => _cfg;
    public F3 Position => _pos;
    public F3 Velocity => _vel;
    public F3 Up => _up;
    public MovementState State => _state;
    public bool OnGround => _onGround;
    public float CrouchAmount => _crouchAmount;
    public float CurrentHeight => _currentHeight;
    public float Yaw => _yaw;
    public float Pitch => _pitch;
    public int TickCount => _tickCount;
    public int SlideHopCount => _slideHopCount;
    public int BunnyHopCount => _bunnyHopCount;
    public int MantleCount => _mantleCount;
    public MantleClass CurrentMantleClass => _mantleClass;
    public MantlePhase CurrentMantlePhase => _mantlePhase;
    public bool JumpFatigueActive => _jumpFatigueState;
    public float FallStunRemaining => _fallStunTimer;
    public bool SlideBoostOnCooldown => _slideBoostCooldown > 0f;
    public LandingTier LastLandingTier => _landingTier;
    public float LastLandingFallDistance => _landingFallDistance;

    /// <summary>
    /// 脚底位置。`Position` 是**胶囊中心**，中心到脚底的距离是「圆柱半高 + 球端半径」
    /// = 当前身高的一半（胶囊总高恰为 CurrentHeight）。
    ///
    /// 曾经的 bug：这里写成 `(_currentHeight - CapsuleRadius*2)*0.5`，那是圆柱半高，
    /// 比真实脚底高出一个半径（0.35m）。后果是攀爬的墙探针被抬高 0.35m，
    /// 直接飞过 0.8m 矮墙 ⇒ 翻越永不触发。
    /// </summary>
    public F3 FeetPosition => _pos - _up * (_currentHeight * 0.5f);

    /// <summary>
    /// **水平**速度大小 (m/s)，即垂直于重力方向的分量。
    /// 注意不要写成 `_vel.Length()` —— 那会混入竖直分量，
    /// 导致滑铲阈值、滑铲跳保留速度等在跳跃/下落瞬间读到偏大的值。
    /// </summary>
    public float HorizontalSpeed => F3.ProjectOnPlane(_vel, _up).Length();

    /// <summary>
    /// 站立是否被头顶空间挡住（= <see cref="UpdateCrouch"/> 里那道检查的结果）。
    ///
    /// 只读、无副作用，供自检与 HUD 观察「蹲着起不来」到底是"头顶真有东西"
    /// 还是"状态机卡住"。
    /// </summary>
    public bool HeadBlockedForStanding
    {
        get
        {
            if (_world == null) return false;
            float hh = (_cfg.StandHeight - _cfg.CapsuleRadius * 2f) * 0.5f;
            F3 testPos = _pos + _up * ((_cfg.StandHeight - _currentHeight) * 0.5f);
            return _world.Overlaps(testPos, _cfg.CapsuleRadius, hh);
        }
    }

    /// <summary>
    /// **当前行走平面内**的移动速度 (m/s)：速度投影到「垂直于地面法线」的平面后的模长。
    ///
    /// v0.5（需求 m01202）：滑铲入口判定必须用它，而不是 <see cref="HorizontalSpeed"/>。
    /// 两者在坡上差一个 cosθ：
    ///   · 平地一致；
    ///   · 30° 坡上，滑铲平面速度 10 m/s ⇒ 水平分量只有 8.66 m/s。
    /// 用水平分量判定会让"坡上滑铲更难触发"，与需求「基于当前行走平面的移动速度判定」不符。
    ///
    /// 不在接地状态时退回 <see cref="HorizontalSpeed"/>（没有地面法线可用）。
    /// </summary>
    public float GroundPlaneSpeed => _onGround
        ? F3.ProjectOnPlane(_vel, _groundNormal).Length()
        : HorizontalSpeed;

    /// <summary>沿重力方向的速度分量（正 = 向上）。</summary>
    public float VerticalSpeed => F3.Dot(_vel, _up);

    /// <summary>
    /// 台阶倾斜角（弧度）。上台阶那一 tick 由 <c>tan(theta) = b / a</c> 算出，
    /// 之后在 <see cref="MovementConfig.StepTiltTime"/> 内线性衰减到 0。
    /// 0 = 没有正在进行的台阶过渡。HUD / 自检读它。
    /// </summary>
    public float StepTiltAngle => _stepTiltAngle;

    /// <summary>当前状态快照（相机子系统 / HUD / 测试读它）。</summary>
    public MovementSnapshot Snapshot => new()
    {
        Position = _pos,
        Velocity = _vel,
        Up = _up,
        Yaw = _yaw,
        Pitch = _pitch,
        CurrentHeight = _currentHeight,
        CrouchAmount = _crouchAmount,
        FeetOffsetFromCenter = _currentHeight * 0.5f,
        EyeOffsetFromFeet = _currentHeight - _cfg.EyeHeightOffset,
        HorizontalSpeed = HorizontalSpeed,
        VerticalSpeed = VerticalSpeed,
        OnGround = _onGround,
        // 「是否冲刺」= 冲刺意图 ∧ 真的跑起来了。用速度兜底是为了覆盖
        // 冲刺途中被 Fallstun/斜坡减速压下来的情况（此时不该继续摆冲刺姿态）。
        Sprinting = _sprintIntent && _state == MovementState.Ground && _crouchAmount < 0.5f &&
                    HorizontalSpeed > _cfg.WalkSpeed * 1.05f,
        Sliding = _state == MovementState.Slide,
        Crouching = _crouchAmount > 0.5f,
        Climbing = _state == MovementState.Climb,
        State = _state,
        MantleClass = _mantleClass,
        MantlePhase = _mantlePhase,
        TraversalProgress = _state is MovementState.Mantle or MovementState.Vault or MovementState.Window
            ? Math.Min(1f, _traversalTime / MathF.Max(1e-4f, _traversalDuration))
            : -1f,
        LandingTier = _landingTier,
        LandingFallDistance = _landingFallDistance,
        LandingImpactSpeed = _landingImpactSpeed,
        ClimbNoInputRemaining = _state == MovementState.Climb
            ? MathF.Max(0f, _cfg.ClimbNoInputGrace - _climbNoInputTimer)
            : 0f,
        ClimbApproaching = _climbApproaching,
        Hanging = _state == MovementState.Hang,
        Slipping = _state == MovementState.Slip,
        SlipDirection = _slipSampleValid ? _slipSample.Direction : F3.Zero,
        SlipAirRestrictRemaining = _slipAirRestrictTimer,
        // 统一状态机里 Climb（按 W 上爬）与 WallHang（扒着）都是「挂在墙上」，
        // HUD 的 hang= 读数与相机侧倾都按这一条走。
        WallHanging = _state is MovementState.WallHang or MovementState.Climb,
        WallHangGrip = _state is MovementState.WallHang or MovementState.Climb
            ? MathF.Max(0f, _wallHangGrip)
            : 0f,
        WallHangTime = _state is MovementState.WallHang or MovementState.Climb ? _wallHangTime : 0f,
        BobPhase = _bobPhase,
        TickCount = _tickCount,
        Gait = _gait,
        StepTiltAngle = _stepTiltAngle,
    };

    public void BindWorld(IMovementWorld world) => _world = world;

    /// <summary>
    /// 设置**重力方向的反方向**（= 当前站立面的"上"），用于星球/球形世界（v0.5 预留接口）。
    ///
    /// 需求（m00818）：「重力方向需要是可改变的，因为星球是球形，玩家的这些移动方向
    /// 啥的也要与星球重力方向绑定，比如移动是与重力方向为 90° 夹角方向，跳跃是与重力
    /// 相反方向」。
    ///
    /// 本类内部所有运动都以 <see cref="_up"/> 为基准轴：
    ///   · 移动 = 把输入方向投影到垂直于 up 的平面（90° 夹角）；
    ///   · 跳跃 / 反重力 = 沿 +up；
    ///   · 重力 / 下落 = 沿 −up；
    ///   · 滑铲坡度 = 坡面法线与 up 的夹角。
    /// 所以只要在球形行星上每 tick 把局部 up 传进来，一整套方向就自动跟着转，
    /// **不需要给每种运动单独写"星球版"**。
    ///
    /// 与 <see cref="Reset"/> 的区别：Reset 是"传送到某处并清空状态"，本方法只改
    /// 参考系，保留速度与碰撞状态（用于走在大球面上时 up 缓慢变化）。
    /// 速度里沿新 up 的分量会被保留 —— 重力会在随后的 tick 里把它吃掉，
    /// 这正是"从平面走进曲面"时该有的连续行为。
    /// </summary>
    public void SetUpDirection(F3 up)
    {
        F3 n = up.Normalized();
        if (n.Length() < 1e-6f) return;          // 退化输入直接忽略（不破坏状态）
        if ((n - _up).Length() < 1e-7f) return;  // 没变就不动，避免无谓的状态抖动
        _up = n;
        // 参考系变了，脚下是否还有地必须重新判定（下一 tick 的 UpdateGroundContact 会做）。
        if (_state is MovementState.Ground or MovementState.Slide)
        {
            _state = MovementState.Air;
            _slideTime = 0f;
            _slideBoostGranted = false;
        }
    }

    /// <summary>当前参考系的上方向（= 重力反方向）。球形行星每 tick 传进来即可。</summary>
    public F3 UpDirection => _up;

    /// <summary>当前脚下的地面法线（未贴地时等于 <see cref="UpDirection"/>）。</summary>
    public F3 GroundNormal => _groundNormal;

    /// <summary>
    /// 一个 tick 内**各个来源**产生的加速度（m/s²，矢量），给三维调试渲染器用。
    ///
    /// 这是"把求解器拆开看"的窗口：每个分量都是本 tick 内该来源造成的
    /// <c>Δv / dt</c>，所以 <see cref="Net"/> 恒等于 <c>(v_end − v_start)/dt</c>。
    /// 各分量之和不等于 Net —— 碰撞投影是**非线性**的（把速度投到平面会丢掉法向分量），
    /// 所以差值就是"碰撞吃掉的量"，单独记在 <see cref="Collision"/> 里。
    ///
    /// 需求（m01621）：「除了速度还有作用力产生的加速度，也实时渲染三维箭头，并且标注大小」。
    /// </summary>
    public readonly struct AccelBreakdown
    {
        /// <summary>重力加速度（空中 = −up·g；滑铲 = 坡面重力分量）。</summary>
        public readonly F3 Gravity;
        /// <summary>摩擦（地面库仑 / 滑铲库仑）。</summary>
        public readonly F3 Friction;
        /// <summary>输入推进（含坡面合速度加成）。</summary>
        public readonly F3 Input;
        /// <summary>瞬时冲量（起跳 / 滑铲 boost）。</summary>
        public readonly F3 Impulse;
        /// <summary>碰撞与贴地投影（速度被墙面/地面改变的部分）。</summary>
        public readonly F3 Collision;
        /// <summary>合计 = (v_end − v_start)/dt。</summary>
        public readonly F3 Net;

        public AccelBreakdown(F3 gravity, F3 friction, F3 input, F3 impulse,
                              F3 collision, F3 net)
        {
            Gravity = gravity; Friction = friction; Input = input;
            Impulse = impulse; Collision = collision; Net = net;
        }
    }

    private F3 _accGravity;
    private F3 _accFriction;
    private F3 _accInput;
    private F3 _accImpulse;
    private F3 _accelStartVel;

    /// <summary>上一 tick 的加速度分解（见 <see cref="AccelBreakdown"/>）。</summary>
    public AccelBreakdown LastAccel { get; private set; }

    /// <summary>重置到指定位置/朝向（确定性：不读取任何外部随机源）。</summary>
    public void Reset(F3 position, float yaw = 0f, float pitch = 0f, F3? up = null)
    {
        _pos = position;
        _vel = F3.Zero;
        _up = (up ?? F3.Up).Normalized();
        _yaw = yaw;
        _pitch = pitch;
        _state = MovementState.Air;
        _crouchAmount = 0f;
        _currentHeight = _cfg.StandHeight;
        _slideTime = 0f;
        _slideBoostCooldown = 0f;
        _slideEntrySpeed = 0f;
        _slideBoostGranted = false;
        _traversalTime = 0f;
        _traversalDuration = 0f;
        _traversalCooldown = 0f;
        _mantleClass = MantleClass.Below;
        _mantlePhase = MantlePhase.Grab;
        _isWindowTraversal = false;
        _climbTime = 0f;
        _climbAttachHeight = 0f;
        _climbBaseY = 0f;
        _climbWallNormal = F3.Zero;
        _climbNoInputTimer = 0f;
        _climbApproaching = false;
        _climbApproachRemaining = 0f;
        _climbHangTop = F3.Zero;
        _climbLastWallHit = F3.Zero;
        _lastClimbAttachY = 0f;
        _lastClimbWallNormal = F3.Zero;
        _hasClimbed = false;
        _slipSample = default;
        _slipSampleValid = false;
        _slipAirRestrictTimer = 0f;
        _slipAirRestrictDir = F3.Zero;
        _wallHangTime = 0f;
        _wallHangSlip = 0f;
        _wallHangGrip = 1f;
        _wallHangCooldown = 0f;
        _wallHangNormal = F3.Zero;
        _wallHangLastHit = F3.Zero;
        _wallGrabNeedW = false;
        _leftGroundByJump = false;
        _brakeRamp = 0f;
        _isSliding = false;
        _airEntrySpeed = 0f;
        _jumpBuffered = false;
        _jumpBufferTimer = 0f;
        _coyoteTimer = 0f;
        _justJumped = false;
        _jumpRampTicksLeft = 0;
        _jumpRampThrust = 0f;
        _onGround = false;
        _groundNormal = _up;
        _jumpFatigueState = false;
        _timeSinceLanded = 999f;
        _fallStunTimer = 0f;
        _fallStunTotal = 0f;
        _airPeakHeight = F3.Dot(position, _up);
        _wasAirborne = false;
        _landingTier = LandingTier.None;
        _landingFallDistance = 0f;
        _landingImpactSpeed = 0f;
        _bobPhase = 0f;
        _gait = default;
        _stepTiltAngle = 0f;
        _stepTiltTimer = 0f;
        _vaultRecoverTimer = 0f;
        _vaultExitSpeed = 0f;
        _vaultExitDir = F3.Zero;
        _tickCount = 0;
        _slideHopCount = 0;
        _bunnyHopCount = 0;
        _mantleCount = 0;
    }

    /// <summary>外部（如传送/电梯/移动平台）直接设定速度，用于保持确定性。</summary>
    public void SetVelocity(F3 v) => _vel = v;

    // ═══════════════════════════════════════════════════════════════
    //  主推进：每个固定 tick 调用一次，dt 必须是 Config.FixedDelta
    // ═══════════════════════════════════════════════════════════════

    public void Step(in MovementInput input, float dt)
    {
        if (_world == null)
            throw new InvalidOperationException("ApexMovementCore: 必须先 BindWorld()");

        _tickCount++;

        // 加速度分解（给三维调试渲染器）：记起点速度、清各来源累加器。
        // 各来源在施加处累加，末尾用"总变化 − 各来源之和"反推碰撞吃掉的部分。
        _accelStartVel = _vel;

        // 重力**全量**记录（−up·g），不分状态。
        //
        // 为什么不只记"沿坡分量"：站在地上时重力照样在拉（9.81 m/s² 向下），
        // 只是被地面支撑力抵消了 —— 调试时这正是最该看见的一对力。
        // 记全量后，地面支撑力会自动落进"碰撞"那一项里（见 Step 末尾的结算），
        // 于是"重力 ↓9.81 + 支撑 ↑9.81 = 合计 0"一眼可读。
        _accGravity = -_up * _cfg.Gravity;
        _accFriction = F3.Zero;
        _accInput = F3.Zero;
        _accImpulse = F3.Zero;

        // 记录冲刺**意图**（不是速度）。相机侧倾/FOV 要按意图走：
        // 收枪行走速度 = 4.407×1.15 = 5.068 m/s，已超过 `WalkSpeed×1.05`，
        // 若用速度反推「是否冲刺」，收枪散步会被误判成冲刺（FOV +3°、侧倾 1.5°）。
        _sprintIntent = input.SprintHeld;

        // 0) 清空本 tick 的落地事件（相机每 tick 只应看到一次落地）
        _landingTier = LandingTier.None;
        _landingFallDistance = 0f;
        _landingImpactSpeed = 0f;

        // 1) 视角（鼠标增量在本 tick 全部消费 —— 见 MovementInput 注释）
        _yaw += input.MouseDeltaYaw;
        _pitch += input.MouseDeltaPitch;
        _pitch = Clamp(_pitch, -MathF.PI / 2f + 0.01f, MathF.PI / 2f - 0.01f);

        // 2) 计时器
        if (_slideBoostCooldown > 0f) _slideBoostCooldown -= dt;
        if (_traversalCooldown > 0f) _traversalCooldown -= dt;
        if (_fallStunTimer > 0f) _fallStunTimer = MathF.Max(0f, _fallStunTimer - dt);
        if (_jumpBufferTimer > 0f) { _jumpBufferTimer -= dt; if (_jumpBufferTimer <= 0f) _jumpBuffered = false; }
        if (_coyoteTimer > 0f) _coyoteTimer -= dt;
        if (_timeSinceLanded < 900f) _timeSinceLanded += dt;
        if (_slipAirRestrictTimer > 0f) _slipAirRestrictTimer = MathF.Max(0f, _slipAirRestrictTimer - dt);
        if (_wallHangCooldown > 0f) _wallHangCooldown = MathF.Max(0f, _wallHangCooldown - dt);
        if (_stepTiltTimer > 0f) _stepTiltTimer = MathF.Max(0f, _stepTiltTimer - dt);
        if (_vaultRecoverTimer > 0f) _vaultRecoverTimer = MathF.Max(0f, _vaultRecoverTimer - dt);

        if (input.JumpPressed) { _jumpBuffered = true; _jumpBufferTimer = _cfg.JumpBufferTime; }

        // 3) 翻越/窗口期间独占推进
        if (_state is MovementState.Mantle or MovementState.Vault or MovementState.Window)
        {
            StepTraversal(input, dt);
            return;
        }

        // 4) 贴墙（Climb 上爬 / WallHang 扒墙）期间独占推进
        //
        // ★ v0.4：两者合成**一个**状态机 `StepWallGrab`。
        //
        //   v0.3 是两套独立入口（Climb 要朝墙动量 + 空中；WallHang 要按住 Ctrl），
        //   需求方明确要求「扒墙默认化、不需要按任何键」「按住 W 才往上爬，
        //   松开 W 就进入扒墙」，所以"上爬"和"扒着"本来就只是同一个挂墙状态下的
        //   两个输入分支，拆成两套状态机只会带来两套不一致的脱离规则。
        //   现在统一为：贴墙抓住 → 按 W 就是 Climb（向上），不按 W 就是 WallHang（挂着）。
        if (_state is MovementState.Climb or MovementState.WallHang)
        {
            // 注意：这里**故意**整帧独占、不落到下面的状态机。
            //
            // v0.5.2 曾试过「脱手当帧继续推进」（消除 4~6 m 墙 End boost 的 1 帧空转），
            // 但实测会打破两条既有语义：
            //   · 蹬墙跳当帧进入 StepAir ⇒ 同一帧内立刻 TryAttachWallGrab 又抓回墙上，
            //     蹬墙跳的离墙速度被吃掉（ClimbTest ⑮ / SlipHangTest 各 1 项红）；
            //   · 7.5 m 超高墙的「单段爬升 ≤ ClimbAttachOffset」被绕过（实测 7.17 m）。
            // 代价是每段脱手少 1 帧位移 —— 在逐帧曲线上是 1 个 16.7 ms 的停格，
            // 远小于已经修掉的那些 0.3~1.8 m 单帧瞬移。
            StepWallGrab(input, dt);
            return;
        }

        // 5) 蹲伏插值（含"空中向中间缩"的处理）
        UpdateCrouch(input, dt);

        // 5b) 打滑体积采样 → 进/出 Slip 状态
        //
        // 逆向（§5.10）：C_TriggerSlipSphere 是**世界里的体积**（不是地面材质），
        // 玩家身上记 m_touchingSlipTriggers*（数量/速度/加速度/方向/原点）与
        // m_slipAirRestrictTime / m_slipAirRestrictDirection。
        // 采样按 SlipProbeInterval 抽样（默认 30 Hz），两次探针之间复用上次结果 ——
        // 否则抽样空档里会误判"已离开滑区"而抖动进出状态。
        UpdateSlip(dt);

        // 6) 自动翻越探测 —— **v0.4 已整体移除**
        //
        // v0.2/v0.3 在这里无条件调 TryTraversal，于是"贴墙按 W"就有一半概率
        // 直接被抓上墙（这就是需求方反馈的「按一下 W 就开始攀爬」）。
        // v0.3 把它收紧到"只在地面/滑铲时探测"，但只要按着 W 贴墙**仍然**会自动翻。
        //
        // v0.4 的三档分级（需求方口径）把这件事彻底讲清楚了：
        //   · ≤0.25 m  台阶   → 由 MoveWithCollision 的 TryStepUp 自动跨越，不需要探测
        //   · ≤1.00 m  小障碍 → **必须 W + Space**（在 StepGround 的跳跃分支里触发 Vault）
        //   · >1.00 m  墙     → 地面上一律撞停，起跳后由空中 → 贴墙抓住 → Climb 接管
        // 所以这里不再有任何"自动探测"，翻越只由跳跃键显式触发。

        // 7) 按状态推进速度
        switch (_state)
        {
            case MovementState.Slide: StepSlide(input, dt); break;
            case MovementState.Slip: StepSlip(input, dt); break;
            case MovementState.Ground: StepGround(input, dt); break;
            case MovementState.Hang:
                StepHang(input, dt);
                // ★ v0.5.2 平滑修复（P1-1）：Hang 这一帧如果已经切进 Mantle/Vault/Window，
                //   就**同帧**把翻越的第一步推出去，不要空转一帧。
                //   原来 StepHang 只改状态、位移等下一帧的 StepTraversal ⇒ 那一帧 |Δp| = 0，
                //   在逐帧曲线上就是一个"顿"点（3.0 m 墙实测整条链路有 2 个这样的空转帧）。
                if (_state is MovementState.Mantle or MovementState.Vault or MovementState.Window)
                {
                    StepTraversal(input, dt);
                    return;
                }
                break;
            default: StepAir(input, dt); break;
        }

        // 7b) 台阶倾斜的计时器在这里只负责衰减（见下方 9b）
        //     —— 真正的速度旋转放在 MoveWithCollision / UpdateGroundContact 之后，
        //        因为"是不是台阶"要等 TryStepUp 扫掠完才知道。

        // 8) 位移与碰撞
        MoveWithCollision(dt);

        // 9) 落地判定 + 状态迁移
        UpdateGroundContact(dt);

        // 9a) 落地即滑铲（v0.4.2）───────────────────────────────────────
        //
        // 需求原文（m04709 第 2 条）：「蹲键不松的话落地后会继续保持滑铲状态，
        // 并且可以继续跳起来」。
        //
        // 为什么必须有这一步：落地那一 tick 的 UpdateGroundContact 只把状态写成
        // Ground，真正的地面推进要等下一 tick 的 StepGround —— 而 StepGround 是
        // **先吃一 tick 地面摩擦**（Friction=4.0；6.6 m/s 时一次掉 ≈0.44 m/s）
        // 才轮到滑铲触发分支。于是连续滑铲跳每跳白掉 0.44 m/s，
        // 6.60 → 6.16 → 5.61 → 5.09，第 4 跳就跌破 SlideMinStartSpeed(5.08)
        // 再也滑不起来 —— 需求要的「可以继续跳起来」实际上只成立 3 次。
        //
        // 落地当场转滑铲既省掉这一 tick 的无谓摩擦，也更贴字面语义：玩家落地时
        // 本来就还蹲着（空中保持蹲姿体积），滑铲是**同一个姿态的延续**，
        // 不该在中间插一帧"站着地面行走"。
        //
        // 判据用 _landingTier != None（= 本 tick 刚落地，每 tick 开头清零），
        // 所以它只影响"落地"这一个瞬间，不改变任何地面滑铲的既有触发路径。
        // v0.5（需求 m01202）：判据换成**落地瞬间的平面速度**。
        // 落地那一 tick，UpdateGroundContact 已把速度投影到地面法线上，
        // 所以 GroundPlaneSpeed 就是"顺坡方向还剩下的速度" ——
        // 从高处落斜坡、被加速道具打飞落地、滑铲跳落地，全部走同一个判据。
        // boost 不参与判定（进入之后才加）。
        if (_landingTier != LandingTier.None && _onGround &&
            _state == MovementState.Ground &&
            input.CrouchHeld && GroundPlaneSpeed >= _cfg.SlideMinStartSpeed)
        {
            BeginSlide(input);
        }

        // 9b) 台阶倾斜：把速度**整体旋转**到倾角上（大小不变，只改方向）
        //
        // 需求原话：「上台阶时水平速度变为斜向上的速度再变回水平，
        // **只有方向变化、大小不变**，不触发任何动作」。
        // 倾斜角 θ 满足 tan(θ) = b / a（b = 台阶高度，a = 脚底中心到脚底边缘的横向距离），
        // 在 StepTiltTime 内从 θ₀ 线性衰减到 0，于是速度方向"抬起 → 落回水平"。
        // 放在 UpdateGroundContact 之后：位置已经被贴地吸附钳好，抬起的竖直分量
        // 不会真的把人顶飞（同一 tick 内吸附已经生效）。
        ApplyStepTilt();

        // 10) 步态（帧率无关：按 tick 累加，不按渲染帧）
        UpdateBobPhase(dt);

        // 11) 结算加速度分解（三维调试渲染器读这个）
        //
        //     碰撞/贴地投影是**非线性**的（把速度投到平面会直接丢掉法向分量），
        //     所以不用"逐项相加"去凑合计，而是用
        //     <c>碰撞 = 总变化 − 重力 − 摩擦 − 输入 − 冲量</c> 反推。
        //     这样恒有 <c>各项之和 == Net == (v_end − v_start)/dt</c>，箭头画出来自洽。
        F3 net = (_vel - _accelStartVel) / dt;
        F3 collision = net - (_accGravity + _accFriction + _accInput + _accImpulse);
        LastAccel = new AccelBreakdown(_accGravity, _accFriction, _accInput,
                                       _accImpulse, collision, net);

        // 12) 记录本 tick 结束时的状态（下一 tick 用它识别"刚进入空中"等边沿）
        _lastState = _state;
    }

    /// <summary>
    /// 把速度按当前台阶倾角旋转：水平分量 × cos θ、竖直分量 += |v_h| × sin θ。
    ///
    /// 这样 <c>|v|</c> 完全不变（只改方向），符合需求「只有方向变化、大小不变」。
    /// θ 在 <see cref="MovementConfig.StepTiltTime"/> 内线性衰减，于是抬起的
    /// 竖直分量会平滑落回 0，而不是"上一级台阶就被弹飞"。
    /// </summary>
    private void ApplyStepTilt()
    {
        if (_stepTiltTimer <= 0f || _stepTiltAngle <= 1e-5f) return;

        float t = _cfg.StepTiltTime <= 1e-4f ? 0f : _stepTiltTimer / _cfg.StepTiltTime;
        if (t <= 0f) return;

        float angle = _stepTiltAngle * t;
        F3 horiz = F3.ProjectOnPlane(_vel, _up);
        float hSpeed = horiz.Length();
        if (hSpeed < 1e-5f) return;

        F3 dir = horiz / hSpeed;

        // ★ v0.5.3 修复（需求 m02395 第 3 点「上台阶有顿挫感」）：
        //   这里原来是
        //     _vel = dir*(hSpeed*cos θ) + _up*(hSpeed*sin θ)
        //   即"把速度大小守恒地转成斜向上"。**但竖直分量根本兑现不了**：
        //   人已经站在台阶面上了，抬起来的那部分速度下一 tick 就被
        //   UpdateGroundContact 的贴地吸附整段抹掉（逐 tick 实测：
        //   倾斜窗口内 posY 恒为 1.1510，而 snapDown 每 tick 0.0325~0.0577 m）。
        //   于是**净效果只剩水平速度被乘了一个 cos θ，再也补不回来** ——
        //   走 4.407→3.866（87.7%）、跑 6.604→4.543（68.8%）、
        //   0.25 m 台阶跑起来只剩 52.5%。这就是"走楼梯像踩刹车"的根因。
        //
        //   修法：**水平分量原样保留**，竖直分量照旧叠加（方向仍然"抬起"，
        //   与 v0.4 的需求口径一致：台阶上速度方向临时变成斜向上）。
        //   这样 |v| 在窗口内略增（√(1+sin²θ) ≈ 1.11 倍），但那部分竖直
        //   本来就一直被封顶在贴地面上、不产生任何真实位移，所以既不额外给能量，
        //   也不会改变弹道；水平速度则**一点不丢**。
        //
        //   实测（0.20 m 台阶、走路 4.407 m/s）：上台阶后水平速度 4.407（100%），
        //   倾斜窗口内 ticks<90% 顶速 = 0（原 31）。
        _vel = dir * hSpeed + _up * (hSpeed * MathF.Sin(angle));
    }

    // ───────────────────────── 地面 ─────────────────────────

    private void StepGround(in MovementInput input, float dt)
    {
        float accel = _crouchAmount > 0.5f ? _cfg.CrouchAccel
                    : input.SprintHeld ? _cfg.SprintAccel
                    : _cfg.GroundAccel;

        // 翻越后的加速宽限：需求「翻越结束快速恢复满速」。
        // 用一个短窗口放大加速度，比"直接塞满速度"自然得多（不会瞬移感）。
        if (_vaultRecoverTimer > 0f) accel *= _cfg.VaultRecoverAccelBoost;

        F3 wishDir = WishDirection(input);
        float wishSpeed = WishSpeed(input);

        // ── 坡面合速度（v0.5 / 需求 m00914 ⑥）────────────────────────
        //
        // **跑动**（冲刺键 + 未蹲）吃坡度 + 重力加成：
        //   v_target = v_base + τ·g·sinθ·(下坡方向 · 期望方向)
        //   下坡更快、上坡更慢、横穿不变，且自动跟着星球重力走。
        // **走 / 蹲走**不吃 —— 需求「如果是慢慢走那就是玩家不希望摔倒」，
        //   那一档只保留库仑摩擦（防滑），速度恒定。
        //
        // 详见 MovementConfig.SlopeSpeedTimeConstant 的推导（速度调节器模型）。
        if (_cfg.SlopeSpeedTimeConstant > 0f && input.SprintHeld && _crouchAmount <= 0.5f)
        {
            float cosT = Clamp(F3.Dot(_groundNormal, _up), 0f, 1f);
            float sinT = MathF.Sqrt(MathF.Max(0f, 1f - cosT * cosT));
            if (sinT > 1e-4f)
            {
                F3 slopeWish = F3.ProjectOnPlane(wishDir, _groundNormal);
                float wl = slopeWish.Length();
                if (wl > 1e-4f)
                {
                    F3 downhill = F3.ProjectOnPlane(-_up, _groundNormal).Normalized();
                    float along = F3.Dot(slopeWish / wl, downhill);   // +1 正下坡, −1 正上坡
                    float mag = MathF.Sqrt(input.MoveX * input.MoveX + input.MoveZ * input.MoveZ);
                    if (mag > 1f) mag = 1f;
                    wishSpeed = MathF.Max(0f,
                        wishSpeed + _cfg.SlopeSpeedTimeConstant * _cfg.Gravity * sinT * along * mag);
                }
            }
        }

        ApplyGroundFrictionAndDrive(input, dt, accel, wishSpeed);

        // 下坡贴地：把速度投影到地面平面，避免"跳下斜坡"
        //
        // ★ v0.4：台阶倾斜进行中**跳过**这次投影 —— 否则刚被 ApplyStepTilt 抬起来的
        //   竖直分量会在下一 tick 就被抹平，需求「水平速度变为斜向上的速度再变回水平」
        //   只能维持一帧。倾斜窗口只有 StepTiltTime（0.12s），过后照常投影。
        if (_stepTiltTimer <= 0f)
            _vel = F3.ProjectOnPlane(_vel, _groundNormal);

        // 滑铲触发（v0.5 / 需求 m01202）：
        //   判定量是**当前行走平面内**的移动速度（速度投影到地面平面后的模长，
        //   不是世界水平的 HorizontalSpeed —— 坡上两者差一个 cosθ），
        //   且这个速度**不含 boost**（boost 是进入之后才加的）。
        // 注意：**滑铲本身没有冷却**，只有 Slideboost 有 2s 冷却（wiki）。
        if (input.CrouchHeld && GroundPlaneSpeed >= _cfg.SlideMinStartSpeed)
        {
            BeginSlide(input);
            return;
        }

        // ── 跳跃 / 翻越小障碍 ────────────────────────────────────
        //
        // ★ v0.4 三档分级里的第二档：**W + Space = 翻越 ≤ VaultMaxHeight 的小障碍**。
        //
        // 为什么放在跳跃分支里而不是每 tick 自动探测：
        // 需求方明确「按 W 会撞墙然后停止移动，只有按 W 贴墙 + Space 跳起后才会爬墙」。
        // 所以"按 W"本身不能触发任何抓墙动作 —— 必须由跳跃键显式发起。
        // 翻越失败（没有障碍 / 障碍太高）就退化成普通跳跃，行为完全不变。
        if (_jumpBuffered)
        {
            if (input.MoveZ > 0.3f && TryVaultObstacle(input))
            {
                _jumpBuffered = false;
                _jumpBufferTimer = 0f;
                return;
            }

            DoJump(fromSlide: false, crouchHeld: input.CrouchHeld);
            return;   // 本 tick 不再贴地吸附，交给空中分支下一 tick 接管
        }

        // 注意：这里**故意不加**任何"向下压"的速度。
        //
        // 历史教训（v0.2 重写时踩到）：旧版每 tick 给一个 `-GroundStickSpeed*dt` 的
        // 向下速度来"保证贴地探测命中"。但这会让胶囊每 tick 往下沉 0.5mm，
        // 逐渐穿透地面；一旦穿透，胶囊扫掠在"起点已接触"时返回 fraction=0，
        // 于是**水平移动也被一起挡住**（表现为"站着却完全走不动"）。
        // 贴地完全由 UpdateGroundContact 的 0.2m 探针 + 吸附完成，不需要这个速度。
    }

    // ───────────────────────── 空中 ─────────────────────────

    private void StepAir(in MovementInput input, float dt)
    {
        // ★ v0.5.2：**进入 Air 的第一帧**用当前速度当"离地速度"。
        //
        //   空中速度上限（见下面 AirControlAccel 那一段）是相对"离地速度"算的，
        //   而脱离地面的路径不止"跳跃"一条：蹬墙跳、松手掉、翻越收尾、滑铲滑出……
        //   那些路径的初速可能远高于上一次落地时的记录值 —— 如果不刷新基准，
        //   上限会把**已经合法获得**的速度硬拽回去（实测蹬墙跳 vz 4.2 → 0.67）。
        if (_lastState != MovementState.Air) _airEntrySpeed = HorizontalSpeed;

        // ★ Apex **没有空气摩擦**：不做任何水平衰减。
        //   只有 AirFriction > 0 时才衰减（默认 0，保留字段以支持别的项目）。
        if (_cfg.AirFriction > 0f) ApplyAirFriction(dt, _cfg.AirFriction);

        // 空中加速：wishSpeed 被硬限制在 AirWishSpeed(60hu/s) —— 这是 air strafe 的物理根因。
        F3 wishDir = WishDirection(input);
        float wishSpeed = MathF.Min(WishSpeed(input), _cfg.AirWishSpeed);

        // ★ 离开打滑体积后的方向限制（m_slipAirRestrictTime / Direction）
        //
        // Apex 在离开滑区后会"锁一会儿方向"：这段时间里空中加速只能落在
        // 离开方向的圆锥内，所以不能立刻反身拉回来 —— 这是滑道出口手感的一部分。
        // 完全锁死方向会让人觉得失控，所以用圆锥（SlipAirRestrictConeDeg，默认 35°）。
        if (_slipAirRestrictTimer > 0f && _slipAirRestrictDir.LengthSq() > 1e-8f &&
            wishDir.LengthSq() > 1e-8f)
        {
            wishDir = ClampToCone(wishDir, _slipAirRestrictDir, _cfg.SlipAirRestrictConeDeg);
        }

        // ── 空中输入（v0.5.2 收紧，需求 m01928 第 4 点）──────────────
        //
        // 需求：「空中输入也不会产生什么作用，只有很微小的偏移，基本上只能靠惯性飞跃」。
        //
        // 旧实现是 Source 谱系的 air-strafe（AirAccel=10 + AirWishSpeed=1.524）：
        // 按住侧移 + 缓慢转视角能把 6.6 m/s 一路加到 7.95 m/s（+20%），
        // 显然不是"靠惯性"。
        //
        // 现在两道闸一起生效：
        //   ① 加速度改用 AirControlAccel（默认 1.2 m/s²，只有原来的 12%）；
        //   ② **硬上限**：水平速度不允许超过「离地速度 + AirSteerSpeedAllowance(0.5)」。
        //      转视角/换方向只会让速度矢量**转向**，不会让它变长 ——
        //      "空中只能微调方向、不能凭空加能量" 成为一条不变量。
        {
            F3 before = _vel;
            Accelerate(wishDir, wishSpeed, _cfg.AirControlAccel, dt);

            F3 horiz = F3.ProjectOnPlane(_vel, _up);
            float hs = horiz.Length();
            float cap = _airEntrySpeed + _cfg.AirSteerSpeedAllowance;
            if (hs > cap && hs > 1e-5f)
            {
                F3 vertical = _vel - horiz;
                _vel = vertical + horiz / hs * cap;
            }

            _accInput += (_vel - before) / dt;
        }

        // ── 起跳加速段（JumpAccelTime > 0 时才有；默认 0 时这段完全不执行）──
        //
        // 与"冲量式起跳"的唯一区别：速度不是一次给满，而是在 T 时间内匀加速到 u。
        // 重力照常作用，所以净加速度是 a − g，位移曲线是连续的抛物线拼接。
        if (_jumpRampTicksLeft > 0)
        {
            _vel += _up * (_jumpRampThrust * dt);
            _accImpulse += _up * _cfg.JumpRampThrust;
            _jumpRampTicksLeft--;
        }

        // 重力
        _vel -= _up * (_cfg.Gravity * dt);

        // 下落速度上限
        float downward = -F3.Dot(_vel, _up);
        if (downward > _cfg.MaxFallSpeed)
            _vel += _up * (downward - _cfg.MaxFallSpeed);

        // ★ v0.4.2：**空中滑铲已整体删除**（需求方：「玩家现在可以无限滑铲连跳，
        //   然后越跳越高，在空中保持滑铲姿态连续跳跃，这是不正常的」）。
        //
        //   旧逻辑在这里用 Apex 的 Air Slide 条件（水平 > 90hu/s 且下坠 > 200hu/s）
        //   直接 BeginSlide —— 于是空中的状态机变成 Slide，而 StepSlide 里既能
        //   贴地（_vel 被投影到一个**过期的** _groundNormal 上 ⇒ 手感上的"虚拟平台"）
        //   又能吃滑铲跳分支（_jumpBuffered ⇒ DoJump 给满 JumpVelocity）。
        //   跳→落→空中滑铲→再跳，每循环净赚一次跳跃初速 ⇒ 无限爬升。
        //
        //   现在的分工：
        //     · 空中按住蹲 ⇒ **只是蹲着**（UpdateCrouch 把胶囊收到蹲姿体积，
        //       空中是"中心不动、收腿"那一档，见 AirCrouchShrinksToMiddle）。
        //     · 落地后仍按住蹲且水平速度 ≥ SlideMinStartSpeed ⇒ **落地那一 tick 就
        //       由 Step() 的 9a 段当场 BeginSlide**（不是等下一 tick 的 StepGround，
        //       那样会先白吃一 tick 地面摩擦）⇒ 需求要的「落地后继续保持滑铲状态，
        //       并且可以继续跳起来」。
        //   配置项 SlideMinStartSpeedAirHorizontal / ...Vertical 已一并删除
        //   （见 MovementConfig 的同位置说明）—— 不留"有旋钮但没代码读"的假接口。

        // 土狼跳
        if (_jumpBuffered && _coyoteTimer > 0f) DoJump(fromSlide: false, crouchHeld: input.CrouchHeld);

        // 贴墙抓住（Climb 上爬 / WallHang 扒着 —— v0.4 合成**一个**入口）
        //
        // 判定见 TryAttachWallGrab：空中 + 够得到陡墙 + 视角朝向 + (正在下落 ∨ 按着 W)。
        //   · 下落中贴墙 ⇒ **自动**抓住（需求「扒墙默认，不需要按任何按键」）
        //   · 上升中      ⇒ 必须按着 W 才抓（需求「只有 W 贴墙 + Space 跳起后才会爬墙」，
        //                   同时保证"向上跳经过墙面不会被突然吸住"）
        // 探针按 ClimbProbeInterval 抽样（默认每 tick），只有空中才跑。
        if (_state == MovementState.Air && ShouldProbeClimb())
        {
            _climbProbeCount++;
            if (TryAttachWallGrab(input)) return;
        }
    }

    // ───────────────────────── 探针抽样（性能） ─────────────────────────

    /// <summary>
    /// 翻越/窗口探针是否该在本 tick 跑。
    /// 间隔 2 ⇒ 60 Hz tick 下实际 30 Hz（需求里"开销大的地方降 30 tick/s"）。
    /// </summary>
    private bool ShouldProbeTraversal()
    {
        int iv = _cfg.TraversalProbeInterval;
        return iv <= 1 || (_tickCount % iv) == 0;
    }

    /// <summary>攀爬附着探针是否该在本 tick 跑（默认每 tick，很便宜）。</summary>
    private bool ShouldProbeClimb()
    {
        int iv = _cfg.ClimbProbeInterval;
        return iv <= 1 || (_tickCount % iv) == 0;
    }

    /// <summary>
    /// 翻越探针累计执行次数（只读，自检/性能面板用）。
    ///
    /// ★ v0.4：**恒为 0**。「自动翻越探测」已整体移除 —— 需求方要求
    /// 「按 W 撞墙停住，只有 W+Space 跳起才爬墙」，所以地面上不再有每 tick 的
    /// 翻越射线；翻越只由跳跃键显式触发（<see cref="TryVaultObstacle"/>）。
    /// 保留这个属性是为了自检与性能面板的兼容，不代表功能回退。
    /// </summary>
    public int TraversalProbeCount => 0;

    /// <summary>攀爬探针累计执行次数（只读，自检/性能面板用）。</summary>
    public int ClimbProbeCount => _climbProbeCount;

    /// <summary>
    /// 攀爬到顶探针是否该在本 tick 跑。间隔 2 ⇒ 60 Hz tick 下 30 Hz。
    /// 这是**整段攀爬期间**每 tick 都要打的一条射线，属于持续开销。
    /// </summary>
    private bool ShouldProbeClimbWall()
    {
        int iv = _cfg.ClimbWallProbeInterval;
        return iv <= 1 || (_tickCount % iv) == 0;
    }

    /// <summary>打滑体积采样是否该在本 tick 跑。间隔 2 ⇒ 30 Hz。</summary>
    private bool ShouldProbeSlip()
    {
        int iv = _cfg.SlipProbeInterval;
        return iv <= 1 || (_tickCount % iv) == 0;
    }

    /// <summary>攀爬到顶探针累计执行次数（只读，自检用）。</summary>
    public int ClimbWallProbeCount => _climbWallProbeCount;

    /// <summary>打滑体积采样累计执行次数（只读，自检用）。</summary>
    public int SlipProbeCount => _slipProbeCount;

    /// <summary>
    /// 扒墙探针累计执行次数（只读，自检用）。
    ///
    /// ★ v0.4：攀爬与扒墙合并成同一个贴墙状态机后，"挂上去"只有**一个**探针入口
    /// （<see cref="TryAttachWallGrab"/>，由 <see cref="ShouldProbeClimb"/> 抽样），
    /// 所以这里与 <see cref="ClimbProbeCount"/> 是同一个计数 —— 语义上它们本来就是
    /// 同一次射线。保留两个属性是为了自检可读性与旧断言兼容。
    /// </summary>
    public int WallHangProbeCount => _climbProbeCount;

    /// <summary>
    /// 本 tick 是否处于"离开滑区后的空中方向限制"里（供 HUD / 自检读取）。
    /// </summary>
    public bool IsSlipAirRestricted => _slipAirRestrictTimer > 0f;

    // ───────────────────────── 打滑（Slip） ─────────────────────────
    //
    // 语义来源（docs/REVERSE-ENGINEERING.md §2.5.10，全部 A 级字段名）：
    //   体积  DT_TriggerSlipSphere / C_TriggerSlipSphere / trigger_slip_sphere
    //         m_slipSpeed / m_slipAcceleration / m_defaultSlipDirection
    //   玩家  m_touchingSlipTriggersCount
    //         m_touchingSlipTriggersSpeed[] / Acceleration[] / Direction[] / Origin[]
    //         m_touchingSlipTriggersSpeed / Acceleration / Direction（聚合）
    //         m_slipAirRestrictTime / m_slipAirRestrictDirection
    //   全局  slipAirRestrictDuration / slipSpeed / slipAcceleration
    //
    // 因此"打滑"不是地面材质，而是**世界里的体积**：
    //   进入体积 → 摩擦失效，速度被体积的 speed/acceleration 拽向体积方向；
    //   离开体积 → 接下来 slipAirRestrictDuration 内空中转向被限制在离开方向附近。
    //
    // 未实现的部分（明确声明）：skydive_slipSpeed / skydive_slipAcceleration /
    // m_skydiveSlipVelocity —— 那是**跳伞/滑翔**阶段的滑行，本插件没有跳伞状态。

    /// <summary>
    /// 每 tick 更新打滑状态：采样滑区 → 进/出 <see cref="MovementState.Slip"/>。
    ///
    /// 只在 Ground / Air 之间转换：
    /// 玩家主动滑铲（Slide）、翻越、攀爬、扒墙时**不被滑区抢状态** ——
    /// 那些是玩家主动意图，优先级高于环境推挤。
    /// </summary>
    private void UpdateSlip(float dt)
    {
        _ = dt;

        // 抽样（探针之间复用上次结果，避免状态抖动）
        if (ShouldProbeSlip())
        {
            _slipProbeCount++;
            _slipSampleValid = _world is ISlipWorld slipWorld &&
                               slipWorld.SampleSlip(_pos, out _slipSample);
        }

        bool inSlip = _slipSampleValid;
        bool convertible = _state is MovementState.Ground or MovementState.Air or MovementState.Slip;

        if (inSlip && convertible && _state != MovementState.Slip)
        {
            BeginSlip();
            return;
        }

        if (!inSlip && _state == MovementState.Slip)
            EndSlip();
    }

    private void BeginSlip()
    {
        _state = MovementState.Slip;
        _slideTime = 0f;
    }

    /// <summary>
    /// 离开滑区：记录"方向限制"（<c>m_slipAirRestrictDirection</c>）与剩余时间
    /// （<c>m_slipAirRestrictTime</c>），然后回到地面/空中。
    /// </summary>
    private void EndSlip()
    {
        F3 horiz = F3.ProjectOnPlane(_vel, _up);
        float speed = horiz.Length();

        // 优先记录"玩家实际飞出去的方向"；速度太小（几乎站着）就用体积方向，
        // 免得记录出一个抖动的零向量把后面 0.6s 的空中控制全废掉。
        if (speed >= _cfg.SlipMinSpeedToRecordDirection)
            _slipAirRestrictDir = horiz / speed;
        else if (_slipSample.Direction.LengthSq() > 1e-8f)
            _slipAirRestrictDir = _slipSample.Direction.Normalized();
        else
            _slipAirRestrictDir = F3.Zero;

        if (_slipAirRestrictDir.LengthSq() > 1e-8f)
            _slipAirRestrictTimer = _cfg.SlipAirRestrictDuration;

        _state = _onGround ? MovementState.Ground : MovementState.Air;
    }

    /// <summary>
    /// 打滑态推进：没有普通摩擦，只有体积给的"朝目标速度加速"。
    ///
    /// 与 Slide 的区别：Slide 是**玩家**蹲下触发的、速度只减不增；
    /// Slip 是**环境**推的、速度会被拉到体积的 slipSpeed。
    /// </summary>
    private void StepSlip(in MovementInput input, float dt)
    {
        F3 dir = _slipSample.Direction;

        // 体积没给方向（例如玩家正好站在触发球中心）→ 用玩家当前水平速度兜底，
        // 对应逆向里的 m_defaultSlipDirection：沿"已经被推出去的那个方向"继续滑。
        if (dir.LengthSq() < 1e-8f)
        {
            F3 horiz = F3.ProjectOnPlane(_vel, _up);
            float hs = horiz.Length();
            if (hs > _cfg.SlipMinSpeedToRecordDirection) dir = horiz / hs;
        }

        if (dir.LengthSq() < 1e-8f)
        {
            // 既没方向也没速度：站着不动就无所谓打滑，回普通状态即可。
            EndSlip();
            return;
        }
        dir = dir.Normalized();

        // ── 沿体积方向加速到 slipSpeed ──────────────────────────────
        float target = _slipSample.Speed;
        float along = F3.Dot(_vel, dir);
        float add = target - along;
        if (add > 0f)
        {
            float accelStep = _slipSample.Acceleration * dt;
            if (accelStep > add) accelStep = add;
            _vel += dir * accelStep;
        }

        // ── 极弱转向（滑道上还能勉强掰一点方向） ────────────────────
        if (_cfg.SlipSteerFactor > 0f)
        {
            F3 wishDir = WishDirection(input);
            if (wishDir.LengthSq() > 1e-6f)
            {
                float steerSpeed = WishSpeed(input) * _cfg.SlipSteerFactor;
                Accelerate(wishDir, steerSpeed, _cfg.GroundAccel * _cfg.SlipSteerFactor, dt);
            }
        }

        // ── 垂直方向 ────────────────────────────────────────────────
        if (_slipSample.LockToPlane)
        {
            // 滑槽式体积：只压掉**向上**的分量（别让球把人顶飞）。
            //
            // ★ 这里**不能**把垂直速度整体归零：v0.3 初版就是 `_vel -= up*dot`，
            //   结果空中进入滑区后每 tick 都被清零 ⇒ 永远落不下去、贴地探测永远打空，
            //   HUD 上表现为 `state=Slip` 但 `ground=False`、角色悬在离地 0.22m 处。
            //   向下的分量必须留给下面那段重力/落地处理。
            float upVel = F3.Dot(_vel, _up);
            if (upVel > 0f) _vel -= _up * upVel;
        }

        if (!_onGround)
        {
            _vel -= _up * (_cfg.Gravity * dt);
            float downward = -F3.Dot(_vel, _up);
            if (downward > _cfg.MaxFallSpeed)
                _vel += _up * (downward - _cfg.MaxFallSpeed);
        }

        if (_onGround)
            _vel = F3.ProjectOnPlane(_vel, _groundNormal);

        // ── 玩家主动行为优先 ────────────────────────────────────────
        // 滑区里仍然允许：蹲下滑铲（把环境推挤转成玩家可控的滑铲）、跳出去。
        //
        // ★ v0.4.2：这里**必须**带 _onGround。改前是 BeginSlide(input, fromAir: !_onGround)
        //   —— 于是"在空中经过滑区 + 按住蹲"会凭空得到一个空中 Slide 状态，
        //   正是需求方报的「在空中保持滑铲姿态」的最后一个入口。
        //   StepSlide 下一 tick 虽然会把它打回 Air，但那一 tick 里玩家已经吃到
        //   滑铲的贴地投影与滑铲跳分支 —— 就是"空中虚拟平台"的来源。
        if (_onGround && input.CrouchHeld && GroundPlaneSpeed >= _cfg.SlideMinStartSpeed)
        {
            BeginSlide(input);
            return;
        }

        if (_jumpBuffered)
        {
            DoJump(fromSlide: false, crouchHeld: input.CrouchHeld);
            return;
        }
    }

    // ───────────────────────── 滑铲 ─────────────────────────

    /// <summary>
    /// 进入滑铲。**调用方必须保证玩家在地面上**（<see cref="MovementState.Slide"/>
    /// 自 v0.4.2 起是**纯地面状态**）。
    ///
    /// 签名里原来有一个 <c>bool fromAir</c> 参数，那是"空中滑铲"还存在的时代的遗留；
    /// 空中滑铲被删除后它恒为 false，于是 v0.4.2 直接把它删掉 ——
    /// **让"在空中有没有办法起滑铲"这件事在类型层面就不成立**，
    /// 而不是靠调用方记得传 false（StepSlip 就是忘了传、留了最后一个空中入口）。
    /// </summary>
    private void BeginSlide(in MovementInput input)
    {
        _state = MovementState.Slide;
        _slideTime = 0f;

        // ── v0.5（需求 m01202）：入口判定与 boost 彻底分离 ──────────────
        //
        //   ① 入口判定用的是**滑铲平面内**的速度（速度投影到地面平面后的模长），
        //      这在调用方（StepGround / 落地 tick）已经做过了 —— 能走到这里就说明
        //      平面速度 ≥ SlideMinStartSpeed。这里只把它记下来供调试/自检。
        //   ② 进入之后**才**加 boost，而且 boost 要按坡度投影换算：
        //        boost_沿坡 = SlideBoostSpeed / cosθ
        //      （需求方例子：30° 坡、boost=√3 ⇒ 2.0 m/s）。
        //   ③ **不再有速度上限**（需求：「取消滑铲速度上限」）。
        F3 velBeforeSlideBoost = _vel;
        F3 planeVel = F3.ProjectOnPlane(_vel, _groundNormal);
        float planeSpeed = planeVel.Length();
        _slideEntrySpeed = planeSpeed;

        float slopeAngle = MathF.Acos(Clamp(F3.Dot(_groundNormal, _up), 0f, 1f));
        _slideBoostGranted = _slideBoostCooldown <= 0f;
        float mod = input.Holstered ? _cfg.HolsterMultiplier : 1f;
        float boost = _slideBoostGranted
            ? SlideBoostForSlope(_cfg.SlideBoostSpeed * mod, slopeAngle)
            : 0f;

        if (planeSpeed > 1e-4f) _vel = planeVel / planeSpeed * (planeSpeed + boost);
        else if (boost > 0f)
        {
            // 站着起滑（理论上门槛挡住了，兜底也保持方向确定）
            F3 fwd = F3.ProjectOnPlane(Forward(), _groundNormal);
            if (fwd.LengthSq() > 1e-8f) _vel = fwd.Normalized() * boost;
        }

        // 滑铲 boost 同样是瞬时冲量（不是加速度），单独记一笔。
        _accImpulse += (_vel - velBeforeSlideBoost) / _cfg.FixedDelta;

        if (_slideBoostGranted) _slideBoostCooldown = _cfg.SlideBoostCooldown;

        // 滑铲瞬间把胶囊拉到蹲姿（wiki：Sliding instantly pulls your Collision Pill down）
        // 注意：这里不瞬间改 _currentHeight，交给 UpdateCrouch 的滑铲强制 target=1 处理，
        // 避免"高度瞬间变化 + 中心平移"把角色塞进地面。
    }

    private void StepSlide(in MovementInput input, float dt)
    {
        // ★ v0.4.2：滑铲是**地面状态**。空中一律交回 Air 状态机。
        //
        // 这一条是"虚拟平台 / 无限连跳"的正面修复点：只要有一 tick 发现脚下没地
        // （滑出坡道尽头、被弹开、或历史上那条空中滑铲入口），就立刻退出滑铲，
        // 绝不在空中继续跑滑铲的贴地/滑铲跳逻辑。
        // 蹲姿体积不受影响 —— 那是 UpdateCrouch 按 CrouchHeld 独立维护的。
        if (!_onGround)
        {
            _state = MovementState.Air;
            _slideTime = 0f;
            _slideBoostGranted = false;
            return;
        }

        _slideTime += dt;

        // ── 摩擦 + 坡度重力（v0.4.1：滑铲改成真实物理）──────────────
        //
        // 沿坡面方向的合加速度：
        //     a = g·sinθ − μ·g·cosθ
        // 其中 θ = 坡面倾角（由 _groundNormal 与 up 的夹角给出）、μ = 地面摩擦系数。
        //
        //   · 平地 θ=0  ⇒ a = −μg（匀减速）
        //   · 越陡 sinθ 越大 ⇒ 下坡越滑越快（用户需求"坡道越陡越快"）
        //   · 临界角 tanθ = μ：超过它重力就再也压不住，一路加速到底
        //
        // μ 由脚下地面材质决定（IFrictionWorld），没实现就用配置默认 0.45。
        // 先加重力分量、再减库仑摩擦，于是"坡不够陡时摩擦能把重力分量吃掉"
        // 自动成立 —— 等价于静摩擦把人按在坡上，不需要额外分支。
        F3 wishDir = WishDirection(input);
        F3 horizVel = F3.ProjectOnPlane(_vel, _up);

        float mu = SlideFrictionAt();
        // 按 S（向后）主动制动：μ 乘 SlideReverseInputPenalty（wiki：反向输入减速非常剧烈）。
        //
        // ★ v0.5.2 改判据：原来用「速度方向 与 wishDir 点积 < −0.2」，而 A/D 横移
        //   也会让 wishDir 与速度夹角变大（斜按 45° 时点积 ≈ 0.7 不触发，但正后方
        //   A+S 一起按点积会掉到 −0.7）—— 结果"侧向微调"偶尔被当成"刹车"。
        //   现在只看**纯纵向输入**（MoveZ < −0.3），语义与需求一致：
        //   「按 s 则和之前一样会提供一些主动制动」。
        if (input.MoveZ < -0.3f)
        {
            mu *= _cfg.SlideReverseInputPenalty;
        }

        float cosT = Clamp(F3.Dot(_groundNormal, _up), 0f, 1f);
        float sinT = MathF.Sqrt(MathF.Max(0f, 1f - cosT * cosT));

        // ① 重力沿坡分量（方向 = 下坡）
        if (sinT > 1e-4f)
        {
            F3 downhill = F3.ProjectOnPlane(-_up, _groundNormal);
            if (downhill.LengthSq() > 1e-8f)
            {
                _vel += downhill.Normalized() * (_cfg.Gravity * sinT * dt);
            }
        }

        // ② 库仑摩擦：沿"坡面内的速度方向"反向，大小 μ·g·cosθ·dt
        //    按**坡面内**的速度算，不是三维速度 —— 否则上坡/下坡会把竖直分量算重。
        F3 slopeVel = F3.ProjectOnPlane(_vel, _groundNormal);
        float slopeSpeed = slopeVel.Length();
        if (slopeSpeed > 1e-5f)
        {
            float drop = mu * _cfg.Gravity * cosT * dt;
            float newSpeed = MathF.Max(0f, slopeSpeed - drop);

            // ★ v0.5（需求 m01202）：「基础蹲走移速是保证滑铲被减速后的最慢移动速度（保底）」
            //   摩擦不许把滑铲速度压到蹲走速度以下 —— 到了这个速度就交还地面状态机，
            //   由蹲走/蹲伏接管，而不是先掉到 1.8 再被拉回来（会看到一下顿挫）。
            float floorSpeed = _cfg.SlideExitSpeed;
            if (newSpeed < floorSpeed) newSpeed = MathF.Min(slopeSpeed, floorSpeed);

            F3 beforeF = _vel;
            _vel += slopeVel / slopeSpeed * (newSpeed - slopeSpeed);
            _accFriction += (_vel - beforeF) / dt;
        }

        // ── 滑铲时的输入（v0.5.2 / 需求 m01928 第 4 点）──────────────
        //
        // 需求：「滑铲时始终是滑动摩擦（最大静摩擦力）并且摩擦力与运动方向相反，
        // 所以滑铲的时候输入按键 w 前进方向不会产生作用，按 a/d 可以提供一点点的
        // 转向控制，按 s 则和之前一样会提供一些主动制动」。
        //
        // 为什么 W 不能有作用：滑铲时脚下是**滑动**，摩擦只沿运动反方向，
        // 没有任何"沿输入方向的推进会由静摩擦提供"的机制 —— 你想加速也无处着力。
        // 所以这里只保留 **横向**（垂直于当前滑行方向）的微调，前进分量直接砍掉。
        if (wishDir.LengthSq() > 1e-6f && slopeSpeed > 1e-4f)
        {
            F3 heading = slopeVel / slopeSpeed;              // 当前滑行方向（坡面内）
            F3 wish = F3.ProjectOnPlane(wishDir, _groundNormal);
            if (wish.LengthSq() > 1e-8f)
            {
                wish = wish.Normalized();
                // 只取横向分量：forward 分量丢掉（W 无效），侧向 a/d 保留
                F3 lateral = wish - heading * F3.Dot(wish, heading);
                float lateralLen = lateral.Length();
                if (lateralLen > 1e-4f)
                {
                    F3 steerDir = lateral / lateralLen;
                    float steerMag = MathF.Min(1f, lateralLen);   // 纯横移=1，斜按更小
                    float steerAccel = _cfg.GroundAccel * _cfg.SlideSteerFactor * steerMag;

                    // 转向不加速：先加侧向速度，再把总速度**投影回原大小**
                    // （滑铲时能量只减不增，转向不该白送速度）
                    F3 beforeI = _vel;
                    _vel += steerDir * (steerAccel * dt);

                    F3 newSlope = F3.ProjectOnPlane(_vel, _groundNormal);
                    float ns = newSlope.Length();
                    if (ns > 1e-5f && ns > slopeSpeed)
                        _vel += (newSlope / ns * slopeSpeed - newSlope);

                    _accInput += (_vel - beforeI) / dt;
                }
            }
        }


        // 贴地
        _vel = F3.ProjectOnPlane(_vel, _groundNormal);

        // ── 结束条件（v0.5 / 需求 m01202）──────────────────────────
        //
        //   · 速度降到**蹲走基础移速**（SlideExitSpeed = CrouchWalkSpeed）就交还地面状态机：
        //     仍按 Ctrl ⇒ 蹲伏；Ctrl+W ⇒ 蹲走。滑铲不会突然掉到 0。
        //   · **没有时间上限**了（需求：「由于坡度较大可以一直保持滑铲姿态」）——
        //     陡坡上重力分量一直大于摩擦，本来就能无限滑下去；
        //     平地靠库仑摩擦自然会减速到蹲走速度，不需要用计时器兜底。
        //   · 松开 Ctrl 立刻结束（原行为）。
        bool tooSlow = slopeSpeed <= _cfg.SlideExitSpeed;
        bool releasedCrouch = !input.CrouchHeld;

        if (tooSlow || releasedCrouch)
        {
            EndSlide();
            return;
        }

        // ── 滑铲跳 / 滑铲跳链 ─────────────────────────────────────
        if (_jumpBuffered)
        {
            // Apex 真实规则：**满足任一**才把跳跃初速叠加到滑铲速度上
            //   A) 当前速度 < 350hu/s(8.89 m/s)     B) 已滑 ≥ 0.24s
            bool slideJumpAllowed =
                HorizontalSpeed < _cfg.SlideJumpSpeedThreshold ||
                _slideTime >= _cfg.SlideJumpTimeWindow;

            F3 horiz = F3.ProjectOnPlane(_vel, _up);
            float speed = horiz.Length();
            if (speed > 1e-4f)
            {
                // 允许 → 完整保留滑铲速度；不允许（Deadslide）→ 速度掉回冲刺档
                float keep = slideJumpAllowed
                    ? speed * _cfg.SlideJumpSpeedRetention
                    : MathF.Min(speed, _cfg.SprintSpeed);
                _vel = horiz / speed * keep;
            }

            DoJump(fromSlide: slideJumpAllowed, crouchHeld: input.CrouchHeld);
            return;
        }
    }

    /// <summary>
    /// 取当前脚下地面的库仑摩擦系数 μ。
    /// 世界实现了 <see cref="IFrictionWorld"/> 就用材质值，否则用配置默认值。
    /// </summary>
    private float SlideFrictionAt()
    {
        if (_world is IFrictionWorld fw &&
            fw.SampleFriction(FeetPosition + _up * 0.05f, out float materialMu) &&
            materialMu >= 0f)
        {
            return materialMu;
        }
        return _cfg.SlideFrictionCoefficient;
    }

    /// <summary>
    /// **按坡度投影换算滑铲 boost** (m/s)。
    ///
    /// v0.5 需求（m01202）：「基于滑铲平面角度添加 boost 速度（例如 30° 斜坡，
    /// boost 如果是 √3 m/s，那基于角度会得到 30° 斜坡的 boost 为 2 m/s，这是基于投影的）」。
    ///
    /// <code>
    ///   boost_沿坡 = boost_水平 / cosθ
    ///   30°、√3 ⇒ √3 / cos30° = 2.0 ✓（与需求方给的例子逐位一致）
    /// </code>
    ///
    /// 为什么是除以：boost 是按"水平面推进量"标定的；斜面上要实现同样的水平推进，
    /// 沿坡面必须走更长的距离，所以沿坡速度更大。
    ///
    /// 夹紧：θ ≥ 60° 时按 60° 算（1/cos 在接近竖直时会发散，而滑铲本来也滑不上 60° 的坡）。
    /// </summary>
    public static float SlideBoostForSlope(float boostFlat, float slopeAngleRad)
    {
        float c = MathF.Cos(slopeAngleRad);
        if (c < 0.5f) c = 0.5f;
        return boostFlat / c;
    }

    /// <summary>
    /// 沿坡滑动的合加速度 (m/s²)：<c>a = g·sinθ − μ·g·cosθ</c>。
    /// 正值 = 沿坡加速（坡太陡，摩擦压不住），负值 = 减速。
    /// 纯函数，自检直接验它（不需要真的造一个斜坡世界）。
    /// </summary>
    public static float SlideSlopeAcceleration(float gravity, float slopeAngleRad, float mu)
    {
        float c = MathF.Cos(slopeAngleRad);
        float s = MathF.Sin(slopeAngleRad);
        return gravity * s - mu * gravity * c;
    }

    private void EndSlide()
    {
        // 脚下没地就是空中（滑出坡道尽头 / 从台面滑下去）——
        // v0.4.2 之前这里无条件写 Ground，于是人在空中却跑地面状态机，
        // 表现就是"空中像有虚拟平台"。
        _state = _onGround ? MovementState.Ground : MovementState.Air;
        _slideTime = 0f;
        _slideBoostGranted = false;
    }

    // ───────────────────────── 挂边 / 扒墙 ─────────────────────────

    private void StepHang(in MovementInput input, float dt)
    {
        // 挂边（Mantle hang / 扒墙）：停在边缘不动，等玩家按前进继续上拉，或按蹲/后退取消。
        //
        // v0.3 起这是**两条路都能到达**的状态：
        //   · 地面 ← 翻越探测（Step ⑥ → TryTraversal → EnableMantleHang）
        //   · 空中 ← 攀爬爬到墙顶（StepClimb → TryBeginClimbHangApproach → 这里）
        // 第二条路进来时 <see cref="_traversalEnd"/> 由 EnterHangFromClimb 预先算好，
        // 所以"按 W 上墙"直接复用 v0.2 的 Mantle pull。
        _vel = F3.Zero;

        if (input.CrouchHeld || input.MoveZ < -0.3f)
        {
            // 取消挂边 → 掉回空中
            _state = MovementState.Air;
            _traversalCooldown = _cfg.TraversalCooldown;
            _jumpFatigueState = false;
            _wasAirborne = true;
            _airPeakHeight = F3.Dot(_pos, _up);
            return;
        }

        if (input.MoveZ > 0.3f)
        {
            // 继续上拉
            _state = MovementState.Mantle;
            _mantleClass = _cfg.ClassifyMantle(
                F3.Dot(_traversalEnd, _up) - F3.Dot(_pos, _up) + _currentHeight * 0.5f);
            _mantlePhase = MantlePhase.Pull;

            // ★ v0.5.2：时间轴也**从 0 起**，并把总时长按"只剩 Pull 段"折算。
            //
            // 原来是把 _traversalTime 预置成 duration*grabFrac（"假装 Grab 段已经走完"）。
            // 但插值改成"全局缓动 + 折线弧长"以后，t=grabFrac 对应的是**已经走了
            // 28% 弧长**的位置 —— 于是第一帧仍然会跳（实测 0.5018 m）。
            // 现在：时间从 0 起、时长只剩 Pull 段、mid 就是起点（上面那行），
            // 三件事一起才让"按 W 上墙"的第一帧位移等于正常一帧。
            _traversalTime = 0f;
            _traversalDuration = MathF.Max(
                0.01f, _cfg.MantleDurationFor(_mantleClass) * (1f - _cfg.MantleGrabFraction));
            _traversalStart = _pos;

            // ★ v0.5.2 平滑修复（P0-1）：Pull 段必须从**当前位置**起步。
            //
            // 原来写的是 start/end 的中点，但 Grab 段已经被跳过（我们还站在 start），
            // 于是 Pull 第一帧直接把身体拉到中点 —— 3.0 m 墙实测单帧瞬移 0.5380 m
            // （等效 32.3 m/s），这就是"攀爬→翻越顿一下"里最大的一处。
            //
            // start→mid→end 三点共线，把 mid 设成 start 只是改**参数化**，
            // 轨迹几何完全不变（ClimbTest 50/50 通过）。
            _traversalMid = _pos;
        }

        _ = dt;
    }

    // ───────────────────────── 跳跃 ─────────────────────────

    private void DoJump(bool fromSlide, bool crouchHeld)
    {
        bool coyote = !_onGround && _coyoteTimer > 0f;

        // 标记"本次腾空来自起跳"（抓墙间隙门限豁免的依据，见 _leftGroundByJump）。
        _leftGroundByJump = true;

        _jumpBuffered = false;
        _jumpBufferTimer = 0f;
        _coyoteTimer = 0f;

        float jumpVel = _cfg.EffectiveJumpVelocity;

        // ── 跳跃疲劳 ──────────────────────────────────────────────
        // wiki：落地后 0.15s 内再跳，高度被压到 30%，线性回升到 0.75s 恢复满高。
        // 高度 ∝ v² ⇒ 速度系数 = sqrt(高度系数)。
        if (_jumpFatigueState && !coyote)
        {
            float frac = JumpHeightFraction();
            jumpVel *= MathF.Sqrt(frac);
        }
        // 土狼跳会**关闭**疲劳状态（wiki：Jump fatigue deactivates when Coyote time conditions are met）
        if (coyote) _jumpFatigueState = false;

        // ── 滑铲跳 / 兔子跳 计数 ──────────────────────────────────
        // slidehop = 按住蹲的 bunnyhop（wiki：A Bunnyhop while holding Crouch to Slide）
        bool isSlideHop = fromSlide ||
                          (crouchHeld && _timeSinceLanded <= _cfg.SlideHopWindowAfterLanding);
        if (isSlideHop) _slideHopCount++;

        // ── Bunnyhop 减速 ─────────────────────────────────────────
        // wiki：落地后 1s 内离地算 bhop；若速度高于冲刺速度，每次扣最多 100hu/s。
        // 逆向 `jump_grace_cutoff_speed` 语义：超过 cutoff 速度就不再扣。
        if (_timeSinceLanded <= _cfg.BunnyHopWindow)
        {
            if (!isSlideHop) _bunnyHopCount++;

            float hs = HorizontalSpeed;
            if (hs > _cfg.SprintSpeed && hs < _cfg.JumpGraceCutoffSpeed)
            {
                F3 horiz = F3.ProjectOnPlane(_vel, _up);
                float reduced = MathF.Max(_cfg.SprintSpeed, hs - _cfg.BunnyHopSpeedPenalty);
                if (hs > 1e-4f) _vel = horiz / hs * reduced;
            }
        }

        // 保留水平分量，替换竖直分量
        F3 h = F3.ProjectOnPlane(_vel, _up);

        // 空中速度增益的基准 = 离地瞬间的水平速度（见 StepAir 的硬上限）
        _airEntrySpeed = h.Length();

        F3 velBeforeJump = _vel;

        // ── 起跳方式：瞬时冲量 / 有限加速（见 MovementConfig.JumpAccelTime）──
        //
        // 默认（JumpAccelTicks = 0）走冲量分支，与 v0.4.2 逐位一致。
        //
        // 加速分支：起跳 tick 先给**一个 tick 的推力位移速度**，保证本 tick 就离开地面
        // （否则 vy=0 会被 UpdateGroundContact 当成"还在地面"而把跳跃吃掉），
        // 剩下的 tick 由 StepAir 每 tick 继续加推力、重力照常扣。
        int rampTicks = _cfg.JumpAccelTicks;
        if (rampTicks > 0)
        {
            // 推力 a = u/T + g：u 是加速段出口速度，T 是加速段总时长，
            // 多出来的 +g 用来抵消加速段里同时作用的重力。
            // 离散补偿：起跳 tick 先给推力、不扣重力，所以整个加速段少扣了一次 g·dt。
            // 把推力降 g/n 之后，第 n tick 结束时速度逐位等于 jumpVel。
            _jumpRampThrust = jumpVel / _cfg.JumpAccelTimeActual
                            + _cfg.Gravity * (1f - 1f / rampTicks);
            _jumpRampTicksLeft = rampTicks - 1;   // 本 tick 已经手动加了一次
            _vel = h + _up * (_jumpRampThrust * _cfg.FixedDelta);
        }
        else
        {
            _jumpRampTicksLeft = 0;
            _jumpRampThrust = 0f;
            _vel = h + _up * jumpVel;
        }

        // 起跳是一次**瞬时冲量**：本 tick 的速度突变全部记到 Impulse 上。
        _accImpulse += (_vel - velBeforeJump) / _cfg.FixedDelta;

        _state = MovementState.Air;
        _onGround = false;
        _justJumped = true;
        _jumpFatigueState = true;
        _slideTime = 0f;
        _slideBoostGranted = false;

        // 开始记录下落高度
        _airPeakHeight = F3.Dot(_pos, _up);
        _wasAirborne = true;
    }

    /// <summary>跳跃疲劳当前允许的高度比例（0.30 → 1.0 线性）。</summary>
    private float JumpHeightFraction()
    {
        float t = _timeSinceLanded;
        if (t <= _cfg.JumpFatigueMinTime) return _cfg.JumpFatigueHeightFrac;
        if (t >= _cfg.JumpFatigueMaxTime) return 1f;
        float k = (t - _cfg.JumpFatigueMinTime) /
                  MathF.Max(1e-4f, _cfg.JumpFatigueMaxTime - _cfg.JumpFatigueMinTime);
        return _cfg.JumpFatigueHeightFrac + (1f - _cfg.JumpFatigueHeightFrac) * k;
    }

    // ───────────────────────── 贴墙：抓住 / 上爬 / 扒着（v0.4 统一状态机） ─────────────────────────
    //
    // ## 需求方口径（m02704 第 2、3 条，逐条对应实现）
    //
    //   第 2 条「贴墙按 W 不该开始攀爬」
    //     · 地面上一律不抓（`_onGround` 直接拒）⇒ 按 W 只会撞墙停住。
    //     · 只有「W 贴墙 + Space 跳起」才会抓：空中 + 按着 W 才允许附着。
    //
    //   第 3 条「扒墙默认化」
    //     · 下落时贴墙**自动**抓住，不需要任何按键（`descending` 分支）。
    //     · 抓住之后：按 W = 一直往上爬；松开 W = 停在墙上（扒墙态）。
    //     · 完全无输入超过 WallHangIdleFallTime ⇒ 掉落。
    //     · A/D 横移、W+A/W+D 斜移、S 立刻垂直下落、下落中按 W 可再抓住。
    //
    // ## 为什么把 v0.3 的两套状态机合成一套
    //
    //   v0.3 有 Climb（朝墙动量 + 空中）与 WallHang（按住 Ctrl）两个独立入口，
    //   于是"贴墙按 W"命中 Climb 的朝墙动量条件就直接吸上去 —— 正是需求方反馈的
    //   问题。而"按住 W 才上爬 / 松 W 就扒着"本来就只是**同一个挂墙状态下的两个
    //   输入分支**，拆成两套只会带来两套不一致的脱离规则。v0.4 统一为：
    //   抓住 → (按 W ? Climb 上爬 : WallHang 扒着)。
    //
    // ## 保留的逆向依据（A 级字段名，仅作命名对照，不再是行为约束）
    //
    //   m_wallHanging / m_wallHangTime / IsWallHanging
    //   m_wallDangleClimbProgressFloor            ← "还能爬升的进度下限"（本实现的握力）
    //   ACT_MP_WALLHANG_FRONT/UP/RIGHT/LEFT/IDLE  ← 5 个姿势 = 可上下左右移动
    //   sound_wallHangStart / Slip / Complete / Fall
    //   automantle_disable_hang                   ← 能直接翻过去的矮墙不该先挂上去

    /// <summary>
    /// 贴墙抓住判定（v0.4 统一入口）。返回 true 表示本 tick 已切到挂墙状态。
    ///
    /// 判定 = 「空中 ∧ 够得到陡墙 ∧ 视角朝向墙面 ∧ (正在下落 ∨ 按着 W)」。
    ///
    /// 注意这里**不再**要求 v0.3 的"朝墙动量"（<c>ClimbMinApproachSpeed</c>）：
    /// 贴着墙按 W 起跳时，水平速度在起跳那一 tick 就被墙吃掉，朝墙动量几乎为 0，
    /// 那个门槛反而把"跳起来该抓"的情况拒掉了。
    /// </summary>
    private bool TryAttachWallGrab(in MovementInput input)
    {
        if (!_cfg.WallHangEnabled) return false;
        if (_world == null) return false;

        // ① 必须离地。地面上的"贴墙按 W"要撞停，不是抓墙（需求第 2 条）。
        if (_onGround) return false;
        // v0.4.2：Slide 已是纯地面状态，空中不可能是 Slide —— 允许集合收窄到 Air。
        if (_state != MovementState.Air) return false;

        // ①b 脚底下就有地面 ⇒ 不抓（这是"站在墙根/刚落地"，不是"挂在墙上"）。
        //
        // 扒墙默认化之后这条是必需的：离开地面那一 tick `_onGround` 还是 false，
        // 而"空中 + 够得到墙 + 正在下落"三个条件全部满足 —— 于是贴着墙走下一级
        // 小台阶的人会被凭空吸到墙上去。加一条脚下探针把"脚下是地面"排除掉。
        //   ★ v0.5（用户 m01300 BUG 2）：**跳出来的腾空豁免这条门**。
        //     需求原话「我希望改成只要跳了就攀爬」—— 跳跃高度不该参与"能不能抓墙"，
        //     否则跳跃疲劳（0.23 m）和超重力星球（跳得更低）都会整块失效。
        //     走下台沿/被推落那种腾空没有起跳动作，仍然要过这道门
        //     （防"贴着墙走下小台阶被凭空吸到墙上"）。
        if (!_leftGroundByJump && _cfg.WallGrabMinGroundClearance > 0f)
        {
            F3 feetNow = _pos - _up * (_currentHeight * 0.5f);
            if (_world.Raycast(feetNow, -_up, _cfg.WallGrabMinGroundClearance,
                               out _, out F3 belowN) &&
                F3.Dot(belowN, _up) >= _cfg.WalkableNormalDot)
                return false;
        }

        // 刚从地面/边缘离地的一小段不抓（留出起跳高度）。
        if (_coyoteTimer > 0f) return false;
        if (_wallHangCooldown > 0f) return false;

        // ② 输入门：下落自动抓；上升必须按着 W。
        bool pressingW = input.MoveZ > 0.3f;
        bool pressingS = input.MoveZ < -0.3f;
        bool descending = VerticalSpeed < -0.1f;

        // 脱手后"必须按 W 才能再抓"的门闩（见 DetachWallGrab）。
        // 没有它，「按 S 掉落 → 下落中自动扒住」会立刻把人吸回墙上。
        if (_wallGrabNeedW && !pressingW) return false;
        if (!descending && !pressingW) return false;

        // ②b 反向输入（按着 S）直接拒绝（v0.4.2 修「倒退坠下高台被边缘吸住」的补充闸）。
        //
        // ④b 用**已经形成**的水平速度判定"离墙/朝墙"，但从静止开始按 S 时，
        // 速度要几 tick 才建立起"背离墙面"的分量；那几 tick 里 approach ≈ 0，
        // ④b 会放行 —— 于是"站在高台边缘按 S 刚掉出去"的瞬间仍可能被抓一下。
        // 需求口径是「只有玩家**面向高台前进**的时候才会触发攀爬」，
        // 所以把"明确的后退意图"也作为拒绝条件：按 S 就是"我要往下掉"，不是"我要抓"。
        if (pressingS) return false;

        F3 fwd = Forward();
        float reach = _cfg.CapsuleRadius + MathF.Max(_cfg.WallHangReach, _cfg.TraversalReach);

        if (!_world.Raycast(_pos, fwd, reach, out F3 hitPoint, out F3 hitNormal))
        {
            // 视野里已经没有墙 ⇒ 确实离开了墙面，解除"必须按 W 才能再抓"的门闩。
            _wallGrabNeedW = false;
            return false;
        }

        // ③ 必须是陡墙（法线与 up 的 |点积| ≤ WalkableNormalDot ⇔ 墙面陡于 45.57°）
        if (MathF.Abs(F3.Dot(hitNormal, _up)) > _cfg.WalkableNormalDot) return false;

        // ④ 视角容差（与攀爬同源 = arccos(0.7)）
        float facing = -F3.Dot(hitNormal, fwd);
        float cosLimit = MathF.Cos(_cfg.WallHangMaxViewAngle * MathF.PI / 180f);
        if (facing < cosLimit) return false;

        // ④b 必须是「朝墙去」，而不是「离墙去」（v0.4.2 修「倒退坠下高台被边缘吸住」）
        //
        // 症状：站在高台上按住 S 倒退着掉下去，人会贴在台子的侧面上挂一小会儿
        //       （WallHangIdleFallTime ≈1.2 s）再掉下来。
        // 根因：倒退时**前向射线恰好打在刚离开的那块台的侧面上** —— 人已经越过
        //       边缘、台子在身后，可角色的朝向没变，于是台子落在"正前方"；
        //       旧判定只看「面向墙 + 正在下落」两条，于是无条件抓住。
        // 需求方口径：「只有玩家面向高台前进的时候才会触发攀爬，而不是任何方向都触发」。
        //
        // 判据用**水平速度在墙面法线上的分量**：
        //   approach > 0 ⇒ 正在靠近墙（正常前进/贴墙跳）
        //   approach < 0 ⇒ 正在远离墙（倒退、被推开、被弹开）
        //   approach ≈ 0 ⇒ 垂直下落（水平分量≈0）
        // 按着 W 时无条件放行（W 就是"我要往前"的明确意图）；
        // 否则远离墙超过 WallGrabMaxRecedeSpeed 就拒绝。于是：
        //   · 倒退坠台（水平速度背离墙面 ≈3 m/s）⇒ 拒绝 ✔
        //   · 跳跃/下落贴墙、不按任何键 ⇒ approach≈0 ⇒ 仍然自动抓住 ✔
        if (!pressingW)
        {
            F3 horizVel = F3.ProjectOnPlane(_vel, _up);
            float approach = -F3.Dot(horizVel, hitNormal);
            if (approach < -_cfg.WallGrabMaxRecedeSpeed) return false;
        }

        // ⑤ 太矮的障碍不挂（对应 automantle_disable_hang 的意图）：
        //    "脚底往上 0.6 × 小障碍档高度"那一层打不到墙 ⇒ 它矮到该迈过去/翻过去。
        //
        //    ★ v0.4 两处修正：
        //    1) v0.3 探的是 _pos + AutoMantleMaxHeight(2.03m)，于是 1.0…2.03 m 之间的
        //       障碍既翻不过去（> VaultMaxHeight）又挂不住 —— 玩家撞上去只能干瞪眼。
        //       按需求方口径「>1m 就是墙，要 W+Space 攀爬+翻越」，门槛降到小障碍档。
        //    2) 探针高度必须相对**脚底**而不是胶囊中心：跳跃贴墙那一刻脚底才 0.03 m 高，
        //       按中心算会平白多探 1 m。取 0.6 倍而不是 1 倍，是为了给 1.5 m 这种
        //       "刚过小障碍档"的墙留出判定余量（0.6 m 处命中 ⇒ 它高过脚底 0.6 m ⇒ 算墙）。
        if (_cfg.WallHangDisableWhenAutoMantleable)
        {
            F3 highOrigin = FeetPosition + _up * (_cfg.VaultMaxHeight * 0.6f);
            if (!_world.Raycast(highOrigin, fwd, reach, out _, out _)) return false;
        }

        // ⑥ 重附着规则（wiki L172-175）：脱离后必须先掉回上次 attach point 以下；
        //    换了面（法线夹角 ≥ 25.842°）则豁免。
        float here = F3.Dot(_pos, _up);
        if (_hasClimbed && here > _lastClimbAttachY)
        {
            float cosSep = MathF.Cos(_cfg.ClimbReattachNormalAngle * MathF.PI / 180f);
            bool sameFace = _lastClimbWallNormal.LengthSq() > 1e-8f &&
                            F3.Dot(hitNormal, _lastClimbWallNormal) > cosSep;
            if (sameFace) return false;
        }

        // ── 附着成立 ──
        _wallHangNormal = hitNormal;
        _wallHangLastHit = hitPoint;
        _climbWallNormal = hitNormal;
        _climbLastWallHit = hitPoint;

        _wallHangTime = 0f;
        _wallHangSlip = 0f;
        _wallHangGrip = 1f;
        _climbTime = 0f;
        _climbNoInputTimer = 0f;
        _climbApproaching = false;
        _climbApproachRemaining = 0f;
        _climbBaseY = here;
        _climbAttachHeight = here;

        // 把身体贴到墙面上（半径 + 一点皮肤）。
        // 注意方向：`dot(_pos - hitPoint, normal)` 才是"玩家到墙面的正距离"
        // （法线朝外，玩家在墙外时为正）。v0.3 初版写成 `dot(hitPoint - _pos, ...)`，
        // 结果恒为负 ⇒ 这行贴墙从来没生效过，玩家停在离墙 1m 处，下一 tick 的
        // 到顶探针（射程 0.7）必然打空 ⇒ 秒判 wallGone ⇒ 直接吃 End boost 弹开。
        float dist = F3.Dot(_pos - hitPoint, hitNormal);
        float want = _cfg.CapsuleRadius + 0.02f;

        // ⚠ v0.4.3 物理审计 P0-3（**已知不物理，故意保留**）：
        //   这一行把身体一次贴到墙面上，单 tick 位移可达 0.88 m
        //   （探测射程 1.3064 − 目标 0.4264），等效 52.8 m/s 的位置突变。
        //   试过把它摊成"每 tick 按 4 m/s 收拢"，结果墙态探针（到顶探针、
        //   墙面重探、横移射线）全都假设**已经贴住墙**，人离开墙面 0.3 m 后
        //   探针打空 ⇒ 立刻判 wallGone 脱手：ClimbTest 47 项里 10 项红、
        //   integration 4 项红。要做成物理连续，必须把整套墙态探针改成
        //   "允许离墙距离"的形式，属于独立改造，不在本次范围内。
        //   结论：保留 Apex 的瞬移吸附（见 README 物理审计表 T 项）。
        _pos += hitNormal * (want - dist);

        // 抓住墙的一瞬间动量清零（手已经"挂"住了）。
        _vel = F3.Zero;

        // 按着 W 就直接进"上爬"分支；不按 W 就是扒着不动。
        _state = pressingW ? MovementState.Climb : MovementState.WallHang;

        _wasAirborne = false;
        _jumpBuffered = false;
        _jumpBufferTimer = 0f;
        _wallGrabNeedW = false;
        return true;
    }
    /// <summary>
    /// 贴墙态推进（Climb 上爬 / WallHang 扒着，**同一个**状态机的两个分支）。
    ///
    /// 位置是运动学积分（同 <see cref="StepTraversal"/>），所以 <see cref="Step"/>
    /// 里是独占分支、不走 MoveWithCollision —— 因此这里必须自己改 `_pos`。
    ///
    /// 输入映射（逐条对应需求方口径）：
    ///   W           → 向上爬（ClimbSpeed 2.0 m/s），状态切 Climb
    ///   W + A / D   → 左上 / 右上斜移
    ///   A / D       → 沿墙左右横移（WallHangShimmySpeed），状态留在 WallHang
    ///   什么都不按  → 停在墙上缓慢下滑，WallHangIdleFallTime 后掉落
    ///   S           → 立刻松手垂直下落，下落中按 W 可以再抓住
    ///   Space       → 翻上墙沿；翻不上去就蹬墙跳
    ///   视角甩开    → 松手
    /// </summary>
    private void StepWallGrab(in MovementInput input, float dt)
    {
        _wallHangTime += dt;
        _climbTime += dt;

        // 墙面法线只保留一份（_wallHangNormal），_climbWallNormal 始终跟随 ——
        // 下游的 TryFindClimbLedge / EnterHangFromClimb 读的是 _climbWallNormal。
        _climbWallNormal = _wallHangNormal;
        _climbLastWallHit = _wallHangLastHit;

        // ── ① 跳跃：优先翻上墙沿，翻不上去就蹬墙跳 ────────────────
        if (_jumpBuffered)
        {
            if (!TryCompleteWallGrab()) DetachWallGrab(jumped: true);
            return;
        }

        // ── ② S：立刻松手垂直下落 ────────────────────────────────
        //    需求：「只要按了 s，就会取消扒墙直接垂直下落，
        //    下落的过程中按 w 可以继续扒住墙」。
        //    走 drop 分支（速度清零让重力接管）+ 打开 `_wallGrabNeedW` 门闩，
        //    否则"还在下落 + 人还贴着墙"会被自动抓取分支立刻吸回去。
        if (input.MoveZ < -0.3f)
        {
            DetachWallGrab(jumped: false, drop: true);
            return;
        }

        // ── ③ 视角甩开太大 → 松手（与附着同一容差 = arccos(0.7)） ──
        F3 fwd = Forward();
        float facing = -F3.Dot(_wallHangNormal, fwd);
        float cosLimit = MathF.Cos(_cfg.WallHangMaxViewAngle * MathF.PI / 180f);
        if (facing < cosLimit)
        {
            DetachWallGrab(jumped: false, drop: true);
            return;
        }

        // ── ④ 补爬阶段（已经决定要翻上去：只走竖直位移） ──────────
        //    这段不做脱离判定，否则"补爬的一半"会被墙顶那条探针打断。
        if (_climbApproaching)
        {
            StepClimbApproach(dt);
            return;
        }

        // ── ⑤ 墙还在吗 / 到顶了吗 ────────────────────────────────
        //
        // ★ 探针按 ClimbWallProbeInterval 抽样（默认 2 ⇒ 60 tick/s 下 30 Hz）。
        //   这条射线是**整段挂墙期间每 tick** 都要打的持续开销（一次 Raycast），
        //   而墙顶不可能在一个 tick 内消失 —— 抽样对玩法零影响，开销直接砍半。
        bool wallGone = false;
        F3 wallHit = _climbLastWallHit;

        if (ShouldProbeClimbWall())
        {
            _climbWallProbeCount++;
            wallGone =
                !_world!.Raycast(_pos, -_wallHangNormal, _cfg.CapsuleRadius + 0.35f,
                                 out F3 probedHit, out F3 n2) ||
                MathF.Abs(F3.Dot(n2, _up)) > _cfg.WalkableNormalDot;

            if (!wallGone)
            {
                _climbLastWallHit = probedHit;
                _wallHangLastHit = probedHit;
                wallHit = probedHit;
                _wallHangNormal = n2;
                _climbWallNormal = n2;
            }
        }

        // 两个高度上限"谁先到谁生效"：attach offset（个人体力）与 climb space cutoff（墙高）
        float climbed = F3.Dot(_pos, _up) - _climbAttachHeight;
        bool overLimit = climbed >= _cfg.ClimbAttachOffset ||
                         F3.Dot(_pos, _up) - _climbBaseY >= _cfg.MantleMaxHeight;

        if (wallGone || overLimit)
        {
            // 上方有可站立台面 ⇒ 先补爬一小段再进 Hang（需求「继续上爬一小部分进入扒墙状态」）
            //
            // ★ v0.5.2 平滑修复（P0-3）：**只有"真的到顶"(wallGone) 才允许补爬**。
            //
            //   原因：TryFindClimbLedge 允许选到"比脚底高 2.75 m"的沿，于是 4~5.4 m 的墙
            //   会同时满足 overLimit 与"沿在扫描窗内"—— 一次抓取里凭空多爬 1~2 m，
            //   既造成 P0-2 那个瞬移，也违反"单段爬升 ≤ ClimbAttachOffset"的不变量。
            //   爬升到上限（overLimit）时一律走 End boost 上抛 → 落回 → 重抓 → 再爬，
            //   与现有 6.0 / 7.5 m 墙完全同一条路。
            if (wallGone && TryBeginClimbHangApproach())
            {
                // 同一帧就把补爬的第一步走出去，别空转一帧（P1-1 的同类问题）
                StepClimbApproach(dt);
                return;
            }

            // 上方什么都没有 / 已达到单段爬升上限 ⇒ End boost 上抛脱离
            DetachWallGrabAtTop();
            return;
        }

        // ── ⑥ 输入解析 ────────────────────────────────────────────
        bool pressingW = input.MoveZ > 0.3f;
        bool pressingLateral = MathF.Abs(input.MoveX) > 0.3f;
        bool anyInput = pressingW || pressingLateral;

        // ── ⑦ 沿墙移动 ────────────────────────────────────────────
        //   按 W ⇒ 向上爬；不按 W ⇒ 缓慢下滑（留在墙上，不立刻掉）。
        // 上爬用 ClimbSpeed（v0.3 起就是 2.0 m/s，README 与集成测试都以它为准）；
        // 松手后的缓慢下滑用 WallHangSlipSpeed。两者是**一个旋钮一个方向**，不要混。
        float vUp = pressingW ? _cfg.ClimbSpeed : -_cfg.WallHangSlipSpeed;
        float vSide = Clamp(input.MoveX, -1f, 1f) * _cfg.WallHangShimmySpeed;

        // ⑦b 下滑到底 ⇒ 直接落地。
        //
        // 贴墙态是运动学积分（不走 MoveWithCollision），所以"一路往下滑"会把
        // 胶囊压进地面：一旦压进去，后续 Sweep 从已重叠的位置起扫会返回
        // fraction=0，水平移动也被一起挡住（表现为"落地后走不动"）。
        // 这里在真的下滑之前先探一次脚下，够到地面就改成正常落地。
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

        F3 right = F3.Cross(fwd, _up).Normalized();
        _pos += _up * (vUp * dt) + right * (vSide * dt);

        // 状态随输入切换：按着 W 就是"在爬"，松开就是"扒着"。
        _state = pressingW ? MovementState.Climb : MovementState.WallHang;

        // ── ⑧ 握力（对应 m_wallDangleClimbProgressFloor） ─────────
        //
        //   · 按 W 上爬   ⇒ 把手再抓高一点，握力回满
        //   · 只横移      ⇒ 缓慢消耗（WallHangLateralFallTime）
        //   · 完全无输入  ⇒ 快速消耗（WallHangIdleFallTime）⇒ 掉落
        if (pressingW)
        {
            if (_cfg.WallHangClimbRestoresGrip)
            {
                _wallHangGrip = MathF.Min(1f, _wallHangGrip +
                    dt / MathF.Max(1e-3f, _cfg.WallHangGripRestoreTime));
            }
            _wallHangSlip = MathF.Max(0f, _wallHangSlip - _cfg.WallHangSlipSpeed * dt);
        }
        else if (anyInput)
        {
            _wallHangGrip -= dt / MathF.Max(1e-3f, _cfg.WallHangLateralFallTime);
            _wallHangSlip += _cfg.WallHangSlipSpeed * dt;
        }
        else
        {
            _wallHangGrip -= dt / MathF.Max(1e-3f, _cfg.WallHangIdleFallTime);
            _wallHangSlip += _cfg.WallHangSlipSpeed * dt;
        }

        if (_wallHangGrip <= 0f || _wallHangSlip >= _cfg.WallHangMaxSlipDistance)
        {
            DetachWallGrab(jumped: false, drop: true);
            return;
        }

        // ── ⑨ 把身体贴回墙面（半径 + 一点皮肤） ──────────────────
        float dist = F3.Dot(_pos - wallHit, _wallHangNormal);
        float want = _cfg.CapsuleRadius + 0.02f;
        if (dist > want) _pos -= _wallHangNormal * (dist - want);

        // 挂墙时对外速度恒为 0（HUD 上应看到 speed=0.00 / vy=0.00）
        _vel = F3.Zero;
    }

    /// <summary>
    /// 尝试进入"补爬 → 扒墙"。找到可站立台面就启动补爬阶段并返回 true；
    /// 没找到（或功能被关掉）返回 false，调用方回退到 End boost。
    /// </summary>
    private bool TryBeginClimbHangApproach()
    {
        if (!_cfg.ClimbToHangEnabled || !_cfg.EnableMantleHang) return false;
        if (_climbApproachRemaining > 0f) return true;   // 已经在补爬里，别重复启动

        if (!TryFindClimbLedge(out F3 ledgeTop)) return false;

        _climbHangTop = ledgeTop;

        // ★ v0.5.2 平滑修复（P0-2）：补爬距离改成"正好爬到挂姿"。
        //
        // 原来固定走 ClimbHangApproachDistance(0.35 m)，但 EnterHangFromClimb 会把
        // 脚底**硬吸附**到「台沿 − 0.25 m」。墙越高，两者差得越多：
        //   3.0 m 墙 ⇒ 单帧吸附 0.3138 m；5.0 m 墙 ⇒ **1.7805 m**（≈107 m/s）。
        // 现在补爬距离按实际差额算，吸附量自然接近 0（保留硬吸附作为兜底）。
        float hangFeetY = F3.Dot(ledgeTop, _up) - 0.25f;
        float curFeetY = F3.Dot(_pos, _up) - _currentHeight * 0.5f;
        _climbApproachRemaining = MathF.Max(_cfg.ClimbHangApproachDistance * 0.01f,
                                            hangFeetY - curFeetY);

        _climbApproaching = true;
        return true;
    }

    /// <summary>
    /// 从当前攀爬位置向上/向内找"能站人的台面"，用于扒墙。
    ///
    /// 与 <see cref="TryTraversal"/> 的内向扫描同源，但**不看视角、不看输入**：
    /// 攀爬已经贴在墙上了，只关心头顶上有没有沿。
    ///
    /// v0.3 修掉的三个几何缺陷：
    /// ① 不再从胸口向前打射线找墙面 —— 到顶那一刻它**必然已经打空**，
    ///    原来那样写会直接 return false，扒墙永远进不去；改用
    ///    <see cref="_climbLastWallHit"/>（墙还在时记下的真实命中点）作水平锚点。
    /// ② 台面高度改与**脚底**比较，而不是与胶囊中心比较：翻上去之后台沿本来就
    ///    低于胸口，和中心比会把唯一合法的沿全部筛掉。
    /// ③ 扫描起点抬到"头顶以上"，扫描长度覆盖到脚底以下，保证沿一定落在窗口内。
    /// </summary>
    private bool TryFindClimbLedge(out F3 ledgeTop)
    {
        ledgeTop = F3.Zero;
        if (_world == null) return false;

        F3 intoWall = -_climbWallNormal;

        // 水平锚点：墙还在时最后一次打到的墙面上那一点。
        // 没记录过（理论上不会）就退回"当前胶囊中心"当锚点。
        F3 anchor = _climbLastWallHit;
        if ((anchor - _pos).LengthSq() < 1e-8f)
            anchor = _pos + intoWall * _cfg.CapsuleRadius;

        float yPos = F3.Dot(_pos, _up);
        float feetY = yPos - _currentHeight * 0.5f;
        float headY = yPos + _currentHeight * 0.5f;

        // 从"头顶 + 0.6m"往下扫，扫到"脚底 - 0.4m"为止。
        float scanTopY = headY + 0.6f;
        float scanLen = (scanTopY - feetY) + 0.4f;

        // 内向偏移范围：从墙面往里走，覆盖"沿 + 一截可以站人的台面"。
        float inwardMax = _cfg.CapsuleRadius * 2f + 0.6f;

        bool found = false;
        float bestY = float.NegativeInfinity;
        F3 best = F3.Zero;

        const int samples = 6;
        for (int i = 0; i < samples; i++)
        {
            float f = samples == 1 ? 0.5f : i / (float)(samples - 1);

            // 起点 = 锚点抬到 scanTopY，再沿"向墙内"方向偏移。
            // 额外加一点 ε（0.15 个半径）把起点推进墙面之内：
            // 正好落在墙面上时向下射线是"擦边"情形，不同世界的边界语义不一致。
            F3 origin = anchor
                        + _up * (scanTopY - F3.Dot(anchor, _up))
                        + intoWall * (inwardMax * f + _cfg.CapsuleRadius * 0.15f);

            if (!_world.Raycast(origin, -_up, scanLen, out F3 hp, out F3 hn)) continue;
            if (F3.Dot(hn, _up) < _cfg.WalkableNormalDot) continue;   // 不是能站的面

            float y = F3.Dot(hp, _up);

            // 必须比**脚底**高（否则爬上去毫无意义），且不能高过"多爬一点也够不着"的位置。
            if (y <= feetY + 0.05f) continue;
            if (y > scanTopY + 0.25f) continue;

            if (y <= bestY) continue;

            bestY = y;
            best = hp;
            found = true;
        }

        if (!found) return false;

        ledgeTop = best;
        return true;
    }

    /// <summary>
    /// 补爬结束：挂到台沿上，进入 <see cref="MovementState.Hang"/>（扒墙）。
    ///
    /// 这里同时把 Mantle 的目标点/时长都准备好，于是 Hang 里按 W 就能直接
    /// 走 v0.2 已有的 Mantle pull 上墙 —— 不需要另写一套。
    /// </summary>
    private void EnterHangFromClimb()
    {
        F3 intoWall = -_climbWallNormal;

        // 挂姿：身体略低于台面，手在沿上。取"脚底低于台面 0.25m"。
        float feetY = F3.Dot(_climbHangTop, _up) - 0.25f;
        F3 hangPos = _pos;
        float curFeet = F3.Dot(_pos, _up) - _currentHeight * 0.5f;
        hangPos += _up * (feetY - curFeet);

        _pos = hangPos;
        _vel = F3.Zero;
        _state = MovementState.Hang;

        // 准备 Mantle pull 的目标（Hang 里按 W 直接用）。
        float topRel = F3.Dot(_climbHangTop, _up) - F3.Dot(hangPos, _up) + _currentHeight * 0.5f;
        _mantleClass = _cfg.ClassifyMantle(topRel);
        _mantlePhase = MantlePhase.Grab;
        _traversalDuration = _cfg.MantleDurationFor(_mantleClass);
        _traversalTime = 0f;
        _traversalStart = hangPos;
        // ⚠️ `_traversalEnd` 是**胶囊中心**的目标位置，不是台面上的一个点：
        //   台沿 + 向内一截（站上台面）之后，高度还要再抬半个身高，
        //   否则上拉结束时会有一半身体陷在台面里（v0.3 初稿就是漏了这一项）。
        _traversalEnd = _climbHangTop
                        + intoWall * _cfg.MantleForwardOffset
                        + _up * (_currentHeight * 0.5f);
        _traversalMid = F3.Lerp(hangPos, _traversalEnd, 0.5f);
        _isWindowTraversal = false;

        _climbApproaching = false;
        _climbApproachRemaining = 0f;
        _climbNoInputTimer = 0f;
        _climbWallNormal = F3.Zero;
        _wasAirborne = false;
    }

    /// <summary>
    /// 松手 / 脱离：回到空中。三种语义合成一个出口 ——
    ///
    /// <list type="bullet">
    ///   <item><paramref name="jumped"/>：蹬墙跳（离墙 + 向上）</item>
    ///   <item><paramref name="drop"/>：主动松手**垂直下落**（速度清零让重力接管）</item>
    ///   <item>两者都不给：普通脱离（保留水平速度）</item>
    /// </list>
    ///
    /// 所有路径都会打开 <see cref="_wallGrabNeedW"/> 门闩：脱离之后必须**按 W**
    /// 才能再抓住，否则"掉落 + 人还贴着墙"会被下落自动抓取立刻吸回墙上，
    /// 表现为「松手掉一下就又挂回去」，S 键等于没用。
    /// </summary>
    private void DetachWallGrab(bool jumped, bool drop = false)
    {
        RememberClimbSurface();

        _state = MovementState.Air;
        // 主动松手（S）用更短的再抓冷却：需求要的是「下落的过程中按 W 可以继续扒住墙」，
        // 0.35s 的通用冷却会让这个动作迟钝半拍。按 W 时 0.12s ≈ 两个 tick 就能抓回。
        _wallHangCooldown = drop ? 0.12f : _cfg.WallHangReattachCooldown;
        _wallHangTime = 0f;
        _wallHangSlip = 0f;
        _climbTime = 0f;
        _climbNoInputTimer = 0f;
        _climbApproaching = false;
        _climbApproachRemaining = 0f;
        _wasAirborne = true;
        _airPeakHeight = F3.Dot(_pos, _up);

        // 唯一解除点是 TryAttachWallGrab 里"前向射线打空"（人真的离开墙面了）。
        _wallGrabNeedW = true;

        if (jumped && _wallHangNormal.LengthSq() > 1e-8f)
        {
            // 蹬墙跳：离墙 + 向上
            _vel = _wallHangNormal * (_cfg.JumpVelocity * 0.35f) +
                   _up * (_cfg.JumpVelocity * 0.8f);
            _jumpBuffered = false;
            _jumpBufferTimer = 0f;
            _justJumped = true;
            _jumpFatigueState = true;
        }
        else if (drop)
        {
            // 垂直下落：速度清零，只留重力。
            _vel = F3.Zero;
            _jumpBuffered = false;
            _jumpBufferTimer = 0f;
            _jumpFatigueState = false;
        }
        else
        {
            _vel = F3.ProjectOnPlane(_vel, _up);
            _jumpFatigueState = false;
        }

        _wallHangNormal = F3.Zero;
        _climbWallNormal = F3.Zero;
        _wallHangLastHit = F3.Zero;
        _climbLastWallHit = F3.Zero;
    }

    /// <summary>
    /// 记录"上次攀爬的面"，供重附着规则使用（wiki L172-175）。
    /// 必须在清空 <see cref="_climbWallNormal"/> **之前**调用。
    /// </summary>
    private void RememberClimbSurface()
    {
        if (_climbWallNormal.LengthSq() > 1e-8f)
        {
            _hasClimbed = true;
            _lastClimbAttachY = _climbAttachHeight;
            _lastClimbWallNormal = _climbWallNormal;
        }
    }

    /// <summary>爬到顶但上方没有可挂的台面：给 End boost（+28hu）上抛脱离。</summary>
    private void DetachWallGrabAtTop()
    {
        // 注意：这里**故意不调 RememberClimbSurface()**。
        //
        // 到顶脱手不是"玩家放弃了这面墙"，而是"爬升距离到了 ClimbAttachOffset 的
        // 上限"——玩家还按着 W，应该能落回去继续爬（高墙分两段爬上去）。
        // 一旦记下 surface，重附着规则（"必须先掉回上次 attach 点以下"）就会把
        // 后续每一次重抓全部拒掉：实测 6 m 墙卡在 2.54 m 处上不去（v0.4.1）。
        // 反作弊规则只针对玩家主动脱手（S / 跳 / 甩视角），那些走 DetachWallGrab。
        _state = MovementState.Air;
        _vel = _up * MathF.Sqrt(2f * _cfg.Gravity * _cfg.ClimbEndBoost);
        _climbTime = 0f;
        _climbNoInputTimer = 0f;
        _climbApproaching = false;
        _climbApproachRemaining = 0f;
        _climbWallNormal = F3.Zero;
        _wallHangNormal = F3.Zero;
        _wallHangLastHit = F3.Zero;
        _climbLastWallHit = F3.Zero;
        _jumpFatigueState = false;
        _wasAirborne = true;
        _airPeakHeight = F3.Dot(_pos, _up);
        _wallGrabNeedW = true;
    }

    /// <summary>
    /// 从挂墙"翻上去"：找到台沿就转 <see cref="MovementState.Hang"/>，
    /// 复用已经验证过的 Hang → Mantle pull 路径（对应 sound_wallHangComplete）。
    /// </summary>
    private bool TryCompleteWallGrab()
    {
        if (!_cfg.EnableMantleHang) return false;
        if (_wallHangNormal.LengthSq() < 1e-8f) return false;

        // TryFindClimbLedge 读的是 _climbWallNormal / _climbLastWallHit，
        // 统一状态机里它们已经与 _wallHangNormal 同步，这里再兜一次。
        _climbWallNormal = _wallHangNormal;
        _climbLastWallHit = _wallHangLastHit;

        if (!TryFindClimbLedge(out F3 ledgeTop)) return false;

        _climbHangTop = ledgeTop;
        EnterHangFromClimb();

        // 完成时给一点前推，避免"翻上去之后贴着墙站着"
        _vel += -_wallHangNormal * _cfg.WallHangCompleteForwardSpeed;

        _wallHangNormal = F3.Zero;
        _wallHangTime = 0f;
        _wallHangSlip = 0f;
        _wallHangLastHit = F3.Zero;
        _climbLastWallHit = F3.Zero;
        return true;
    }

    // ───────────────────────── 三档障碍分级（v0.4） ─────────────────────────
    //
    // 需求方口径（逐字）：
    //
    //   >1 m     = 墙      直接撞上去、不翻越；需按 W+Space 跳过，或触发攀爬+翻越；
    //                      攀爬时水平速度掉为 0，影响前进速度。
    //   ≤1 m     = 小障碍  靠近按 W+Space 直接翻越，**不触发攀爬**，水平速度削减较小，
    //                      翻越结束快速恢复满速。
    //   ≤0.25 m  = 台阶    直接平滑经过、无速度衰减；水平速度变斜向上再变回水平，
    //                      只有方向变化、大小不变，不触发任何动作；tan(θ) = b / a。
    //
    // 三档在代码里的落点：
    //   · 台阶   → MoveWithCollision 的 TryStepUp（OverwalkableHeight = 0.25）
    //              + ApplyStepTilt（只旋转速度、不改大小）
    //   · 小障碍 → 本函数 TryVaultObstacle（只在 StepGround 的跳跃分支里调用）
    //   · 墙     → 地面撞停（MoveWithCollision 挡下）+ 空中 → TryAttachWallGrab

    /// <summary>
    /// 翻越 **≤ <see cref="MovementConfig.VaultMaxHeight"/> 的小障碍**。
    ///
    /// 与 <see cref="TryTraversal"/> 的区别（这也是它单独存在的原因）：
    ///
    /// | | TryTraversal（v0.2 遗留，全档位） | TryVaultObstacle（v0.4 第二档） |
    /// |---|---|---|
    /// | 触发 | 外部显式调用 / 每 tick 自动探测 | **只由跳跃键触发**（W + Space） |
    /// | 高度 | 0.05 m … MantleMaxHeight | `OverwalkableHeight` … `VaultMaxHeight` |
    /// | 速度 | 归零（`BeginMantle` 里清空） | 翻完按 `VaultSpeedRetention` **还回去** |
    ///
    /// 需求「不触发攀爬」的落实方式：本函数**只**创建 <see cref="MovementState.Vault"/>，
    /// 且高度上限压在 1 m，绝不可能走到 Climb / WallHang 那条链上。
    ///
    /// 失败（前方没障碍 / 障碍高于 1 m / 太矮不归翻越管）时返回 false，
    /// 调用方退化成普通跳跃 —— 行为与 v0.3 完全一致。
    /// </summary>
    private bool TryVaultObstacle(in MovementInput input)
    {
        if (_world == null) return false;
        if (_state != MovementState.Ground) return false;
        if (_vaultRecoverTimer > 0f) return false;      // 刚翻完，别连环翻

        // 开口（窗）优先：W+Space 对着窗口时走"穿过去"，而不是爬上去。
        // v0.4 把自动翻越探测删掉了（需求：不要走着走着自动翻），
        // 窗口翻越就挂在这里，否则 Window 状态会变成死代码。
        if (_cfg.WindowTraversalEnabled && TryWindowTraversal(input)) return true;

        F3 fwd = Forward();
        F3 feetPos = FeetPosition;
        float reach = _cfg.CapsuleRadius + _cfg.TraversalReach;

        // 探针放在"小障碍高度的一半"处：太低会被地面回波命中，
        // 太高（> VaultMaxHeight）会直接越过矮障碍打空。
        F3 wallProbe = feetPos + _up * (_cfg.VaultMaxHeight * 0.5f);
        if (!_world.Raycast(wallProbe, fwd, reach, out F3 hitPoint, out F3 hitNormal))
            return false;

        // 只处理竖直立面（法线水平）
        if (MathF.Abs(F3.Dot(hitNormal, _up)) > 0.5f) return false;

        float facing = -F3.Dot(hitNormal, fwd);
        if (facing < MathF.Cos(_cfg.MantleMaxViewAngle * MathF.PI / 180f)) return false;

        F3 intoWall = -hitNormal;

        // ── 多点向下扫描找台面（与 TryTraversal 同源，但窗口压到小障碍档） ──
        const int samples = 5;
        float maxInward = _cfg.CapsuleRadius * 2f + 0.6f;
        float scanUp = _cfg.VaultMaxHeight + 0.3f;
        float scanLen = _cfg.VaultMaxHeight + 0.8f;

        F3 bestTop = F3.Zero;
        float bestHeight = float.MinValue;
        bool foundTop = false;

        for (int i = 1; i <= samples; i++)
        {
            float inward = maxInward * i / samples;
            F3 from = hitPoint + intoWall * inward + _up * scanUp;

            if (!_world.Raycast(from, -_up, scanLen, out F3 top, out F3 topNormal))
                continue;
            if (F3.Dot(topNormal, _up) < _cfg.WalkableNormalDot) continue;

            float h = F3.Dot(top - feetPos, _up);

            // 台阶档（≤ OverwalkableHeight）：不归翻越管 —— TryStepUp 自己会跨过去，
            // 这里插一脚反而会变成"走个小台阶就翻个跟头"。
            if (h <= _cfg.OverwalkableHeight) continue;
            // 高于小障碍上限 ⇒ 这是墙，交给撞停 / 抓墙，不翻。
            if (h > _cfg.VaultMaxHeight) continue;

            if (h > bestHeight)
            {
                bestHeight = h;
                bestTop = top;
                foundTop = true;
            }
        }

        if (!foundTop) return false;

        // ── 回扫内边沿（决定"翻过去落在哪"） ──
        float innerEdge = 0f;
        bool foundEdge = false;
        for (int i = samples; i >= 1; i--)
        {
            float inward = maxInward * i / samples;
            F3 from = hitPoint + intoWall * inward + _up * scanUp;

            if (_world.Raycast(from, -_up, scanLen, out F3 top, out F3 topNormal) &&
                F3.Dot(topNormal, _up) > _cfg.WalkableNormalDot &&
                MathF.Abs(F3.Dot(top - feetPos, _up) - bestHeight) < 0.05f)
            {
                innerEdge = inward;
                foundEdge = true;
            }
            else if (foundEdge)
            {
                break;      // 台面到此为止
            }
        }

        // 落点：越过台面落到**另一侧**（翻越不是"站上去"，是"翻过去"）
        float inwardOffset = (foundEdge ? innerEdge : 0.5f) +
                             _cfg.CapsuleRadius + _cfg.MantleForwardOffset * 0.5f;
        F3 end = bestTop + intoWall * inwardOffset + _up * (_currentHeight * 0.5f);

        if (OverlapsWithSkin(end)) return false;

        // ── 记住翻越前的水平速度：翻完按 VaultSpeedRetention 还回去 ──
        //
        // 注意：**不能只看当前速度**。走到障碍前面时 `MoveWithCollision` 已经把
        // 速度投影到障碍立面上，水平速度基本被吃成 0 —— 拿它乘保留率的话，
        // 翻越结束人就几乎原地不动，和需求「水平速度削减较小、翻越结束快速恢复满速」
        // 正好相反。所以用**意图速度**（WishSpeed）兜底。
        F3 horiz = F3.ProjectOnPlane(_vel, _up);
        float hSpeed = horiz.Length();

        float wish = WishSpeed(input);
        if (wish > hSpeed)
        {
            F3 wishDir = WishDirection(input);
            if (wishDir.LengthSq() < 1e-8f) wishDir = fwd;
            horiz = wishDir * wish;
            hSpeed = wish;
        }

        _vaultExitSpeed = hSpeed * _cfg.VaultSpeedRetention;
        _vaultExitDir = hSpeed > 1e-5f ? horiz / hSpeed : fwd;

        BeginMantle(MantleClass.Below, end,
                    _cfg.MantleDurationFor(MantleClass.Below), asVault: true);
        return true;
    }

    // ───────────────────────── 翻越 / 窗口 ─────────────────────────

    /// <summary>
    /// 尝试触发翻越/窗口穿越。由驱动在 tick 内调用（<see cref="MovementConfig.AutoTraverse"/>
    /// 为 true 时 <see cref="Step"/> 会自动调用）。
    /// </summary>
    // v0.3：翻越需要"输入指向墙面"（input.MoveZ > 0.3），
    // 旧的无参重载 `TryTraversal() => TryTraversal(MovementInput.Empty)` 会恒返回 false，
    // 是个会误导调用方的 API，已删除。外部要主动触发翻越请传 MovementInput。

    /// <summary>带输入的翻越尝试（Auto-mantle 需要知道跳跃是否按住）。</summary>
    public bool TryTraversal(in MovementInput input)
    {
        if (_world == null || _traversalCooldown > 0f) return false;
        if (_state is MovementState.Mantle or MovementState.Vault or MovementState.Window
                    or MovementState.Climb or MovementState.Hang) return false;

        // ── 先试窗口（有开口就不用翻墙） ──────────────────────────
        if (_cfg.WindowTraversalEnabled && TryWindowTraversal(input)) return true;

        F3 fwd = Forward();
        F3 feetPos = FeetPosition;

        // 0) 必须是"玩家想往墙上走"（wiki：mantle grab 的移动输入需指向墙面）。
        //
        //    ★ v0.3 新增。v0.2 完全没有输入门槛，于是**站在墙边完全不操作也会被
        //      自动抓上去**（测试里一个零输入的自由落体玩家落到地面后立刻被
        //      6m 墙 mantle 掉，就是这条缺失导致的）。和"攀爬不强制"是同一个
        //      设计原则：翻越/攀爬都必须由玩家的移动意图触发。
        if (input.MoveZ <= 0.3f) return false;

        // 1) 前方是否有墙。从角色中心起探（不从表面外推——贴墙时外推会把起点
        //    送进墙体内部，射线从实心内部射出永远打不到）。
        F3 wallProbe = feetPos + _up * (_currentHeight * 0.25f);
        float reach = _cfg.CapsuleRadius + _cfg.TraversalReach;

        if (!_world.Raycast(wallProbe, fwd, reach, out F3 hitPoint, out F3 hitNormal))
            return false;

        // 只处理竖直墙面（法线水平）
        if (MathF.Abs(F3.Dot(hitNormal, _up)) > 0.5f) return false;

        // 视角必须大致朝向墙面（wiki Mantle grab：移动输入需指向墙面 ±50° 内）
        //
        // ★ v0.3：去掉 v0.2 的 `* 0.5f` 魔法系数 —— 那等于把 50° 的容差压到 ~30°，
        //   是"该翻的时候翻不上去"的来源之一。
        float facing = -F3.Dot(hitNormal, fwd);
        if (facing < MathF.Cos(_cfg.MantleMaxViewAngle * MathF.PI / 180f)) return false;

        F3 intoWall = -hitNormal;

        // 2) 找台面：在墙内侧做**多点**向下探测，取最高且可站立的命中点。
        //
        //    为什么用多点而不是单点：单点探针的落点取决于墙体厚度，
        //    推进太少会落在台面外沿之外（打回地面），推进太多会越过整堵墙
        //    打到墙后背（同样打回地面）。多点扫描把这个"猜墙厚"的环节彻底去掉。
        F3 bestTop = F3.Zero;
        float bestHeight = float.MinValue;
        bool foundTop = false;

        const int samples = 6;
        float maxInward = _cfg.CapsuleRadius * 2f + 0.6f;   // 最多向内扫 1.3m，足够覆盖薄墙
        for (int i = 1; i <= samples; i++)
        {
            float inward = maxInward * i / samples;
            F3 from = hitPoint + intoWall * inward
                    + _up * (_cfg.MantleMaxHeight + 0.5f);

            if (!_world.Raycast(from, -_up, _cfg.MantleMaxHeight + 1.0f,
                                out F3 top, out F3 topNormal))
                continue;

            // 必须可站立
            if (F3.Dot(topNormal, _up) < _cfg.WalkableNormalDot) continue;

            float h = F3.Dot(top - feetPos, _up);
            if (h <= 0.05f) continue;          // 与脚下同高，不算障碍

            if (h > bestHeight)
            {
                bestHeight = h;
                bestTop = top;
                foundTop = true;
            }
        }

        if (!foundTop) return false;
        if (bestHeight > _cfg.MantleMaxHeight) return false;

        // 3) 反推台面的**内侧边沿**：从最深处向玩家方向回扫，
        //    找到台面结束（打回地面）的位置。这样落点可以夹在台面内部，
        //    而不是盲目推进一个半径然后掉到台面另一侧外面。
        float innerEdge = 0f;
        bool foundEdge = false;
        for (int i = samples; i >= 1; i--)
        {
            float inward = maxInward * i / samples;
            F3 from = hitPoint + intoWall * inward
                    + _up * (_cfg.MantleMaxHeight + 0.5f);

            if (_world.Raycast(from, -_up, _cfg.MantleMaxHeight + 1.0f,
                               out F3 top, out F3 topNormal) &&
                F3.Dot(topNormal, _up) > _cfg.WalkableNormalDot &&
                MathF.Abs(F3.Dot(top - feetPos, _up) - bestHeight) < 0.05f)
            {
                innerEdge = inward;
                foundEdge = true;
            }
            else if (foundEdge)
            {
                break;   // 台面到此为止
            }
        }

        // 4) 决定档位（Apex 4 档）
        MantleClass cls = _cfg.ClassifyMantle(bestHeight);
        bool isVault = cls == MantleClass.Below;

        // 落点距离（墙内方向）：
        //   攀爬 → 台面内侧边沿再收回半个半径，保证胶囊整体在台面上
        //   翻越 → 越过台面落到另一侧
        float inwardOffset;
        if (isVault)
        {
            inwardOffset = (foundEdge ? innerEdge : 0.5f) + _cfg.CapsuleRadius +
                           _cfg.MantleForwardOffset * 0.5f;
        }
        else
        {
            float safeInner = foundEdge ? innerEdge : (0.3f + _cfg.CapsuleRadius);
            inwardOffset = MathF.Max(_cfg.CapsuleRadius * 0.5f, safeInner - _cfg.CapsuleRadius * 0.6f);
        }

        // 落点必须把胶囊**放在台面上**：`_pos` 是胶囊中心，脚底要正好踩到 bestTop，
        // 所以中心抬高半个当前身高。
        F3 end = bestTop + intoWall * inwardOffset + _up * (_currentHeight * 0.5f);

        // 落点可用性检查：台面上方可能有悬挑 / 天花板 / 第二层台阶。
        // 注意 skin：落点**天然**与台面共面（脚底 == 台面高度）。若不收缩，
        // 浮点误差会让"脚底 1.5999 < 台面 1.6"被判定为重叠，把正常攀爬全部拒掉。
        if (OverlapsWithSkin(end)) return false;

        // 5) 起步
        float duration = _cfg.MantleDurationFor(cls);
        BeginMantle(cls, end, duration, isVault);

        _ = input;
        return true;
    }

    /// <summary>窗口翻越探测（对应二进制 `traversal_window_*` 六个参数）。</summary>
    private bool TryWindowTraversal(in MovementInput input)
    {
        F3 fwd = Forward();
        F3 feet = FeetPosition;
        float reach = _cfg.CapsuleRadius + _cfg.TraversalReach + 0.4f;

        // ── ① 竖直扫描：找出"前方是空的"那段高度区间 ──────────────
        // 用 12 个探针从脚底上方扫到头顶上方，命中 = 前方有墙。
        const int N = 12;
        float lo = 0.15f;
        float hi = _currentHeight + 0.90f;
        var hit = new bool[N];
        var hitPoint = new F3[N];

        for (int i = 0; i < N; i++)
        {
            float h = lo + (hi - lo) * i / (N - 1);
            F3 origin = feet + _up * h;
            hit[i] = _world!.Raycast(origin, fwd, reach, out F3 hp, out _);
            hitPoint[i] = hp;
        }

        // 找最长的连续"空"区间
        int bestStart = -1, bestLen = 0;
        int curStart = -1, curLen = 0;
        for (int i = 0; i < N; i++)
        {
            if (!hit[i])
            {
                if (curStart < 0) { curStart = i; curLen = 0; }
                curLen++;
                if (curLen > bestLen) { bestLen = curLen; bestStart = curStart; }
            }
            else
            {
                curStart = -1; curLen = 0;
            }
        }


        // 开口必须**上下都有实体**（下有窗台、上有窗楣），且至少 2 个探针高
        if (bestStart <= 0 || bestLen < 2) return false;
        int openEnd = bestStart + bestLen - 1;
        if (openEnd >= N - 1) return false;      // 上方没有窗楣 → 是矮墙，不是窗口

        // ── ② 窗台高度：从开口内部（贴着墙面内侧）向下打 ──────────
        // 用"开口下方那个命中探针"的水平位置作为墙面位置，再往墙内推 2cm，
        // 这样向下的射线一定会打到窗台顶面（而不是地面）。
        float openMidH = lo + (hi - lo) * (bestStart + bestLen * 0.5f) / (N - 1);
        F3 wallFace = hitPoint[bestStart - 1];
        F3 insidePoint = new F3(
            wallFace.X + fwd.X * 0.02f,
            feet.Y + openMidH,
            wallFace.Z + fwd.Z * 0.02f);

        if (!_world!.Raycast(insidePoint, -_up, 3.0f, out F3 sillHit, out F3 sillN) ||
            F3.Dot(sillN, _up) < _cfg.WalkableNormalDot)
            return false;

        // ── ③ 窗楣下沿：从开口内部向上打 ─────────────────────────
        if (!_world.Raycast(insidePoint, _up, 3.0f, out F3 lintelHit, out F3 lintelN) ||
            F3.Dot(lintelN, _up) > -_cfg.WalkableNormalDot)
            return false;

        float sillTop = F3.Dot(sillHit, _up);
        float lintelBottom = F3.Dot(lintelHit, _up);
        float clearance = lintelBottom - sillTop;

        // 净高必须够蹲姿通过（Apex 的 traversal_hand_required_width 语义）
        if (clearance < _cfg.CrouchHeight * 0.9f) return false;

        // ── ④ 开口宽度：向左右各打一条射线 ───────────────────────
        F3 right = F3.Cross(fwd, _up).Normalized();
        float widthL = FreeWidth(insidePoint, -right);
        float widthR = FreeWidth(insidePoint, right);
        if (widthL + widthR < _cfg.WindowRequiredWidth) return false;

        // ── ⑤ 落点：穿过窗口，脚底落在窗台上 ──────────────────────
        F3 throughDir = fwd;
        F3 endFeet = insidePoint
                   + throughDir * _cfg.WindowForwardOffset
                   + right * _cfg.WindowSidewaysOffset;
        // 把落点高度设为窗台面
        endFeet += _up * (sillTop - F3.Dot(endFeet, _up));

        F3 end = endFeet + _up * (_currentHeight * 0.5f);
        if (OverlapsWithSkin(end)) return false;

        BeginWindow(end, _cfg.WindowDuration);
        _ = input;
        return true;
    }

    /// <summary>从 origin 沿 dir 方向能走多远而不撞墙（上限 1.5m）。</summary>
    private float FreeWidth(F3 origin, F3 dir)
    {
        if (!_world!.Raycast(origin, dir, 1.5f, out F3 hp, out _))
            return 1.5f;

        float dist = F3.Dot(hp - origin, dir.Normalized());
        return MathF.Max(0f, MathF.Min(1.5f, dist));
    }

    private bool OverlapsWithSkin(F3 end)
    {
        if (_world == null) return false;
        const float skin = 0.02f;
        float hhEnd = (_currentHeight - _cfg.CapsuleRadius * 2f) * 0.5f - skin;
        if (hhEnd < 0.01f) hhEnd = 0.01f;
        float rEnd = _cfg.CapsuleRadius - skin;
        if (rEnd < 0.01f) rEnd = 0.01f;
        return _world.Overlaps(end, rEnd, hhEnd);
    }

    private void BeginMantle(MantleClass cls, F3 end, float duration, bool asVault)
    {
        _state = asVault ? MovementState.Vault : MovementState.Mantle;
        _mantleClass = cls;
        _mantlePhase = MantlePhase.Grab;
        _isWindowTraversal = false;
        _traversalStart = _pos;
        _traversalEnd = end;
        _traversalTime = 0f;
        _traversalDuration = MathF.Max(0.01f, duration);
        _vel = F3.Zero;
        _mantleCount++;

        // 抓边点：起点与终点之间、把"先往上够、再往前拉"的形状做出来
        float grabY = MathF.Max(_traversalStart.Y, end.Y - _currentHeight * 0.35f);
        _traversalMid = new F3(_traversalStart.X, grabY, _traversalStart.Z);
    }

    private void BeginWindow(F3 end, float duration)
    {
        _state = MovementState.Window;
        _mantleClass = MantleClass.Level;
        _mantlePhase = MantlePhase.Pull;
        _isWindowTraversal = true;
        _traversalStart = _pos;
        _traversalEnd = end;
        _traversalTime = 0f;
        _traversalDuration = MathF.Max(0.01f, duration);
        _vel = F3.Zero;
        _mantleCount++;
        _traversalMid = F3.Lerp(_traversalStart, end, 0.5f);
    }

    private void StepTraversal(in MovementInput input, float dt)
    {
        _traversalTime += dt;
        float t = _traversalTime / _traversalDuration;

        if (t >= 1f)
        {
            _pos = _traversalEnd;
            _vel = F3.Zero;
            bool wasVault = _state == MovementState.Vault;
            _state = MovementState.Ground;
            _traversalCooldown = _cfg.TraversalCooldown;
            // wiki：Mantling resets Jump fatigue
            _jumpFatigueState = false;
            _timeSinceLanded = 0f;
            _wasAirborne = false;

            // v0.4：翻越小障碍后把水平速度**还回去**（需求「水平速度削减较小，
            // 翻越结束快速恢复满速」）：
            //   · 立刻恢复 VaultSpeedRetention（默认 0.85）倍
            //   · 之后 VaultRecoverTime 内 StepGround 用 VaultRecoverAccelBoost 加速回满速
            if (wasVault)
            {
                _vel = _vaultExitDir * _vaultExitSpeed;
                _vaultRecoverTimer = _cfg.VaultRecoverTime;
            }
            return;
        }

        // ── 两段式轨迹 ────────────────────────────────────────────
        // Grab 段：先垂直往上够（贴住边缘）；Pull 段：再往前拉上台面。
        // 这个形状来自 wiki 对 Mantle grab / Mantle pull 的拆分，以及
        // 二进制里的 m_traversalBegin / m_traversalMid / m_traversalEnd 三段式字段。
        float grabFrac = _isWindowTraversal ? 0.5f : _cfg.MantleGrabFraction;

        if (!_isWindowTraversal && _cfg.EnableMantleHang &&
            _mantlePhase == MantlePhase.Grab && t >= grabFrac && input.MoveZ <= 0.3f)
        {
            // 松手 → 进入挂边（Mantle hang），停在抓边位置等输入
            _mantlePhase = MantlePhase.Hang;
            _state = MovementState.Hang;
            _pos = _traversalMid;
            _vel = F3.Zero;
            return;
        }

        // ── 两段式轨迹（v0.5.2 平滑修复 P2-3）──────────────────
        //
        // 原来两段**各自**做一次 SmoothStep：交界处（t = grabFrac）两侧导数都是 0，
        // 于是速度必然塌下去再重建 —— 实测 Vault 0.8 m 的交界帧 |Δp| 是
        // 0.0045 → **0.0015** → 0.0202（掉到 1/3，肉眼就是"顿一下"）。
        //
        // 现在改成**全局缓动 + 折线路径**：
        //   ① 用一条覆盖 0..1 的 SmoothStep 求总弧长参数 u（两端慢、中间快，且处处导数为正）；
        //   ② 按两段路径的**实际长度**分配弧长，u 直接线性映射到折线上。
        // 这样交界处速度**不为 0**（同一个 u 的导数），而路径形状（先上后前）完全不变。
        {
            float u = SmoothStep(t);
            F3 dA = _traversalMid - _traversalStart;
            F3 dB = _traversalEnd - _traversalMid;
            float lenA = dA.Length();
            float lenB = dB.Length();
            float total = lenA + lenB;

            if (total < 1e-5f)
            {
                _pos = _traversalEnd;
            }
            else
            {
                float along = u * total;
                if (along <= lenA || lenB < 1e-5f)
                {
                    _mantlePhase = MantlePhase.Grab;
                    _pos = lenA < 1e-5f
                        ? _traversalMid
                        : _traversalStart + dA * (along / lenA);
                }
                else
                {
                    _mantlePhase = MantlePhase.Pull;
                    _pos = _traversalMid + dB * ((along - lenA) / lenB);
                }
            }
        }

        _vel = F3.Zero;
    }

    // ───────────────────────── 蹲伏 ─────────────────────────

    private void UpdateCrouch(in MovementInput input, float dt)
    {
        float target = input.CrouchHeld ? 1f : 0f;

        // 滑铲期间强制蹲姿
        if (_state == MovementState.Slide) target = 1f;

        // 想站起时检查头顶空间。
        // 注意 testPos：**脚底锚定**后，站立姿态的胶囊中心比蹲姿高
        // (StandHeight − 当前高度)/2，所以头顶检测要按站立中心去测。
        //
        //   ★ v0.5：胶囊**底部留 4 cm 余量**（半高减 2 cm、中心抬 2 cm）。
        //     贴地站立时胶囊底面恰好落在地面上，而物理形状查询带 margin，
        //     会把地板本身判成"头顶有东西" ⇒ 永远站不起来。
        //     抬起来之后顶部高度不变（天花板仍然测得到），只忽略脚底那 4 cm ——
        //     那一段本来就压在地面里，不可能是障碍物。
        if (target < 1f && _crouchAmount > 0f)
        {
            float hh = (_cfg.StandHeight - _cfg.CapsuleRadius * 2f) * 0.5f - 0.02f;
            if (hh < 0.01f) hh = 0.01f;
            F3 testPos = _pos + _up * ((_cfg.StandHeight - _currentHeight) * 0.5f + 0.02f);
            if (_world != null && _world.Overlaps(testPos, _cfg.CapsuleRadius, hh))
                target = 1f;  // 头顶有东西，保持蹲
        }

        float rate = dt / MathF.Max(1e-4f, _cfg.CrouchTransitionTime);
        if (_crouchAmount < target) _crouchAmount = MathF.Min(target, _crouchAmount + rate);
        else if (_crouchAmount > target) _crouchAmount = MathF.Max(target, _crouchAmount - rate);

        float newHeight = _cfg.StandHeight + (_cfg.CrouchHeight - _cfg.StandHeight) * _crouchAmount;
        float delta = newHeight - _currentHeight;

        // ── 收缩方向 ──────────────────────────────────────────────
        // 地面：脚底锚定（中心随高度变化平移 delta/2）—— 等价"低头"
        // 空中：中心不动（脚底随高度变化抬升/下降 delta/2）—— 等价"收腿"
        //
        // 这正是 wiki 指出的 slidehop vs bhop 手感差异的根因。
        //
        // ★ v0.5 修复（用户 m01300 BUG 1「连续滑铲跳后松开按键不起身」）：
        //   **空中起身时脚底会向下伸 delta/2**。如果这时脚底离地面不到 delta/2，
        //   胶囊就会伸进地里；而 UpdateGroundContact 只会向下吸附、不会把胶囊顶出来，
        //   于是角色永久陷在地面以下约 4 cm，接着 Overlaps 认为"站立姿态头顶有东西"
        //   → 蹲伏 target 永远被钉成 1 → 松开 Ctrl 也站不起来，
        //   只有跳一下（胶囊升空、重叠消失）才会恢复。实测复现：
        //   空中 h 0.95→1.90 的 8 个 tick 里脚底从 +0.024 掉到 −0.042。
        //
        //   物理上正确的处理：腿伸不穿地面。所以脚底一旦碰到地面就停住，
        //   剩下的长度由**身体上移**吸收（人踩到地面时身体自然被撑起来）。
        if (_onGround || !_cfg.AirCrouchShrinksToMiddle)
        {
            _pos += _up * (delta * 0.5f);
        }
        else if (delta > 0f)
        {
            float growth = delta * 0.5f;                       // 脚底将下伸的距离
            F3 feetNow = _pos - _up * (_currentHeight * 0.5f);
            bool feetOnGround = false;
            if (_world != null &&
                _world.Raycast(feetNow, -_up, growth + 0.01f, out _, out F3 gn))
            {
                feetOnGround = F3.Dot(gn, _up) >= _cfg.WalkableNormalDot;
            }
            // 脚底被地面挡住 ⇒ 脚底锚定（中心上移 growth），腿伸不穿地
            if (feetOnGround) _pos += _up * growth;
        }

        _currentHeight = newHeight;
    }

    // ───────────────────────── 物理工具 ─────────────────────────

    private F3 WishDirection(in MovementInput input)
    {
        F3 fwd = Forward();
        F3 right = F3.Cross(fwd, _up).Normalized();
        F3 dir = right * input.MoveX + fwd * input.MoveZ;
        float len = dir.Length();
        return len > 1e-6f ? dir / len : F3.Zero;
    }

    private float WishSpeed(in MovementInput input)
    {
        float speed = _crouchAmount > 0.5f ? _cfg.CrouchWalkSpeed
                    : input.SprintHeld ? _cfg.SprintSpeed
                    : _cfg.WalkSpeed;

        // 收枪加速（wiki Holster：×1.15）
        if (input.Holstered) speed *= _cfg.HolsterMultiplier;

        // Fallstun 期间顶速下降
        if (_fallStunTimer > 0f && _fallStunTotal > 1e-4f)
        {
            float k = _fallStunTimer / _fallStunTotal;             // 1 → 0
            float scale = 1f - (1f - _cfg.FallStunSpeedScale) * k; // 恢复曲线
            speed *= scale;
        }

        // 输入强度缩放（模拟摇杆/斜向）
        float mag = MathF.Sqrt(input.MoveX * input.MoveX + input.MoveZ * input.MoveZ);
        if (mag > 1f) mag = 1f;

        return speed * mag;
    }

    /// <summary>Source 谱系的加速公式。</summary>
    private void Accelerate(F3 wishDir, float wishSpeed, float accel, float dt)
    {
        if (wishSpeed <= 0f) return;

        float currentSpeed = F3.Dot(_vel, wishDir);
        float addSpeed = wishSpeed - currentSpeed;
        if (addSpeed <= 0f) return;

        float accelSpeed = accel * dt * wishSpeed;
        if (accelSpeed > addSpeed) accelSpeed = addSpeed;

        _vel += wishDir * accelSpeed;
    }

    /// <summary>
    /// 把 <paramref name="dir"/> 夹到以 <paramref name="axis"/> 为轴、半角
    /// <paramref name="halfAngleDeg"/> 的圆锥内（都按单位向量处理）。
    ///
    /// 用于打滑后的空中方向限制：不是把方向投影成一条线（那样会完全失控），
    /// 而是允许在圆锥内自由偏转。轴反平行时退回轴本身。
    /// </summary>
    /// <summary>
    /// 把一个方向夹进以 <paramref name="axis"/> 为轴、半角 <paramref name="halfAngleDeg"/>
    /// 的圆锥里（已在圆锥内则原样返回）。
    ///
    /// 用途：离开打滑体积后的空中方向限制（<c>m_slipAirRestrictDirection</c>）。
    /// 用圆锥而不是锁死一条线，是为了"不能立刻反身拉回来"但仍能微调。
    ///
    /// 公开是为了让自检能直接验证这段纯数学（见 <c>SlipHangTest</c>）。
    /// </summary>
    public static F3 ClampToCone(F3 dir, F3 axis, float halfAngleDeg)
    {
        F3 a = axis.Normalized();
        F3 d = dir.Normalized();
        float cosAngle = F3.Dot(d, a);
        float cosLimit = MathF.Cos(halfAngleDeg * MathF.PI / 180f);
        if (cosAngle >= cosLimit) return d;      // 已在圆锥内

        // 把 d 分解成"沿轴 + 垂直轴"，垂直分量缩到极限角度对应的比例
        F3 perp = d - a * cosAngle;
        float perpLen = perp.Length();
        if (perpLen < 1e-6f)
        {
            // 完全反向：任何垂直于轴的修正都无意义，直接返回轴
            return a;
        }

        float sinLimit = MathF.Sqrt(MathF.Max(0f, 1f - cosLimit * cosLimit));
        F3 clamped = a * cosLimit + (perp / perpLen) * sinLimit;
        return clamped.Normalized();
    }

    /// <summary>Source 谱系的摩擦公式。</summary>
    /// <summary>
    /// 空中摩擦（默认 0，Apex 没有空气阻力）。保留粘性模型即可 ——
    /// 空气阻力确实是 ∝ 速度的，和地面库仑摩擦不是一回事。
    /// </summary>
    private void ApplyAirFriction(float dt, float friction)
    {
        float speed = _vel.Length();
        if (speed < 0.1f) { _vel = F3.Zero; return; }

        float control = speed < _cfg.StopSpeed ? _cfg.StopSpeed : speed;
        float drop = control * friction * dt;
        float newSpeed = speed - drop;
        if (newSpeed < 0f) newSpeed = 0f;

        _vel *= newSpeed / speed;
    }

    /// <summary>
    /// **地面驱动 + 摩擦**（v0.5.2 重做，需求 m01928 第 4 点）。
    ///
    /// 需求原话：「人行走奔跑起身是静摩擦力是做正功的，因为人不是滑动，所以摩擦力产生的
    /// 加速度应该是浮动的，而且作用于玩家输入方向。当玩家达到最大速度时，静摩擦力会归零，
    /// 如果玩家要转向，静摩擦力会将玩家的水平速度限制在上限……当玩家停止输入时，静摩擦力
    /// 将会与玩家当前移动方向相反，然后缓慢增大到最大静摩擦变为滑动摩擦，直到停止
    /// （类似汽车刹车，先降低轮胎滚动速度，最后抱死刹车）」
    /// 「如果地面摩擦系数非常小，玩家可能无法达到正常行走的最大速度，可以真实模拟冰面
    /// 难以行走，原地打滑的效果」。
    ///
    /// ## 三个工况
    ///
    /// **① 有输入 · 未超速（脚钉在地上 ⇒ 静摩擦做正功）**
    /// 加速度方向 = **输入方向**（不是速度方向！），大小 = 驱动预算 + 重力沿坡分量：
    /// <code>
    ///   a_drive = accel·|input| + g·sinθ·(输入方向·下坡方向)
    ///   a_drive = clamp(a_drive, 0, μs·g·cosθ)      ← 上限 = 最大静摩擦
    ///   v += a_drive · dir(输入) · dt
    /// </code>
    /// **静摩擦不会沿速度反方向拖你** —— 那是打滑才有的行为。所以这一支里
    /// **完全没有"摩擦减速"**，只有"驱动加速"。
    /// 一旦 <c>a_drive</c> 被 μs 卡住（抓不住地），加速度就只剩 <c>μs·g·cosθ</c>：
    /// 冰面（μk=0.05 ⇒ μs≈0.07）上按 W 只能拿到 0.69 m/s²，**永远走不到正常走速**，
    /// 这就是需求要的"冰面难以行走、原地打滑"。
    ///
    /// **② 有输入 · 已超上限（转向 / 带着惯性落地）**
    /// 需求：「静摩擦力会将玩家的水平速度限制在上限」。做法是：**只压超出的那一部分**，
    /// 用滑动摩擦的减速度（μk·g·cosθ）收回来，收到上限为止。
    /// 不一次性把速度砍到上限 —— 那会让"高速落地"瞬间变成"走路速度"，手感很假。
    /// 于是：快落地 + 按住 W ⇒ 高速滑一段、慢慢降到跑速（真人在高速下也要用几步刹车）。
    ///
    /// **③ 无输入（刹车：静摩擦缓慢长到最大，再抱死成滑动摩擦）**
    /// <code>
    ///   μ_eff = μs·min(1, t/BrakeRampTime)      t &lt; BrakeRampTime（轮胎滚阻上升）
    ///   μ_eff = μk                              t ≥ BrakeRampTime（抱死，纯滑动）
    /// </code>
    /// 正是需求说的"先降低轮胎滚动速度，最后抱死刹车"。
    /// **主动急停**：输入与速度夹角 &gt; 120° 时按
    /// <see cref="MovementConfig.ActiveBrakeMultiplier"/> 放大制动 —— 人想停会主动蹬地。
    /// </summary>
    private void ApplyGroundFrictionAndDrive(in MovementInput input, float dt,
                                             float accel, float wishSpeed)
    {
        F3 planeVel = F3.ProjectOnPlane(_vel, _groundNormal);
        F3 normalPart = _vel - planeVel;
        float speed = planeVel.Length();

        // ── 脚下材质的 μk（冰面等），μs 按同一比例缩放 ────────────────
        float muK = _cfg.GroundFrictionCoefficient;
        _effectiveMuS = _cfg.GroundStaticFrictionCoefficient;
        if (_world is IFrictionWorld fw &&
            fw.SampleFriction(FeetPosition + _up * 0.05f, out float materialMu) &&
            materialMu >= 0f)
        {
            muK = materialMu;
            // μs 与 μk 按默认配置的比例同步缩放；并保证物理上 μs ≥ μk
            float ratio = _cfg.GroundFrictionCoefficient > 1e-6f
                ? materialMu / _cfg.GroundFrictionCoefficient
                : 1f;
            _effectiveMuS = MathF.Max(materialMu, _cfg.GroundStaticFrictionCoefficient * ratio);
        }

        float cosT = Clamp(F3.Dot(_groundNormal, _up), 0f, 1f);
        float sinT = MathF.Sqrt(MathF.Max(0f, 1f - cosT * cosT));

        F3 wishDir = WishDirection(input);
        float inputMag = MathF.Sqrt(input.MoveX * input.MoveX + input.MoveZ * input.MoveZ);
        if (inputMag > 1f) inputMag = 1f;

        // 移动是**沿坡面**的，所以驱动方向也要投影到地面平面
        F3 driveDir = F3.Zero;
        if (wishDir.LengthSq() > 1e-6f)
        {
            F3 p = F3.ProjectOnPlane(wishDir.Normalized(), _groundNormal);
            float pl = p.Length();
            if (pl > 1e-4f) driveDir = p / pl;
        }

        EffectiveKineticMu = muK;
        float maxStaticAccel = _effectiveMuS * _cfg.Gravity * cosT;
        float kineticAccel = muK * _cfg.Gravity * cosT;

        if (driveDir.LengthSq() > 1e-8f)
        {
            // ── 上限 = 当前这一档的设计速度（走 / 跑 / 蹲走各自不同）──
            //
            //   需求：「奔跑时水平面移动速度永远无法高于上限」。
            //   注意超速时**不是瞬移到上限**，而是按滑动摩擦一段一段收回来
            //   （见下面 ②），所以"高速落地"会是"滑出去一小段再稳住"，
            //   而不是"啪一下被砍到走速"。
            float capSpeed = wishSpeed;

            // ── ② 超速部分用滑动摩擦收回 ────────────────────────────
            //
            //   超速时（例如高速落地、或刚被滑铲 boost 弹出去）**这一 tick 不再推进**，
            //   否则"摩擦减掉的量"会被"输入加速度加回来"，速度会永远停在 12 m/s
            //   （实测就是如此：每 tick −0.114 摩擦 +0.167 驱动，再被上限夹住）。
            //   玩家要跑得更快只有滑铲/外力；光按 W 会稳稳收回到设计顶速。
            bool overspeed = speed > capSpeed + 1e-4f;
            if (overspeed)
            {
                float newSpeed = MathF.Max(capSpeed, speed - kineticAccel * dt);
                F3 before = planeVel;
                planeVel = planeVel / speed * newSpeed;
                _accFriction += (planeVel - before) / dt;
                _isSliding = true;
            }
            else
            {
                _isSliding = false;
            }

            // ── ① 静摩擦驱动（作用于输入方向）──────────────────────
            //
            //   重力沿坡分量：下坡帮 +g·sinθ，上坡帮 −g·sinθ
            //   （坡面重力方向 = ProjectOnPlane(-up, n)，归一化后与 driveDir 点乘）
            F3 downhill = F3.ProjectOnPlane(-_up, _groundNormal);
            float dl = downhill.Length();
            float slopeAlong = dl > 1e-4f ? F3.Dot(driveDir, downhill / dl) : 0f;

            float budget = accel * inputMag;

            // 主动急停：输入与速度反向 ⇒ 乘主动制动倍率
            if (speed > 0.5f)
            {
                float align = F3.Dot(planeVel / speed, driveDir);
                if (align < -0.5f) budget *= _cfg.ActiveBrakeMultiplier;
            }

            // ── 抓地判定：推进需要的力 > 最大静摩擦 ⇒ 脚打滑 ────────
            //
            //   grip = min(1, μs·g·cosθ / 需要的推进加速度)
            //   1 = 完全抓地；<1 = 打滑。冰面（μk=0.05 ⇒ grip≈0.095）几乎推不动，
            //   这就是"冰面难以行走、原地打滑"的量化来源。
            //
            //   ★ 打滑时推进力按 grip **线性**衰减，另外再叠加一份**随滑移速度增大**
            //     的耗散（见下面 drag）。只靠"截断推进"是压不住冰面的：实测线性
            //     截断下冰面 5 秒仍能爬到 4.7 m/s（0.95 m/s² × 5 s），而只有耗散
            //     没有推进又会把玩家永久钉在原地（软锁）。两者一起才得到
            //     "起步打滑、能挪但挪不快"的稳定终端速度。
            float need = MathF.Max(1e-4f, MathF.Abs(budget));
            GripRatio = MathF.Min(1f, maxStaticAccel / need);
            float grip = GripRatio;
            float slip = 1f - grip;

            // 推进部分（受抓地限制）；坡度重力部分不受摩擦限制（重力不是摩擦给的）
            float pushAccel = MathF.Min(budget * grip, maxStaticAccel);
            float driveAccel = pushAccel + slopeAlong * _cfg.Gravity * sinT;
            if (driveAccel < 0f) driveAccel = 0f;

            if (!overspeed && driveAccel > 1e-6f)
            {
                F3 before = planeVel;
                planeVel += driveDir * (driveAccel * dt);

                // ★ 上限：**不允许输入让合速度超过当前允许的最大速度**。
                //   需求：「奔跑时水平面移动速度永远无法高于 7.6 m/s」。
                //   做法是只限制"这一 tick 加进去的增量"，绝不回削已经合法的速度 ——
                //   于是转向时速度不会越转越大，而"高速落地"的惯性也不受影响。
                float limit = MathF.Max(capSpeed, speed);
                float ns = planeVel.Length();
                if (ns > limit && ns > 1e-5f)
                    planeVel = planeVel / ns * limit;

                _accInput += (planeVel - before) / dt;
            }

            // 打滑时额外消耗动能：滑动摩擦沿运动反方向，并且**随滑移速度增大**
            //   drag = μk·g·cosθ·slip·(1 + v/vRef)
            //
            //   为什么带 (1 + v/vRef)：脚相对地面的滑移速度越大，耗散越强。
            //   于是打滑地面上存在一个**稳定的终端速度**（推进 = 耗散），
            //   而不是"慢慢加速到顶"或者"完全动不了"（后者会把玩家卡死在冰面上）。
            //   冰面 μk=0.05 实测终端速度 ≈ 1.1 m/s —— 能挪，但挪不快。
            if (slip > 1e-3f && speed > 1e-4f)
            {
                float dragAccel = kineticAccel * slip * (1f + speed / _cfg.SlipDragRefSpeed);
                F3 before = planeVel;
                float newSpeed = MathF.Max(0f, speed - dragAccel * dt);
                planeVel = planeVel / speed * newSpeed;
                _accFriction += (planeVel - before) / dt;
                if (slip > 0.5f) _isSliding = true;
            }

            _brakeRamp = 0f;
        }
        else
        {
            // ── ③ 无输入：刹车（最大制动 → 衰减到滑动摩擦）────────────
            //
            // v0.5.3 需求（m02395 第 2 点）：「停止移动时先给一个速度反向的**最大制动**
            // （比如刚刚的最大输入 10 m/s²），然后**逐渐降低至当前滑动摩擦**
            // （6.86 m/s²），直到人物停下」。
            //
            // 所以刹车曲线是**从强到弱**：
            //   a(t) = μk·g·cosθ + (a₀ − μk·g·cosθ)·(1 − min(1, t/T))
            //   a₀   = min(μs·g·cosθ, GroundAccel)
            //
            // 为什么 a₀ 要跟 GroundAccel 挂钩：需求说的是"先给一个和最大输入相当的制动"，
            // 而驱动加速度就是"腿能给出的最大推力"。同时它被静摩擦极限 μs·g·cosθ 夹住
            // （鞋底抓不住的部分给不出来）—— 于是这条曲线既符合需求，也不会突破物理。
            _brakeRamp += dt;
            float ramp = _cfg.BrakeRampTime > 1e-4f
                ? MathF.Min(1f, _brakeRamp / _cfg.BrakeRampTime)
                : 1f;

            float brakeMax = MathF.Min(maxStaticAccel, _cfg.GroundAccel);
            float decel = kineticAccel + (brakeMax - kineticAccel) * (1f - ramp);
            if (decel < kineticAccel) decel = kineticAccel;

            if (speed > 1e-5f)
            {
                F3 before = planeVel;
                float newSpeed = MathF.Max(0f, speed - decel * dt);
                planeVel = planeVel / speed * newSpeed;
                _accFriction += (planeVel - before) / dt;
            }

            //   ramp 未满 = 主动制动（静摩擦还在做负功）；ramp 满 = 已抱死成滑动摩擦
            _isSliding = ramp >= 1f && speed > 1e-3f;
            GripRatio = _isSliding ? 0f : 1f;
            _brakeAccel = decel;
        }

        _vel = normalPart + planeVel;
    }

    /// <summary>
    /// 补爬一步（贴着墙继续往上蹭，直到能进入扒墙姿态）。
    ///
    /// ★ v0.5.2：速度改用 <see cref="MovementConfig.ClimbSpeed"/>（与正常上爬同速）。
    ///   原来用 <c>ClimbHangApproachDistance / ClimbHangApproachTime</c> = 2.9167 m/s，
    ///   而正常上爬是 2.0 m/s —— 两段速度不一样，接缝处会看到一脚"加速蹿"。
    ///   <see cref="MovementConfig.ClimbHangApproachTime"/> 因此降级为"最短时长"语义。
    /// </summary>
    private void StepClimbApproach(float dt)
    {
        float speed = _cfg.ClimbSpeed;
        float stepUp = MathF.Min(_climbApproachRemaining, speed * dt);
        _climbApproachRemaining -= stepUp;

        _vel = _up * (stepUp / MathF.Max(1e-6f, dt));
        _pos += _up * stepUp;

        if (_climbApproachRemaining <= 1e-4f) EnterHangFromClimb();
    }

    /// <summary>无输入刹车已经持续多久（用于 <see cref="MovementConfig.BrakeRampTime"/> 的斜坡）。</summary>
    private float _brakeRamp;

    /// <summary>离地瞬间的水平速度。空中速度不允许超过它 + AirSteerSpeedAllowance。</summary>
    private float _airEntrySpeed;

    /// <summary>上一 tick 的状态（用于识别"刚进入空中"这类边沿）。</summary>
    private MovementState _lastState;

    /// <summary>本 tick 的等效静摩擦系数（材质覆盖时按比例缩放）。</summary>
    private float _effectiveMuS = 1.9f;

    /// <summary>
    /// 本 tick 的制动加速度（m/s²）。只在**无输入**时有值：
    /// 从"最大制动"（= min(μs·g·cosθ, GroundAccel)）线性衰减到滑动摩擦 μk·g·cosθ。
    /// HUD / 调试渲染器用它显示"现在刹车有多狠"。
    /// </summary>
    private float _brakeAccel;

    /// <summary>本 tick 的制动加速度（无输入刹车时从最大制动衰减到滑动摩擦）。</summary>
    public float BrakeAccel => _brakeAccel;

    /// <summary>本 tick 的等效滑动摩擦系数（材质覆盖后）。</summary>
    public float EffectiveKineticMu { get; private set; }

    /// <summary>
    /// 本 tick 的**抓地力比值** = 最大静摩擦加速度 / 需要的驱动加速度，夹到 [0,1]。
    ///
    /// 1 = 完全抓地（正常路面）；&lt;1 = 打滑（脚在滑，推不动身体）；
    /// 冰面 μk=0.05 时约 0.07 —— 这就是"冰面上按 W 原地打滑"的量化指标。
    /// </summary>
    public float GripRatio { get; private set; } = 1f;

    /// <summary>当前是否在打滑：静摩擦顶到极限（原地打滑）或抱死刹车中。</summary>
    public bool IsSlidingFriction => _isSliding;

    private bool _isSliding;

    private void MoveWithCollision(float dt)
    {
        F3 delta = _vel * dt;
        float hh = (_currentHeight - _cfg.CapsuleRadius * 2f) * 0.5f;
        if (hh < 0.01f) hh = 0.01f;

        F3 startPos = _pos;
        F3 startVel = _vel;
        bool hitSteepFace = false;

        for (int iter = 0; iter < 4; iter++)
        {
            if (delta.LengthSq() < 1e-12f) break;

            if (!_world!.Sweep(_pos, _cfg.CapsuleRadius, hh, delta, out F3 normal, out float frac))
            {
                _pos += delta;
                break;
            }

            // 撞到**陡面**（法线与 up 的夹角超过可行走阈值）时记一笔：
            // 后面无论"被挡掉多少比例"，都要给上台阶一次机会（见下方 blocked 的判定）。
            if (MathF.Abs(F3.Dot(normal, _up)) <= _cfg.WalkableNormalDot) hitSteepFace = true;

            _pos += delta * frac;

            F3 remaining = delta * (1f - frac);
            delta = F3.ProjectOnPlane(remaining, normal);
            _vel = F3.ProjectOnPlane(_vel, normal);

            // 撞到天花板时取消向上速度
            if (F3.Dot(normal, _up) < -0.5f && F3.Dot(_vel, _up) > 0f)
                _vel -= _up * F3.Dot(_vel, _up);
        }

        // ── 上台阶（Apex 的 "overwalkable" 机制） ──────────────────────
        //
        // wiki 引擎篇：「Ledges up to a height of around 30hu are overwalkable.
        // You can clear them by just walking over them without any additional Input.」
        // 30hu = 0.762m —— 但**必须显式实现**：胶囊扫掠不会自动爬上台阶，
        // 撞到 19cm 高的斜坡起步沿就会被当成墙挡住（实测 15° 斜坡一级都爬不上去）。
        if (_cfg.OverwalkableHeight > 0f)
        {
            F3 intended = F3.ProjectOnPlane(startVel * dt, _up);
            F3 actual = F3.ProjectOnPlane(_pos - startPos, _up);

            // 「被挡住」的两种口径，取并集：
            //   ① 水平推进不足本 tick 应有位移的 25%（原来的判据）；
            //   ② **撞到了陡面** —— 哪怕这一 tick 还蹭着滑了 80%，下一 tick 也会被完全挡住。
            //      只靠 ① 会漏判：触墙那一 tick 的推进比例取决于"接触点落在 tick 内的哪一段"，
            //      速度稍有变化（例如换了摩擦模型）就会从"放行上台阶"翻成"速度被投影清零"，
            //      于是同一段上台阶逻辑时好时坏（v0.5 库仑摩擦落地时就撞上了这个脆性）。
            bool blocked = intended.LengthSq() > 1e-10f &&
                           (hitSteepFace || actual.LengthSq() < intended.LengthSq() * 0.25f);

            if (blocked && TryStepUp(intended, startPos, hh))
            {
                // 上台阶成功：保留水平速度，清掉竖直分量（接下来交给贴地吸附）
                _vel = F3.ProjectOnPlane(startVel, _up);
            }
        }
    }

    /// <summary>
    /// 尝试上一级台阶：**退后 → 抬升 → 水平推进 → 下落找面**。
    ///
    /// 为什么要"先退后一个半径"：胶囊的底部是**半球**。贴住台阶立面时，
    /// 球面会卡在台阶上沿的**下侧**（楔住），此时直接向上抬只会加深穿插、
    /// 被扫掠判为 0 而放弃 —— 这就是"19cm 台阶爬不上去"的直接原因。
    /// 退后一个半径让球面脱离接触，抬升就自由了。
    ///
    /// 失败时不改动 <see cref="_pos"/>，返回 false。
    /// </summary>
    private bool TryStepUp(F3 horizDelta, F3 fromPos, float hh)
    {
        float step = _cfg.OverwalkableHeight;
        if (step <= 0f) return false;

        float hLen = horizDelta.Length();
        if (hLen < 1e-6f) return false;

        F3 dir = horizDelta / hLen;
        float back = _cfg.CapsuleRadius;      // 退回一个半径，脱离与台阶上沿的接触
        F3 from = fromPos - dir * back;

        // ① 抬升
        F3 lifted;
        if (_world!.Sweep(from, _cfg.CapsuleRadius, hh, _up * step, out _, out float fUp))
        {
            if (fUp < 0.5f) return false;      // 抬升空间不足一半 → 是矮天花板，不是台阶
            lifted = from + _up * (step * fUp);
        }
        else
        {
            lifted = from + _up * step;
        }

        // ② 水平推进（要走回"退后的距离" + 本 tick 的位移）
        //
        // ★ v0.4.3 物理审计 P0-2：这里原来无条件推到 max(hLen, 0.75×半径)，
        //   于是每上一级台阶**前向瞬移 0.3048 m**（冲刺一 tick 本该只走 0.1101 m，
        //   多推 3.69 倍）—— 实测楼梯上水平速度在 6.604 / 5.925 / 0.988 / 0.000
        //   之间跳，就是这个"多推"与随后的碰撞投影互相打架的结果。
        //
        //   为什么需要"多推"：胶囊底部是**半球**，贴住台阶立面时球面卡在棱的下侧，
        //   而半球要搭到台阶面上，重心得越过棱一段距离（不是箱子那样 1 mm 就够）。
        //   但"需要多少"取决于台阶高度与当前几何，**不该是一个与速度无关的常数**。
        //
        //   现在的做法：从**本 tick 应有的位移**开始，按 1/4 半径递增试，
        //   取**第一个能站住**的推进量 ⇒ 绝大多数情况只推 hLen，只有真站不住时才多推。
        float startH = F3.Dot(fromPos, _up);
        F3 down = -_up * (step + 0.10f);
        F3 landed = F3.Zero;
        F3 nD = F3.Zero;

        float advanceMin = hLen;
        float advanceMax = MathF.Max(hLen, _cfg.CapsuleRadius * 0.75f);
        float advanceStep = MathF.Max(1e-3f, _cfg.CapsuleRadius * 0.25f);

        bool found = false;
        float advance = advanceMin;
        while (true)
        {
            F3 forward = dir * (back + advance);
            F3 moved;
            if (_world.Sweep(lifted, _cfg.CapsuleRadius, hh, forward, out _, out float fH))
            {
                if (fH < 0.5f) return false;     // 水平仍被挡大半 → 这是墙，不是台阶
                moved = lifted + forward * fH;
            }
            else
            {
                moved = lifted + forward;
            }

            // ③ 下落找可站立面
            if (_world.Sweep(moved, _cfg.CapsuleRadius, hh, down, out F3 candN, out float fD))
            {
                F3 cand = moved + down * fD;
                float candH = F3.Dot(cand, _up);
                if (F3.Dot(candN, _up) >= _cfg.WalkableNormalDot &&
                    candH > startH + 0.005f &&                 // 有升高 → 是台阶
                    candH <= startH + step + 0.05f)            // 没升太高 → 不是攀爬
                {
                    landed = cand;
                    nD = candN;
                    found = true;
                    break;
                }
            }

            if (advance >= advanceMax) break;
            advance = MathF.Min(advanceMax, advance + advanceStep);
        }

        if (!found) return false;

        _pos = landed + _up * 0.001f;
        _groundNormal = nD;

        // ── 台阶倾斜（需求「水平速度变为斜向上的速度再变回水平，
        //    只有方向变化、大小不变」；tan θ = b / a） ──
        //
        //   b = 实际抬升高度（landedH - startH）
        //   a = 玩家脚底中心到脚底边缘的横向距离 = 胶囊半径（StepTiltFootRadius 可覆盖）
        //
        // 只记录角度与时长，真正的速度旋转在 ApplyStepTilt 里做 —— 这样
        // `|v|` 严格不变，且倾角在 StepTiltTime 内线性衰减回 0。
        float a = _cfg.StepTiltFootRadius > 0f ? _cfg.StepTiltFootRadius : _cfg.CapsuleRadius;
        float b = F3.Dot(landed, _up) - startH;
        if (a > 1e-4f && b > 1e-4f)
        {
            _stepTiltAngle = MathF.Atan(b / a);
            _stepTiltTimer = _cfg.StepTiltTime;
        }

        return true;
    }

    private void UpdateGroundContact(float dt)
    {
        float hh = (_currentHeight - _cfg.CapsuleRadius * 2f) * 0.5f;
        if (hh < 0.01f) hh = 0.01f;

        // 刚刚起跳：本 tick 必须保持空中态。
        // 否则位移只有几厘米，地面探测仍然命中，会把状态立刻改回 Ground 并抹掉初速。
        if (_justJumped)
        {
            _justJumped = false;
            _onGround = false;
            if (_state == MovementState.Ground) _state = MovementState.Air;
            TrackAirborne();
            return;
        }

        // 正在**上升**：不吸附、不算着地。
        //
        // 这一步至关重要：贴地吸附会把胶囊直接压到接触面，若在上升途中执行，
        // 它会把整段跳跃初速吃掉 —— 表现为"起跳后原地不动"。
        // 实测症状：满速跳跃（初速 7.69）第一 tick 上升 0.128m，侥幸躲过 0.20m 的吸附距离；
        // 但**疲劳跳**（初速 4.21）第一 tick 只上升 0.071m，第二 tick 就被吸附回地面，
        // 跳跃高度变成 0。
        //
        // 注意条件里必须带 `_state == Air`：**上坡**时把速度投影到坡面后
        // VerticalSpeed 天然为正（15° 坡、4.4 m/s → +1.14 m/s）。若无条件判定，
        // 上坡会被误判成"正在跳跃"，贴地吸附被跳过、状态被打成 Air，
        // 角色于是在坡上一路弹跳（实测 15° 斜坡只爬升 0.11m 就停住）。
        if (_state == MovementState.Air && VerticalSpeed > 0.1f)
        {
            _onGround = false;
            if (_state == MovementState.Ground)
            {
                _state = MovementState.Air;
                _coyoteTimer = _cfg.CoyoteTime;
            }
            TrackAirborne();
            return;
        }

        // ── 反穿透：脚底若已陷进地面，先把胶囊抬回来 ──────────────────
        //
        //   v0.5 修复（用户 m01300 BUG 1）的安全网。历史事故：空中起身时脚底向下伸，
        //   伸进地面 4 cm，而这里只会向下吸附 ⇒ 永久卡在地面以下，且站立检查误判被挡。
        //   UpdateCrouch 已经从源头挡住"腿伸穿地"，这里再兜一层：任何原因造成的
        //   穿透（台阶、坡面、贴墙位移、姿态插值）都在同一 tick 内被顶出来。
        {
            const float MaxPenetration = 0.10f;
            F3 feetNow = _pos - _up * (_currentHeight * 0.5f);
            if (_world!.Raycast(feetNow + _up * MaxPenetration, -_up, MaxPenetration + 0.02f,
                                out F3 penHit, out F3 penNormal) &&
                F3.Dot(penNormal, _up) >= _cfg.WalkableNormalDot)
            {
                float pen = F3.Dot(penHit - feetNow, _up);   // >0 ⇒ 地面在脚底之上
                if (pen > 0.001f) _pos += _up * pen;
            }
        }

        F3 probe = -_up * _cfg.GroundSnapLength;

        bool grounded = _world!.Sweep(_pos, _cfg.CapsuleRadius, hh, probe,
                                      out F3 normal, out float frac) &&
                        F3.Dot(normal, _up) > _cfg.WalkableNormalDot * 0.7f;

        if (grounded)
        {
            // ── 贴地吸附 ──────────────────────────────────────────
            // 只把 `_onGround` 置 true 是不够的：探测距离 0.20m 远大于单 tick 的
            // 向下位移，角色会停在离地几厘米处（实测 5.8cm）再也落不下去。
            //
            // ★ v0.5 修复（需求 m01070）：「跳到落地时，即将落地瞬间被吸在地上、
            //   突然大幅加速」。根因就在这里：0.20 m 的吸附距离是**一帧吃完**的，
            //   于是落地前的最后 0.2 m 实际下落速度恒为 0.2/dt = 12 m/s，
            //   与真实落速无关 —— 玩家看到的就是「到地面那一下突然猛加速」。
            //
            //   现在按物理量分两种情形：
            //     · **已经在地面**（走下坡 / 下台阶）：照旧全量吸附，不掉帧、不弹跳；
            //     · **空中 → 地面**（真正落地）：只允许吃掉「这一 tick 物理上本来
            //       该走的距离」（|vy|·dt + 1 mm 皮肤），剩下的留到后续 tick。
            //       落速越大触地越快，肉眼分辨不出差别，但不再有瞬移加速。
            float gap = MathF.Max(0f, _cfg.GroundSnapLength * frac - 0.001f);
            bool wasAir = !_onGround;
            if (wasAir && gap > 2e-3f)
            {
                // 空中 → 地面：**不做任何位移吸附**。
                //
                //   物理上这一 tick 该走多远，_vel 已经决定；探针剩下的 gap 是
                //   「下一 tick 才该走」的距离，本 tick 把它吃掉就等于凭空多了
                //   一段位移 —— 落地前最后一帧速度突变正是这么来的。
                //   真正的触地由 MoveWithCollision 的扫掠自然停住（留在 1 mm 皮肤），
                //   这里只负责判定「还差得多 ⇒ 继续下落」。
                _onGround = false;
                TrackAirborne();
                return;
            }
            if (gap > 0f) _pos -= _up * gap;   // ≤2mm 的皮肤收尾

            _onGround = true;
            _groundNormal = normal;
            _leftGroundByJump = false;

            if (_state == MovementState.Air || wasAir)
            {
                // ── 落地 ──────────────────────────────────────────
                // 冲击强度 = 落地瞬间**沿地面法线**的接近速度（「砸多重」的物理量）。
                float impact = MathF.Max(0f, -F3.Dot(_vel, normal));

                // ★ v0.5（需求 m00818 第 3 点）：落地按真实物理处理 ——
                //   速度沿**地面法线**的分量清零（稳固地形不传导），切向分量**保留**。
                //   例：10 m/s 垂直落在 30° 坡上 ⇒ 法向 −8.66 清零，
                //   切向留下 5.0 m/s 顺着坡往下滑；玩家可以用输入抵消或加速。
                //   原来是 `_vel -= _up * down`（只抹世界竖直分量），
                //   在坡上会留下指向坡内的法向分量，下一 tick 又被投影掉，
                //   等价于「坡面落地丢了一半速度」。
                _vel = F3.ProjectOnPlane(_vel, normal);

                if (_state == MovementState.Air)
                {
                    _state = MovementState.Ground;
                    _coyoteTimer = _cfg.CoyoteTime;
                    OnLanded(impact);
                }
            }
            else if (_state == MovementState.Ground)
            {
                _coyoteTimer = _cfg.CoyoteTime;
            }
        }
        else
        {
            bool wasOnGround = _onGround;
            _onGround = false;

            // v0.4.2：这里原来只处理 Ground ⇒ 滑铲中滑出台沿时，状态会**多留一 tick 的 Slide**
            // （本 tick 结束时已经是「离地 + Slide」），要等下一 tick 的 StepSlide 才被
            // 离地保险丝打回 Air。
            //
            // ★ 那一 tick **不是**无害的：下一 tick 的 StepSlide 一进门就被保险丝
            //   return 掉，于是**整整少积一次重力**（独立验证逐 tick 实测：
            //   prev 在第 6 tick vy 恒 0.00000 / py 不动，new 为 vy −0.34634 = −g·dt、
            //   py 少 0.005772 = g·dt²；之后每 tick 累加 g·dt²，
            //   300 tick 的无跳滑出台沿轨迹累计到位置差 0.2275 m、速度差 5.5414 m/s）。
            //
            //   所以这不是"只改标签"的洁癖改动，而是补回一个真实漏掉的重力 tick。
            //   现在离地当场就把 Slide 一并转成 Air，让「Slide 是纯地面状态」这条
            //   不变量由状态机自己保证（StepSlide 开头的保险丝保留为第二道防线）。
            //   注意：跳跃离地走的是 DoJump 已先置 Air 的路径，命中 _justJumped 早退，
            //   因此「滑铲跳」那条轨迹完全不受影响（实测 420+485+40 tick 逐位相同）。
            if (_state is MovementState.Ground or MovementState.Slide)
            {
                if (_state == MovementState.Slide)
                {
                    // 与 StepSlide 的离地保险丝保持一致，别留下半截滑铲状态
                    _slideTime = 0f;
                    _slideBoostGranted = false;
                }

                _state = MovementState.Air;
                _coyoteTimer = _cfg.CoyoteTime;
                _airEntrySpeed = HorizontalSpeed;   // 空中速度增益的基准（见 StepAir）

                // v0.5.3：离地立刻结束台阶倾斜。
                //   倾斜会往速度里叠一个"向上"的分量，贴地时它被吸附吃掉、
                //   无害；可一旦人在倾斜窗口内走出台沿，这个分量就会变成
                //   一次莫名其妙的起跳（0.2 m 台阶 ≈ 1.95 m/s 的上抛）。
                //   清掉它，倾斜就严格只在"还站在台阶上"时有效。
                _stepTiltTimer = 0f;

                // wiki：走下边缘（非跳跃离地）会**关闭**跳跃疲劳状态
                if (wasOnGround) _jumpFatigueState = false;
            }
            TrackAirborne();
        }
    }

    /// <summary>记录空中最高点（用于落地分档与 Fallstun）。</summary>
    private void TrackAirborne()
    {
        if (!_wasAirborne)
        {
            _wasAirborne = true;
            _airPeakHeight = F3.Dot(_pos, _up);
        }
        float h = F3.Dot(_pos, _up);
        if (h > _airPeakHeight) _airPeakHeight = h;
    }

    /// <summary>落地处理：落地分档 + Fallstun + 跳跃疲劳计时器重启。</summary>
    private void OnLanded(float impactSpeed)
    {
        float fallDistance = MathF.Max(0f, _airPeakHeight - F3.Dot(_pos, _up));

        _landingTier = _cfg.ClassifyLanding(fallDistance);
        _landingFallDistance = fallDistance;
        _landingImpactSpeed = impactSpeed;

        // 跳跃疲劳：落地只重启"衰减计时器"，**不关闭**疲劳状态（wiki 明确）
        _timeSinceLanded = 0f;

        // Fallstun（wiki：300hu→0s，800hu→1s，非线性）
        float stun = _cfg.FallStunDuration(fallDistance);
        if (stun > 0f)
        {
            _fallStunTotal = stun;
            _fallStunTimer = stun;
        }

        _wasAirborne = false;
        _airPeakHeight = F3.Dot(_pos, _up);
    }

    /// <summary>
    /// 步态采样 + 步频相位推进（v0.4 重写）。
    ///
    /// ## v0.3 的公式错在哪
    ///
    /// 旧公式是 `步频 = 1.2 + speed × 0.55` Hz —— 与**角色身高无关**、与**步幅无关**，
    /// 也完全没有"奔跑有腾空相"这件事。代入本插件速度：
    ///   行走 4.41 m/s → 3.62 Hz；冲刺 6.60 m/s → 4.83 Hz。
    /// 人类冲刺步频上限约 3.3 Hz（博尔特 ≈ 4.4 步/秒但那是 2.6 m 步幅的精英短跑），
    /// 4.83 Hz 等于每秒迈 5 步 —— 镜头当然像在抖腿。这就是"抖动频率和幅度还是太剧烈"
    /// 的量化根因。
    ///
    /// ## v0.4 的推导链（详见 <see cref="GaitModel"/>）
    ///
    ///   身高 → 腿长（0.53 H）
    ///        → Froude 相似律给出"舒适步行速度" vPref = 0.45·√(g·L) 与"走跑转换速度"
    ///          vRun = 1.9·vPref（用**地球**重力 9.81，不是游戏重力 20.78 —— 步态是
    ///          人的生物力学，跟游戏把重力放大到 2 倍无关）
    ///        → 步行段步频走幂律、奔跑段步频线性上升（并带腾空相）
    ///        → 步幅 = 速度 / 步频
    ///        → 垂直起伏 = 腿长·(1-cosθ)·吸收系数 + 腾空期弹道下沉
    ///        → 镜头的幅度 / 点头 / 侧倾全部由这个起伏换算
    ///
    /// 相位推进用**步频**（Hz）而不是速度线性式，所以"走快一点"和"跑起来"
    /// 在频率上是连续的，不会在走跑转换点突然跳一下。
    /// </summary>
    private void UpdateBobPhase(float dt)
    {
        bool onGaitState = _state is MovementState.Ground or MovementState.Slide;

        // 滑铲时**不按真实滑行速度**取步态：
        // 滑铲不是"跑"，是蹲着滑行，需求也要求"镜头与下蹲状态一致"。
        // 若拿 10 m/s 去算步频，会得到 4 Hz 的抖动，正好是要消除的东西。
        float speed = 0f;
        if (onGaitState)
            speed = _state == MovementState.Slide ? _cfg.CrouchWalkSpeed : HorizontalSpeed;

        _gait = GaitModel.Evaluate(
            speed,
            _cfg.StandHeight,
            MathF.Max(_cfg.WalkSpeed, _cfg.SprintSpeed),
            _state == MovementState.Slide ? 1f : _crouchAmount,
            _cfg.Gait);

        if (!onGaitState) return;

        // 相位步频封顶（v0.4.2）：真实冲刺步频 3.4 Hz 直接拿来推相位，
        // 画面上是"嗡嗡"的高频抖动。压到 BobCadenceMaxHz（3.0 Hz）后仍然
        // 跟得上步点，但明显更稳。HUD 里的 CadenceHz 仍是真实值。
        float camCadence = MathF.Min(_gait.CadenceHz, _cfg.Gait.BobCadenceMaxHz);
        if (camCadence > 0.05f)
        {
            // ★ v0.4.3：这个相位**改口径**了 —— 现在是**步幅相位**（一个 2π = 一个
            //   完整步幅 = 左右各一步），所以推进速率是步频的一半。
            //
            //   为什么必须改：v0.4.2 的相位是「步频相位」（一个 2π = 一步），而
            //   波形里用 cos(φ/2) 去取"半步频"的横向摆动 —— 但 cos(φ/2) 的周期是
            //   4π 而不是 2π，**不是 φ 的周期函数**。于是每次相位回绕
            //   （φ: 2π⁻ → 0⁺）横向位移与侧倾都会从 −幅度 突跳到 +幅度，
            //   频率 = 每步一次。这就是"缓慢向左倾斜一点点然后迅速回正"的根因，
            //   与幅度参数完全无关，调 BobGaitScale 永远调不好。
            //
            //   改成步幅相位后，波形侧直接用 sin(φ)（横向，1 次/步幅）与
            //   sin(2φ)（竖直，2 次/步幅 = 一步一次）—— 频率关系与物理一致，
            //   且处处光滑，合起来就是屏幕平面上的 ∞（见 core/BobWaveform.cs）。
            _bobPhase += dt * camCadence * 0.5f * MathF.PI * 2f;
            _bobPhase %= MathF.PI * 2f;
        }
    }

    // ───────────────────────── 朝向 ─────────────────────────

    public F3 Forward()
    {
        float cy = MathF.Cos(_yaw), sy = MathF.Sin(_yaw);
        F3 fwd = new(sy, 0f, -cy);

        // 把世界水平朝向映射到以 _up 为竖直的坐标系
        if (MathF.Abs(_up.Y) < 0.999f)
        {
            F3 right = F3.Cross(F3.Up, _up).Normalized();
            F3 realUp = F3.Cross(_up, right).Normalized();
            fwd = right * fwd.X + realUp * fwd.Z;
        }

        return F3.ProjectOnPlane(fwd, _up).Normalized();
    }

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

    private static float SmoothStep(float t)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        return t * t * (3f - 2f * t);
    }
}
