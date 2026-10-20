using System;

namespace ApexMovement.Core;

/// <summary>
/// Apex 风格移动求解器 v0.3 —— **固定 tick 确定性**实现。
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
    private float _climbNonUpwardTimer;
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

    /// <summary>翻越探针实际执行次数（自检用：验证 30 Hz 抽样真的生效）。</summary>
    private int _traversalProbeCount;
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
    private float _wallHangCooldown;
    private F3 _wallHangNormal = F3.Zero;
    private F3 _wallHangLastHit;
    /// <summary>
    /// 脱手后"必须先离开墙面一次才能再挂"的门闩。
    /// 没有它，按住蹲的玩家会在同一位置反复「抓住 → 滑落/超时脱手 → 再抓住」。
    /// </summary>
    private bool _wallHangNeedRelease;
    /// <summary>扒墙探针实际执行次数（自检用）。</summary>
    private int _wallHangProbeCount;
    /// <summary>攀爬到顶探针实际执行次数（自检用：验证 30 Hz 抽样）。</summary>
    private int _climbWallProbeCount;

    // ── 跳跃辅助 ──
    private bool _justJumped;
    private bool _jumpBuffered;
    private float _jumpBufferTimer;
    private float _coyoteTimer;
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

    /// <summary>沿重力方向的速度分量（正 = 向上）。</summary>
    public float VerticalSpeed => F3.Dot(_vel, _up);

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
        WallHanging = _state == MovementState.WallHang,
        WallHangGrip = _state == MovementState.WallHang
            ? MathF.Max(0f, 1f - _wallHangSlip / MathF.Max(1e-4f, _cfg.WallHangMaxSlipDistance))
            : 0f,
        WallHangTime = _state == MovementState.WallHang ? _wallHangTime : 0f,
        BobPhase = _bobPhase,
        TickCount = _tickCount,
    };

    public void BindWorld(IMovementWorld world) => _world = world;

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
        _climbNonUpwardTimer = 0f;
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
        _wallHangCooldown = 0f;
        _wallHangNormal = F3.Zero;
        _wallHangLastHit = F3.Zero;
        _wallHangNeedRelease = false;
        _jumpBuffered = false;
        _jumpBufferTimer = 0f;
        _coyoteTimer = 0f;
        _justJumped = false;
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

        if (input.JumpPressed) { _jumpBuffered = true; _jumpBufferTimer = _cfg.JumpBufferTime; }

        // 3) 翻越/窗口期间独占推进
        if (_state is MovementState.Mantle or MovementState.Vault or MovementState.Window)
        {
            StepTraversal(input, dt);
            return;
        }

        // 4) 贴墙爬期间独占推进
        if (_state == MovementState.Climb)
        {
            StepClimb(input, dt);
            return;
        }

        // 4b) 扒墙 / 悬垂期间独占推进（位置是运动学积分，不需要 MoveWithCollision）
        if (_state == MovementState.WallHang)
        {
            StepWallHang(input, dt);
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

        // 6) 自动翻越/窗口探测
        //
        // ★ v0.3 关键修正：**只在地面/滑铲时探测**。
        //
        // v0.2 无条件在这里调 TryTraversal，于是角色还在空中、只要按着 W 贴着墙，
        // 就会先被判定成"可以翻越"直接抓上去 —— 这就是"贴墙 W 强制攀爬"的一半根因。
        // 空中该走的是 Climb（见 StepAir），不是 mantle；而 Climb 爬到顶会自己
        // 过渡到 Hang（扒墙），再按 W 才走 Mantle pull。职责就分清了。
        //
        // 另一半是探针抽样（TraversalProbeInterval）：翻越扫描是最贵的一段，
        // 降到 30 Hz（间隔 2）对玩法无影响。（需求：性能开销大的地方可降到 30 tick/s）
        if (_cfg.AutoTraverse && (_onGround || _state == MovementState.Slide) && ShouldProbeTraversal())
        {
            _traversalProbeCount++;
            TryTraversal(input);
        }

        // 7) 按状态推进速度
        switch (_state)
        {
            case MovementState.Slide: StepSlide(input, dt); break;
            case MovementState.Slip: StepSlip(input, dt); break;
            case MovementState.Ground: StepGround(input, dt); break;
            case MovementState.Hang: StepHang(input, dt); break;
            default: StepAir(input, dt); break;
        }

        // 8) 位移与碰撞
        MoveWithCollision(dt);

        // 9) 落地判定 + 状态迁移
        UpdateGroundContact(dt);

        // 10) 步频相位（帧率无关：按 tick 累加，不按渲染帧）
        UpdateBobPhase(dt);
    }

    // ───────────────────────── 地面 ─────────────────────────

    private void StepGround(in MovementInput input, float dt)
    {
        float accel = _crouchAmount > 0.5f ? _cfg.CrouchAccel
                    : input.SprintHeld ? _cfg.SprintAccel
                    : _cfg.GroundAccel;

        ApplyFriction(dt, _cfg.Friction);

        F3 wishDir = WishDirection(input);
        float wishSpeed = WishSpeed(input);
        Accelerate(wishDir, wishSpeed, accel, dt);

        // 下坡贴地：把速度投影到地面平面，避免"跳下斜坡"
        _vel = F3.ProjectOnPlane(_vel, _groundNormal);

        // 滑铲触发（地面滑铲：只看水平速度是否过 200hu/s 门槛）
        // 注意：**滑铲本身没有冷却**，只有 Slideboost 有 2s 冷却（wiki）。
        if (input.CrouchHeld && HorizontalSpeed >= _cfg.SlideMinStartSpeed)
        {
            BeginSlide(input, fromAir: false);
            return;
        }

        // 跳跃：**必须**在贴地吸附之前处理，否则本 tick 的吸附会把跳跃初速抹掉。
        if (_jumpBuffered)
        {
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
        // ★ Apex **没有空气摩擦**：不做任何水平衰减。
        //   只有 AirFriction > 0 时才衰减（默认 0，保留字段以支持别的项目）。
        if (_cfg.AirFriction > 0f) ApplyFriction(dt, _cfg.AirFriction);

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

        Accelerate(wishDir, wishSpeed, _cfg.AirAccel, dt);

        // 重力
        _vel -= _up * (_cfg.Gravity * dt);

        // 下落速度上限
        float downward = -F3.Dot(_vel, _up);
        if (downward > _cfg.MaxFallSpeed)
            _vel += _up * (downward - _cfg.MaxFallSpeed);

        // 空中滑铲：需要**同时**满足水平 > 90hu/s 与向下 > 200hu/s（wiki Air Slide）
        if (input.CrouchHeld &&
            HorizontalSpeed >= _cfg.SlideMinStartSpeedAirHorizontal &&
            (-VerticalSpeed) >= _cfg.SlideMinStartSpeedAirVertical)
        {
            BeginSlide(input, fromAir: true);
            return;
        }

        // 土狼跳
        if (_jumpBuffered && _coyoteTimer > 0f) DoJump(fromSlide: false, crouchHeld: input.CrouchHeld);

        // 扒墙 / 悬垂（WallHang）
        //
        // 放在攀爬之前：两者互斥（扒墙要按蹲键，攀爬要朝墙动量），
        // 但"按住蹲 + 贴墙"这种组合优先理解成"我要挂住"。
        // 探针与攀爬共用 30 Hz 抽样（ClimbProbeInterval）。
        if (_state == MovementState.Air && ShouldProbeClimb())
        {
            _wallHangProbeCount++;
            if (TryAttachWallHang(input)) return;
        }

        // 贴墙爬附着（Climb）
        //
        // ★ v0.3：去掉 `input.MoveZ > 0.3f` 这个前置门槛。
        //
        // v0.2 只有"按着 W"才去试附着，而 TryAttachClimb 内部又只校验了视角，
        // 于是形成"按 W + 贴墙 ⇒ 无条件吸住"。wiki L37-38 明确说不需要 W：
        // 决定粘不粘墙的是**朝墙的动量**。所以现在移交给 TryAttachClimb 自己判
        // 「面向墙 ∧ 空中 ∧ 有朝墙速度」三件事，W 不再是条件、也不再是放行证。
        if (_state == MovementState.Air && ShouldProbeClimb())
        {
            _climbProbeCount++;
            TryAttachClimb(input);
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

    /// <summary>翻越探针累计执行次数（只读，自检/性能面板用）。</summary>
    public int TraversalProbeCount => _traversalProbeCount;

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

    /// <summary>扒墙探针累计执行次数（只读，自检用）。</summary>
    public int WallHangProbeCount => _wallHangProbeCount;

    /// <summary>
    /// 本 tick 是否处于"离开滑区后的空中方向限制"里（供 HUD / 自检读取）。
    /// </summary>
    public bool IsSlipAirRestricted => _slipAirRestrictTimer > 0f;

    // ───────────────────────── 打滑（Slip） ─────────────────────────
    //
    // 语义来源（docs/reverse-engineering-v0.2.md §5.10，全部 A 级字段名）：
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
        if (input.CrouchHeld && HorizontalSpeed >= _cfg.SlideMinStartSpeed)
        {
            BeginSlide(input, fromAir: !_onGround);
            return;
        }

        if (_jumpBuffered)
        {
            DoJump(fromSlide: false, crouchHeld: input.CrouchHeld);
            return;
        }
    }

    // ───────────────────────── 滑铲 ─────────────────────────

    private void BeginSlide(in MovementInput input, bool fromAir)
    {
        _state = MovementState.Slide;
        _slideTime = 0f;
        _slideEntrySpeed = HorizontalSpeed;

        // ★ Slideboost：进入滑铲的**第一帧**触发一次，带 2s 冷却。
        //   公式（wiki）：Slideboost = 150hu/s × (holster × walkBoost)
        //   进入后速度 = 当前速度 + Slideboost，上限 400hu/s × (holster × walkBoost)
        float mod = input.Holstered ? _cfg.HolsterMultiplier : 1f;
        _slideBoostGranted = _slideBoostCooldown <= 0f;

        F3 horiz = F3.ProjectOnPlane(_vel, _up);
        float speed = horiz.Length();
        if (speed > 1e-4f)
        {
            float boosted = speed + (_slideBoostGranted ? _cfg.SlideBoostSpeed * mod : 0f);
            float cap = _cfg.SlideMaxSpeed * mod;
            if (boosted > cap) boosted = cap;
            _vel = horiz / speed * boosted + _up * F3.Dot(_vel, _up);
        }

        if (_slideBoostGranted) _slideBoostCooldown = _cfg.SlideBoostCooldown;

        // 滑铲瞬间把胶囊拉到蹲姿（wiki：Sliding instantly pulls your Collision Pill down）
        // 注意：这里不瞬间改 _currentHeight，交给 UpdateCrouch 的滑铲强制 target=1 处理，
        // 避免"高度瞬间变化 + 中心平移"把角色塞进地面。
        _ = fromAir;
    }

    private void StepSlide(in MovementInput input, float dt)
    {
        _slideTime += dt;

        // ── 摩擦 ──────────────────────────────────────────────────
        // 反向输入会"非常剧烈"地减速（wiki），用额外摩擦倍率表达。
        float friction = _cfg.SlideFriction;
        F3 horizVel = F3.ProjectOnPlane(_vel, _up);
        F3 wishDir = WishDirection(input);
        if (horizVel.LengthSq() > 1e-6f && wishDir.LengthSq() > 1e-6f &&
            F3.Dot(horizVel.Normalized(), wishDir) < -0.2f)
        {
            friction *= _cfg.SlideReverseInputPenalty;
        }
        ApplyFriction(dt, friction);

        // ── 转向 ──────────────────────────────────────────────────
        // wiki：短暂方向输入几乎被忽略，持续按住可以极轻微转向。
        if (wishDir.LengthSq() > 1e-6f)
        {
            float steerSpeed = WishSpeed(input) * _cfg.SlideSteerFactor;
            Accelerate(wishDir, steerSpeed, _cfg.GroundAccel * _cfg.SlideSteerFactor, dt);
        }

        // 贴地
        _vel = F3.ProjectOnPlane(_vel, _groundNormal);

        // ── 结束条件 ──────────────────────────────────────────────
        bool tooSlow = HorizontalSpeed < _cfg.SlideMinSpeed;
        bool tooLong = _slideTime >= _cfg.SlideMaxDuration;
        bool releasedCrouch = !input.CrouchHeld;

        if (tooSlow || tooLong || releasedCrouch)
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

    private void EndSlide()
    {
        _state = MovementState.Ground;
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
            _traversalTime = _traversalDuration * _cfg.MantleGrabFraction;
            _traversalStart = _pos;
            _traversalMid = F3.Lerp(_pos, _traversalEnd, 0.5f);
        }

        _ = dt;
    }

    // ───────────────────────── 跳跃 ─────────────────────────

    private void DoJump(bool fromSlide, bool crouchHeld)
    {
        bool coyote = !_onGround && _coyoteTimer > 0f;

        _jumpBuffered = false;
        _jumpBufferTimer = 0f;
        _coyoteTimer = 0f;

        float jumpVel = _cfg.JumpVelocity;

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
        _vel = h + _up * jumpVel;

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

    // ───────────────────────── 贴墙爬（Climb） ─────────────────────────
    //
    // ## v0.3 重写要点（需求：攀爬改成非强制）
    //
    // v0.2 的问题：空中只要按着 W 贴着墙就吸上去，吸上就一股劲爬到顶。
    // v0.3 把它拆成"三个门槛 + 一个状态机 + 一个扒墙过渡"：
    //
    //   门槛（必须同时满足，见 TryAttachClimb）
    //     ① 在空中（不在 Ground/Slide/Mantle）
    //     ② 面向墙壁（视角偏离墙面法线 ≤ 45.57° = arccos 0.7）
    //     ③ 有朝墙速度（-dot(vel, normal) ≥ ClimbMinApproachSpeed）
    //
    //   状态机（见 StepClimb）
    //     · 按住 W ⇒ 向上爬
    //     · 松 W 超过 ClimbNoInputGrace ⇒ 脱手落回空中
    //     · 跳跃 / 蹲 / 后退 / 视角甩开 ⇒ 立即脱离
    //
    //   扒墙过渡
    //     · 探到墙顶（前向探针打空）或摸到高度上限，且上方有可站立台面
    //       ⇒ 先补爬 ClimbHangApproachDistance，再进 MovementState.Hang
    //     · 没有台面可挂 ⇒ 保持 v0.2 的 End boost 上抛
    //
    // ## 证据
    // wiki Climb：L21/L33 必须离地；L37-38 附着看的是朝墙动量而不是 W；
    // L40/L208 视角容差 45.57° = arccos(0.7)；L47 向上爬无计时（≥10hu/s 即可）；
    // L101-105 两个高度上限"谁先到谁生效"；L117 脱离角度限制与附着相同；
    // L138-140 End boost 28hu；L172-175 重附着须先低于上次 attach point，
    // 除非新表面角度差 ≥ 25.842°。
    // 逆向：`climbEnabled` / `climbHeight` / `climbSpeedStart|End` / `climbAccel` /
    // `m_wallClimbSetUp` / `m_prepClimbPusher` / `wallclimb_vertical_gain_reduction`
    // 证明攀爬是**带自己的速度曲线与 setup 阶段的状态**，不是一次性位移；
    // `m_wallHanging` / `IsWallHanging` / `m_wallHangTime` / `ACT_MP_WALLHANG_*` /
    // `sound_wallHangStart|Slip|Fall|Complete` / `rumbleOnWallHangStart`
    // 证明"扒墙"是独立状态且有独立的进入/打滑/坠落反馈 —— 对应此处的 Hang 过渡。

    /// <summary>
    /// 附着攀爬判定。**三个条件必须同时满足**：空中 + 面向墙 + 有朝墙速度。
    /// 任何一条不满足就什么都不做（返回 false），绝不"顺手吸上去"。
    /// </summary>
    private bool TryAttachClimb(in MovementInput input)
    {
        if (_world == null) return false;

        // ① 必须在空中。地面/滑铲上的"贴墙"应该交给翻越（Step ⑥），不是攀爬。
        if (_onGround) return false;
        if (_state is not (MovementState.Air or MovementState.Slide)) return false;

        // 刚从地面起跳的一小段不贴墙（留出起跳高度；wiki L45 也提到
        // 从边缘掉落时有未知的附着延迟）。
        if (_coyoteTimer > 0f) return false;

        F3 fwd = Forward();

        // ② 面向墙壁：视线方向上必须打到陡墙。
        if (!_world.Raycast(_pos, fwd, _cfg.CapsuleRadius + _cfg.TraversalReach,
                            out F3 hitPoint, out F3 hitNormal))
            return false;

        // 必须是陡墙（法线与 up 的点积 ≤ 0.7 ⇔ 墙面陡于 45.57°）。
        if (MathF.Abs(F3.Dot(hitNormal, _up)) > _cfg.WalkableNormalDot) return false;

        // 视角偏离墙面法线的角度 ≤ ClimbAttachMaxViewAngle。
        // ★ 去掉 v0.2 的 `cosLimit * 0.5f` 魔法系数（那等价于要求 ±30° 以内还不够，
        //   实际把容差砍到 1/2，配合"按 W 才探测"就变成"贴边就吸"的怪手感）。
        float facing = -F3.Dot(hitNormal, fwd);
        float cosLimit = MathF.Cos(_cfg.ClimbAttachMaxViewAngle * MathF.PI / 180f);
        if (facing < cosLimit) return false;

        // ③ 有朝墙速度：这才是"我要撞上去"的证据（需求原话）。
        float approach = -F3.Dot(_vel, hitNormal);
        if (approach < _cfg.ClimbMinApproachSpeed) return false;

        // ④ 重附着规则（wiki L172-175）：脱离后必须先掉回上次 attach point 以下；
        //    若换了面（法线夹角 ≥ 25.842°）则豁免。
        float here = F3.Dot(_pos, _up);
        if (_hasClimbed && here > _lastClimbAttachY)
        {
            float cosSep = MathF.Cos(_cfg.ClimbReattachNormalAngle * MathF.PI / 180f);
            bool sameFace = _lastClimbWallNormal.LengthSq() > 1e-8f &&
                            F3.Dot(hitNormal, _lastClimbWallNormal) > cosSep;
            if (sameFace) return false;
        }

        // ── 附着成立 ──
        _state = MovementState.Climb;
        _climbTime = 0f;
        _climbNonUpwardTimer = 0f;
        _climbNoInputTimer = 0f;
        _climbApproaching = false;
        _climbApproachRemaining = 0f;
        _climbWallNormal = hitNormal;
        _climbBaseY = here;
        _climbAttachHeight = here;
        _climbLastWallHit = hitPoint;

        // 把身体贴到墙面上（半径 + 一点皮肤）。
        // 否则"离墙 1m 但仍在探测射程内"也可能挂住，而 StepClimb 的到顶探针
        // 只有 `CapsuleRadius + 0.35` 的射程，下一 tick 就会误判成"墙没了"。
        // 注意方向：`dot(_pos - hitPoint, normal)` 才是"玩家到墙面的正距离"
        // （法线朝外，玩家在墙外时为正）。v0.3 初版写成 `dot(hitPoint - _pos, ...)`，
        // 结果恒为负 ⇒ 这行贴墙从来没生效过，玩家停在离墙 1m 处，下一 tick 的
        // 到顶探针（射程 0.7）必然打空 ⇒ 秒判 wallGone ⇒ 直接吃 End boost 弹开。
        float dist = F3.Dot(_pos - hitPoint, hitNormal);
        float want = _cfg.CapsuleRadius + 0.02f;
        _pos += hitNormal * (want - dist);

        // 抓住墙的一瞬间动量清零（手已经"挂"住了）。
        // 需求：这里只是"进入攀爬状态"，不向上爬 —— 往上爬必须按住 W。
        _vel = F3.Zero;

        _ = input;
        return true;
    }

    private void StepClimb(in MovementInput input, float dt)
    {
        _climbTime += dt;

        F3 fwd = Forward();
        F3 intoWall = -_climbWallNormal;

        // ── 脱离条件 ──────────────────────────────────────────────
        // ① 跳跃 / 蹲 / 后退
        if (_jumpBuffered || input.CrouchHeld || input.MoveZ < -0.3f)
        {
            DetachClimb(jump: _jumpBuffered);
            return;
        }

        // ② 视角甩开太大（wiki：脱离限制与附着相同，> 45.57° 掉）
        float facing = -F3.Dot(_climbWallNormal, fwd);
        float cosLimit = MathF.Cos(_cfg.ClimbAttachMaxViewAngle * MathF.PI / 180f);
        if (facing < cosLimit)
        {
            DetachClimb(jump: false);
            return;
        }

        // ── 补爬阶段（即将扒墙） ──────────────────────────────────
        // 已经决定要挂上去：这段只走竖直位移，不再做脱离判定，
        // 否则"补爬的一半"容易被墙顶那条探针打断。
        if (_climbApproaching)
        {
            float speed = _cfg.ClimbHangApproachDistance /
                          MathF.Max(1e-4f, _cfg.ClimbHangApproachTime);
            float stepUp = MathF.Min(_climbApproachRemaining, speed * dt);
            _climbApproachRemaining -= stepUp;

            _vel = _up * (stepUp / MathF.Max(1e-6f, dt));
            _pos += _up * stepUp;          // 运动学位移（同 StepTraversal）

            if (_climbApproachRemaining <= 1e-4f)
                EnterHangFromClimb();
            return;
        }

        // ── 墙没了 / 到顶 ─────────────────────────────────────────
        //
        // ★ 探针按 ClimbWallProbeInterval 抽样（默认 2 ⇒ 30 Hz）。
        //   这条射线是**整段攀爬期间每 tick** 都要打的持续开销（一次 Raycast），
        //   而墙顶不可能在一个 tick 内消失 —— 抽样对玩法零影响，开销直接砍半。
        //   非探针 tick 沿用上一次的命中点做贴墙钳制。
        bool wallGone = false;
        F3 wallHit = _climbLastWallHit;

        if (ShouldProbeClimbWall())
        {
            _climbWallProbeCount++;
            wallGone =
                !_world!.Raycast(_pos, intoWall, _cfg.CapsuleRadius + 0.35f,
                                 out F3 probedHit, out F3 n2) ||
                MathF.Abs(F3.Dot(n2, _up)) > _cfg.WalkableNormalDot;

            // 记住最后一次看到的墙面**真实命中点**：一旦到顶，上面那条射线就打空了，
            // 而"找台面"的向下扫描还需要知道墙在哪个水平平面上 —— 所以墙还在时持续记录。
            if (!wallGone)
            {
                _climbLastWallHit = probedHit;
                wallHit = probedHit;
            }
        }

        // 两个高度上限"谁先到谁生效"：attach offset（个人体力）与 climb space cutoff（墙高）
        float climbed = F3.Dot(_pos, _up) - _climbAttachHeight;
        bool overLimit = climbed >= _cfg.ClimbAttachOffset ||
                         F3.Dot(_pos, _up) - _climbBaseY >= _cfg.MantleMaxHeight;

        if (wallGone || overLimit)
        {
            // 需求：「即将扒墙，则会继续上爬一小部分进入扒墙状态」
            if (TryBeginClimbHangApproach()) return;

            // 上方没有可挂的台面（例如墙比人能爬的还高）→ 保持 v0.2 的 End boost
            DetachClimbAtTop();
            return;
        }

        // ── 攀爬速度（按住 W 才向上） ──────────────────────────────
        //
        // ⚠️ 这里**必须自己位移**：`Step()` 在 Climb 状态是提前 return 的
        //    （第 4 步独占推进），`MoveWithCollision` / `UpdateGroundContact`
        //    都不会跑。v0.2 只在这里赋 `_vel` 而没有位移，于是"按住 W 爬墙"
        //    实际上原地不动 —— 这是本次专项测试才暴露出来的真 bug。
        //    处理方式和 `StepTraversal` 一致：攀爬是**运动学**状态，直接改 `_pos`。
        bool pressingW = input.MoveZ > 0.3f;

        float verticalSpeed;
        if (pressingW)
        {
            _climbNoInputTimer = 0f;
            _climbNonUpwardTimer = 0f;
            verticalSpeed = _cfg.ClimbSpeed;
        }
        else
        {
            // 松 W：先向下滑一点点，给一个 ClimbNoInputGrace 的宽限窗口；
            // 宽限用完就真正脱手下落（需求：「短时间没按 W 则取消攀爬开始下落」）。
            _climbNoInputTimer += dt;
            if (_climbNoInputTimer >= _cfg.ClimbNoInputGrace)
            {
                DetachClimb(jump: false);
                return;
            }

            verticalSpeed = -_cfg.ClimbSpeed * 0.35f;

            // 仍然保留 wiki 的"非向上爬 1s 计时"作为后手：
            // 若玩家一直给着侧向输入却不向上，1 秒后也该掉（wiki L59-60）。
            bool hasLateral = MathF.Abs(input.MoveX) > 0.3f;
            if (hasLateral)
            {
                _climbNonUpwardTimer += dt;
                if (_climbNonUpwardTimer >= _cfg.ClimbNonUpwardTimer)
                {
                    DetachClimb(jump: false);
                    return;
                }
            }
        }

        _vel = _up * verticalSpeed;
        _pos += _up * (verticalSpeed * dt);

        // 每 tick 把身体重新贴回墙面（半径 + 一点皮肤），
        // 避免附着瞬间还离墙 1m 时 `wallGone` 立刻误判成"到顶"。
        if (!wallGone)
        {
            // 射线沿 intoWall 前进 t 命中 ⇒ t 就是"眼睛到墙面的正距离"。
            float dist = F3.Dot(_pos - wallHit, _climbWallNormal);
            float want = _cfg.CapsuleRadius + 0.02f;
            if (dist > want)
                _pos -= _climbWallNormal * (dist - want);
        }
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
        _climbApproachRemaining = _cfg.ClimbHangApproachDistance;
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
        _climbNonUpwardTimer = 0f;
        _climbNoInputTimer = 0f;
        _climbWallNormal = F3.Zero;
        _wasAirborne = false;
    }

    /// <summary>普通脱离：带出当前垂直速度，回到空中。</summary>
    private void DetachClimb(bool jump)
    {
        float carry = _vel.Y;
        RememberClimbSurface();
        _state = MovementState.Air;
        _vel = _up * carry + F3.ProjectOnPlane(_vel, _up);
        _climbNonUpwardTimer = 0f;
        _climbNoInputTimer = 0f;
        _climbApproaching = false;
        _climbApproachRemaining = 0f;
        _climbWallNormal = F3.Zero;
        _wasAirborne = true;
        _airPeakHeight = F3.Dot(_pos, _up);

        if (jump)
        {
            _vel = F3.ProjectOnPlane(_vel, _up) + _up * _cfg.JumpVelocity;
            _jumpBuffered = false;
            _jumpBufferTimer = 0f;
            _jumpFatigueState = true;   // wiki：从 Climb 跳出去会触发疲劳
            _justJumped = true;
        }
        else
        {
            _jumpFatigueState = false;  // wiki：不带跳跃脱离 Climb 会关闭疲劳
        }
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

    /// <summary>爬到顶：给 End boost（+28hu）并落到台面上。</summary>
    private void DetachClimbAtTop()
    {
        RememberClimbSurface();
        _state = MovementState.Air;
        _vel = _up * MathF.Sqrt(2f * _cfg.Gravity * _cfg.ClimbEndBoost);
        _climbNonUpwardTimer = 0f;
        _climbNoInputTimer = 0f;
        _climbApproaching = false;
        _climbApproachRemaining = 0f;
        _climbWallNormal = F3.Zero;
        _jumpFatigueState = false;
        _wasAirborne = true;
        _airPeakHeight = F3.Dot(_pos, _up);
    }

    // ───────────────────────── 扒墙 / 悬垂（WallHang） ─────────────────────────
    //
    // 与 Climb 的分工（这是 v0.3 完整版新增的一套**独立**状态机）：
    //
    //   Climb    —— 有朝墙动量 → 抓着墙往上爬，松 W 就掉。
    //   WallHang —— 停在墙上（悬垂），可以沿墙上下左右挪，久了脱手。
    //
    // 逆向依据（docs/reverse-engineering-v0.2.md §5.11，A 级字段名）：
    //   m_wallHanging / m_wallHangTime / IsWallHanging
    //   m_wallDangleClimbProgressFloor          ← "还能爬升的进度下限"（握力）
    //   ACT_MP_WALLHANG_FRONT/UP/RIGHT/LEFT/IDLE ← 5 个姿势 = 可上下左右移动
    //   sound_wallHangStart / Slip / Complete / Fall ← 挂上/下滑/完成/掉落
    //   spread_wallhanging                       ← 挂着时仍可射击（本插件无武器，仅语义）
    //   automantle_disable_hang                  ← 能自动翻越的矮墙不该先挂上去
    //
    // 设计取舍（C 级）：进入条件是**显式按蹲键**，而不是"贴墙自动挂" ——
    // 延续需求里的"非强制"原则：贴着墙跳一下不该被环境抓走。

    /// <summary>
    /// 尝试进入扒墙。返回 true 表示本 tick 已切到 <see cref="MovementState.WallHang"/>。
    ///
    /// 五条同时满足：① 空中 ② （默认）按住蹲键 ③ 前方够得到一面陡墙
    /// ④ 视角夹角 ≤ <see cref="MovementConfig.WallHangMaxViewAngle"/> ⑤ 不在冷却里。
    /// 另外 <see cref="MovementConfig.WallHangDisableWhenAutoMantleable"/> 时，
    /// 矮到能自动翻越的墙不挂（对应 <c>automantle_disable_hang</c>）。
    /// </summary>
    private bool TryAttachWallHang(in MovementInput input)
    {
        if (!_cfg.WallHangEnabled) return false;
        if (_wallHangCooldown > 0f) return false;
        if (_cfg.WallHangRequiresCrouch && !input.CrouchHeld) return false;
        if (_world == null) return false;

        F3 fwd = Forward();
        float reach = _cfg.CapsuleRadius + _cfg.WallHangReach;

        if (!_world.Raycast(_pos, fwd, reach, out F3 hitPoint, out F3 hitNormal))
        {
            // 视野里已经没有墙 ⇒ 玩家确实离开了墙面，"必须重新接近才能再挂"的
            // 状态在这里解除（这是唯一解除点，见下面的 _wallHangNeedRelease）。
            _wallHangNeedRelease = false;
            return false;
        }

        // 刚松手 / 刚脱手：必须先离开这面墙一次，才能重新挂上。
        //
        // 不加这条会出大问题：挂着不动会因握力耗尽而脱手，但玩家还按着蹲、人还贴着墙，
        // 冷却一过（0.35s）就又被抓回来 —— 表现为「挂满 6s 掉一下、立刻又挂回去」，
        // 既不符合"脱手"的语义，也让 m_wallHangTime 永远走不到上限。
        if (_wallHangNeedRelease) return false;

        // 必须是陡墙（法线与 up 的 |点积| ≤ WalkableNormalDot ⇔ 墙面陡于 45.57°）
        if (MathF.Abs(F3.Dot(hitNormal, _up)) > _cfg.WalkableNormalDot) return false;

        // 视角容差（与攀爬同源 = arccos(0.7)）
        float facing = -F3.Dot(hitNormal, fwd);
        float cosLimit = MathF.Cos(_cfg.WallHangMaxViewAngle * MathF.PI / 180f);
        if (facing < cosLimit) return false;

        // 对应 automantle_disable_hang：墙顶低于"自动翻越上限"时，先别挂 ——
        // 用一条射线近似判断：在 AutoMantleMaxHeight 高度上打不到墙 ⇒ 墙比它矮。
        if (_cfg.WallHangDisableWhenAutoMantleable)
        {
            F3 highOrigin = _pos + _up * _cfg.AutoMantleMaxHeight;
            if (!_world.Raycast(highOrigin, fwd, reach, out _, out _))
                return false;
        }

        // ── 附着成立 ──
        _state = MovementState.WallHang;
        _wallHangTime = 0f;
        _wallHangSlip = 0f;
        _wallHangNormal = hitNormal;
        _wallHangLastHit = hitPoint;

        // 贴到墙面上（半径 + 皮肤），否则下一 tick 的"墙还在吗"探针会打空
        float dist = F3.Dot(_pos - hitPoint, hitNormal);
        float want = _cfg.CapsuleRadius + 0.02f;
        _pos += hitNormal * (want - dist);

        // 挂住 = 动量清零（下落被手接住）
        _vel = F3.Zero;
        _wasAirborne = false;
        _jumpBuffered = false;
        _jumpBufferTimer = 0f;
        return true;
    }

    /// <summary>
    /// 扒墙态推进。位置是**运动学**积分（同 Climb / Traversal），所以
    /// <see cref="Step"/> 里是独占分支、不走 MoveWithCollision。
    ///
    /// 行为映射：
    ///   W / S      → 沿墙上爬 / 下移（`ACT_MP_WALLHANG_UP`）
    ///   A / D      → 沿墙左右横移（`ACT_MP_WALLHANG_LEFT/RIGHT`）
    ///   不动       → 缓慢下滑（`sound_wallHangSlip`），滑过握力下限就掉
    ///   Space      → 翻上去（`sound_wallHangComplete`）
    ///   松开蹲键   → 松手（`sound_wallHangFall`）
    /// </summary>
    private void StepWallHang(in MovementInput input, float dt)
    {
        _wallHangTime += dt;

        // ── 主动松手 ──────────────────────────────────────────────
        if (_cfg.WallHangRequiresCrouch && !input.CrouchHeld)
        {
            DetachWallHang(jumped: false);
            return;
        }

        // ── 向上翻越（Complete） / 主动下落 ────────────────────────
        if (_jumpBuffered)
        {
            if (!TryCompleteWallHang())
                DetachWallHang(jumped: true);
            return;
        }

        if (input.MoveZ < -0.6f)
        {
            DetachWallHang(jumped: false);
            return;
        }

        // ── 挂太久脱手（m_wallHangTime 上限） ──────────────────────
        if (_wallHangTime >= _cfg.WallHangMaxTime)
        {
            DetachWallHang(jumped: false);
            return;
        }

        // ── 视角甩开太大 → 松手（与攀爬同一容差） ──────────────────
        F3 fwd = Forward();
        float facing = -F3.Dot(_wallHangNormal, fwd);
        float cosLimit = MathF.Cos(_cfg.WallHangMaxViewAngle * MathF.PI / 180f);
        if (facing < cosLimit)
        {
            DetachWallHang(jumped: false);
            return;
        }

        // ── 沿墙移动 ──────────────────────────────────────────────
        F3 right = F3.Cross(fwd, _up).Normalized();
        bool pressingW = input.MoveZ > 0.3f;

        float vUp = 0f;
        if (pressingW) vUp = _cfg.WallHangClimbSpeed;
        else if (input.MoveZ < -0.3f) vUp = -_cfg.WallHangClimbSpeed * 0.6f;

        float vSide = Clamp(input.MoveX, -1f, 1f) * _cfg.WallHangShimmySpeed;

        _pos += _up * (vUp * dt) + right * (vSide * dt);

        // ── 握力（m_wallDangleClimbProgressFloor） ────────────────
        //
        // 挂着不动会往下滑；按 W 往上爬能把手"再抓高一点"，把下滑量收回来。
        if (pressingW)
        {
            if (_cfg.WallHangClimbRestoresGrip)
                _wallHangSlip = MathF.Max(0f, _wallHangSlip - _cfg.WallHangSlipSpeed * dt);
        }
        else
        {
            _wallHangSlip += _cfg.WallHangSlipSpeed * dt;
            _pos -= _up * (_cfg.WallHangSlipSpeed * dt);

            if (_wallHangSlip >= _cfg.WallHangMaxSlipDistance)
            {
                DetachWallHang(jumped: false);
                return;
            }
        }

        // ── 墙还在吗（探针按 ClimbWallProbeInterval 抽样，默认 30 Hz） ──
        bool wallGone;
        if (ShouldProbeClimbWall())
        {
            _climbWallProbeCount++;
            wallGone =
                !_world!.Raycast(_pos, -_wallHangNormal, _cfg.CapsuleRadius + 0.35f,
                                 out F3 wallHit, out F3 n2) ||
                MathF.Abs(F3.Dot(n2, _up)) > _cfg.WalkableNormalDot;

            if (!wallGone)
            {
                _wallHangLastHit = wallHit;
                _wallHangNormal = n2;

                // 每 tick 把身体重新贴回墙面
                float dist = F3.Dot(_pos - wallHit, _wallHangNormal);
                float want = _cfg.CapsuleRadius + 0.02f;
                if (dist > want) _pos -= _wallHangNormal * (dist - want);
            }
        }
        else
        {
            // 非探针 tick：沿用上一帧的结论（这里只需要"贴墙"）
            float dist = F3.Dot(_pos - _wallHangLastHit, _wallHangNormal);
            float want = _cfg.CapsuleRadius + 0.02f;
            if (dist > want) _pos -= _wallHangNormal * (dist - want);
            wallGone = false;
        }

        if (wallGone)
        {
            // 到墙顶了：能翻就翻，不能翻就松手
            if (!TryCompleteWallHang())
                DetachWallHang(jumped: false);
            return;
        }

        // 挂着的时候速度对外恒为 0（HUD 上应该看到 speed=0.00 / vy=0.00）
        _vel = F3.Zero;
    }

    /// <summary>
    /// 从扒墙"完成"上墙：找到台沿就转 <see cref="MovementState.Hang"/>，
    /// 复用攀爬到顶那条已经验证过的 Hang → Mantle pull 路径。
    /// </summary>
    private bool TryCompleteWallHang()
    {
        if (!_cfg.EnableMantleHang) return false;
        if (_wallHangNormal.LengthSq() < 1e-8f) return false;

        // TryFindClimbLedge 读的是 _climbWallNormal / _climbLastWallHit，
        // 这里把扒墙的墙面信息借给它用。
        F3 savedNormal = _climbWallNormal;
        F3 savedHit = _climbLastWallHit;

        _climbWallNormal = _wallHangNormal;
        _climbLastWallHit = _wallHangLastHit;

        bool found = TryFindClimbLedge(out F3 ledgeTop);
        if (found)
        {
            _climbHangTop = ledgeTop;
            EnterHangFromClimb();

            // 完成时给一点前推，避免"翻上去之后贴着墙站着"
            _vel += -_wallHangNormal * _cfg.WallHangCompleteForwardSpeed;

            _wallHangNormal = F3.Zero;
            _wallHangTime = 0f;
            _wallHangSlip = 0f;
            return true;
        }

        _climbWallNormal = savedNormal;
        _climbLastWallHit = savedHit;
        return false;
    }

    /// <summary>松手：回到空中，带一个可选的"蹬墙跳"。</summary>
    private void DetachWallHang(bool jumped)
    {
        _state = MovementState.Air;
        _wallHangCooldown = _cfg.WallHangReattachCooldown;
        _wallHangTime = 0f;
        _wallHangSlip = 0f;
        _wallHangNeedRelease = true;   // 必须先离开墙面一次才能再挂（见 TryAttachWallHang）
        _wasAirborne = true;
        _airPeakHeight = F3.Dot(_pos, _up);

        if (jumped && _wallHangNormal.LengthSq() > 1e-8f)
        {
            // 蹬墙跳：离墙 + 向上
            _vel = _wallHangNormal * (_cfg.JumpVelocity * 0.35f) + _up * (_cfg.JumpVelocity * 0.8f);
            _jumpBuffered = false;
            _jumpBufferTimer = 0f;
            _justJumped = true;
            _jumpFatigueState = true;
        }
        else
        {
            _vel = F3.Zero;
        }

        _wallHangNormal = F3.Zero;
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
            _state = MovementState.Ground;
            _traversalCooldown = _cfg.TraversalCooldown;
            // wiki：Mantling resets Jump fatigue
            _jumpFatigueState = false;
            _timeSinceLanded = 0f;
            _wasAirborne = false;
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

        if (t < grabFrac)
        {
            _mantlePhase = MantlePhase.Grab;
            float s = SmoothStep(t / MathF.Max(1e-4f, grabFrac));
            _pos = F3.Lerp(_traversalStart, _traversalMid, s);
        }
        else
        {
            _mantlePhase = MantlePhase.Pull;
            float s = SmoothStep((t - grabFrac) / MathF.Max(1e-4f, 1f - grabFrac));
            _pos = F3.Lerp(_traversalMid, _traversalEnd, s);
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
        if (target < 1f && _crouchAmount > 0f)
        {
            float hh = (_cfg.StandHeight - _cfg.CapsuleRadius * 2f) * 0.5f;
            F3 testPos = _pos + _up * ((_cfg.StandHeight - _currentHeight) * 0.5f);
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
        if (_onGround || !_cfg.AirCrouchShrinksToMiddle)
            _pos += _up * (delta * 0.5f);

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
    private void ApplyFriction(float dt, float friction)
    {        float speed = _vel.Length();
        if (speed < 0.1f) { _vel = F3.Zero; return; }

        float control = speed < _cfg.StopSpeed ? _cfg.StopSpeed : speed;
        float drop = control * friction * dt;
        float newSpeed = speed - drop;
        if (newSpeed < 0f) newSpeed = 0f;

        _vel *= newSpeed / speed;
    }

    private void MoveWithCollision(float dt)
    {
        F3 delta = _vel * dt;
        float hh = (_currentHeight - _cfg.CapsuleRadius * 2f) * 0.5f;
        if (hh < 0.01f) hh = 0.01f;

        F3 startPos = _pos;
        F3 startVel = _vel;

        for (int iter = 0; iter < 4; iter++)
        {
            if (delta.LengthSq() < 1e-12f) break;

            if (!_world!.Sweep(_pos, _cfg.CapsuleRadius, hh, delta, out F3 normal, out float frac))
            {
                _pos += delta;
                break;
            }

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
            bool blocked = intended.LengthSq() > 1e-10f &&
                           actual.LengthSq() < intended.LengthSq() * 0.25f;

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
        // 位移取 `max(hLen, 0.75×半径)`：贴住台阶时速度已经被扫掠吃掉，
        // 本 tick 的 hLen 只剩几毫米，照原样推进**根本走不上台阶**。
        // 用一个与速度无关的最小推进量，才能把胶囊真正送到台阶面上。
        float push = back + MathF.Max(hLen, _cfg.CapsuleRadius * 0.75f);
        F3 forward = dir * push;
        F3 moved;
        if (_world.Sweep(lifted, _cfg.CapsuleRadius, hh, forward, out _, out float fH))
        {
            if (fH < 0.5f) return false;       // 水平仍被挡大半 → 这是墙，不是台阶
            moved = lifted + forward * fH;
        }
        else
        {
            moved = lifted + forward;
        }

        // ③ 下落找可站立面
        F3 down = -_up * (step + 0.10f);
        if (!_world.Sweep(moved, _cfg.CapsuleRadius, hh, down, out F3 nD, out float fD))
            return false;                       // 下方没东西 → 是往下跳，不该上台阶
        if (F3.Dot(nD, _up) < _cfg.WalkableNormalDot) return false;

        F3 landed = moved + down * fD;
        float startH = F3.Dot(fromPos, _up);
        float landedH = F3.Dot(landed, _up);
        if (landedH <= startH + 0.005f) return false;            // 没升高 → 不是台阶
        if (landedH > startH + step + 0.05f) return false;       // 升太高 → 是攀爬，不是台阶

        _pos = landed + _up * 0.001f;
        _groundNormal = nD;
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

        F3 probe = -_up * _cfg.GroundSnapLength;

        bool grounded = _world!.Sweep(_pos, _cfg.CapsuleRadius, hh, probe,
                                      out F3 normal, out float frac) &&
                        F3.Dot(normal, _up) > _cfg.WalkableNormalDot * 0.7f;

        if (grounded)
        {
            // ── 贴地吸附 ──────────────────────────────────────────
            // 只把 `_onGround` 置 true 是不够的：探测距离 0.20m 远大于单 tick 的
            // 向下位移，角色会停在离地几厘米处（实测 5.8cm）再也落不下去。
            float snap = _cfg.GroundSnapLength * frac - 0.001f;
            if (snap > 0f) _pos -= _up * snap;

            bool wasAir = !_onGround;
            _onGround = true;
            _groundNormal = normal;

            if (_state == MovementState.Air || wasAir)
            {
                // ── 落地 ──────────────────────────────────────────
                float impact = -VerticalSpeed;

                // 落地保留水平速度，抹掉向下速度
                float down = F3.Dot(_vel, _up);
                if (down < 0f) _vel -= _up * down;

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

            if (_state == MovementState.Ground)
            {
                _state = MovementState.Air;
                _coyoteTimer = _cfg.CoyoteTime;

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

    /// <summary>步频相位：按 tick 固定推进，保证任意帧率下相位一致。</summary>
    private void UpdateBobPhase(float dt)
    {
        if (_state is MovementState.Ground or MovementState.Slide)
        {
            float speed = HorizontalSpeed;
            if (speed > 0.2f)
            {
                // 步频与速度成正比（走得越快，晃得越快）
                _bobPhase += dt * (1.2f + speed * 0.55f) * MathF.PI * 2f;
                _bobPhase %= MathF.PI * 2f;
            }
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
