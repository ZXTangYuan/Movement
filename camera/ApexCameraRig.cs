using System;
using ApexMovement.Core;

namespace ApexMovement.Camera;

/// <summary>相机每帧输出的最终姿态（世界无关的标量 + 世界空间偏移）。</summary>
public struct CameraOutput
{
    /// <summary>水平 FOV（度）。</summary>
    public float Fov;

    /// <summary>相机滚转（弧度）。</summary>
    public float Roll;

    /// <summary>叠加到视角俯仰上的偏移（弧度，正 = 抬头）。</summary>
    public float PitchOffset;

    /// <summary>叠加到视角偏航上的偏移（弧度）。</summary>
    public float YawOffset;

    /// <summary>眼位偏移（世界空间，已含 bob / 冲刺前移 / 蹲前移 / 滑铲下沉）。</summary>
    public F3 EyeOffset;

    /// <summary>翻越相机脱开程度 0..1（调试 / 自检用）。</summary>
    public float TraversalBlend;

    /// <summary>
    /// 本帧 bob 的**物理半幅**（米，来自步态模型的质心起伏）。
    ///
    /// v0.5 起相机只输出**角度**（视线方向画 ∞），位移不再进入 EyeOffset，
    /// 但自检/调参仍需要"这一帧的起伏有多少毫米"这个物理量 ——
    /// 所以在输出里保留原始米制值，换算关系见 <see cref="YawOffset"/>。
    /// </summary>
    public float BobVerticalMeters;

    /// <summary>本帧 bob 的横向物理半幅（米）。见 <see cref="BobVerticalMeters"/>。</summary>
    public float BobLateralMeters;

    /// <summary>本帧 bob 的**点头**通道（弧度，来自步态模型的头部位移，不含竖直项换算）。</summary>
    public float BobPitchRad;

    /// <summary>本帧 bob 的**滚转**通道（弧度；BobRollEnabled=false 时不参与最终 Roll）。</summary>
    public float BobRollRad;
}

/// <summary>
/// Apex 风格相机效果求解器（**纯 C#，不依赖 Godot**）。
///
/// 由 <see cref="ApexMovementCore"/> 的状态快照驱动，输出 FOV / 侧倾 / 视角踢 / 眼位偏移。
/// 因为不引用引擎类型，所以可以在无头环境下做逐帧断言（见 <c>tests/CameraTest.cs</c>）。
///
/// ## 实现的四套效果（对应逆向观测到的字段）
/// 1. **FOV** —— 基础值 + 冲刺/滑铲/翻越增量，在 tan 空间平滑（`.fTanHalfFOV`）。
/// 2. **视角踢** —— 二阶弹簧（`viewkick_spring`），落地按下落距离分 4 档给冲量。
/// 3. **侧倾** —— 滑铲（随速度增大，入/出不同速率）与冲刺各一套。
/// 4. **翻越相机插值** —— 位置滞后 + 高度上限（`traversal_mantle_cap_camera_height`）+ 角度滞后。
/// 5. **走路摇晃** —— 由 tick 驱动的步频相位（帧率无关）。
/// </summary>
public sealed class ApexCameraRig
{
    private readonly CameraEffectsConfig _cfg;

    // ── FOV ──
    private float _fov;
    private bool _fovInitialized;

    // ── 视角踢弹簧（角度 + 角速度） ──
    private float _kickPitch, _kickPitchVel;
    private float _kickYaw, _kickYawVel;
    private float _kickRoll, _kickRollVel;

    // ── 侧倾 ──
    private float _slideRoll;
    private float _sprintRoll;

    // ── 翻越相机 ──
    private float _traversalBlend;          // 0 = 完全跟随, 1 = 完全脱开

    // ── 平滑量 ──
    private float _sprintBlend;
    private float _slideBlend;
    private float _traversalTargetBlend;

    private int _lastLandingTick = -1;

    /// <summary>上一帧的胶囊中心（用于翻越相机的位移滞后）。</summary>
    private F3 _prevPosition;
    private bool _hasPrevPosition;

    public ApexCameraRig(CameraEffectsConfig? config = null)
    {
        _cfg = config ?? new CameraEffectsConfig();
        _cfg.Validate();
        _fov = _cfg.FovBase;
    }

    public CameraEffectsConfig Config => _cfg;

    /// <summary>当前 FOV（度）。</summary>
    public float Fov => _fov;

    /// <summary>重置（换关卡 / 复活时调用）。</summary>
    public void Reset()
    {
        _fov = _cfg.FovBase;
        _fovInitialized = true;
        _kickPitch = _kickPitchVel = 0f;
        _kickYaw = _kickYawVel = 0f;
        _kickRoll = _kickRollVel = 0f;
        _slideRoll = 0f;
        _sprintRoll = 0f;
        _traversalBlend = 0f;
        _sprintBlend = 0f;
        _slideBlend = 0f;
        _traversalTargetBlend = 0f;
        _lastLandingTick = -1;
        _hasPrevPosition = false;
    }

    /// <summary>玩家手动调 FOV（对应游戏内 `cl_fovScale` 滑条）。返回是否被接受。</summary>
    public bool AdjustBaseFov(float deltaDeg)
    {
        float v = Math.Clamp(_cfg.FovBase + deltaDeg, _cfg.FovMin, _cfg.FovMax);
        if (MathF.Abs(v - _cfg.FovBase) < 1e-4f) return false;
        _cfg.FovBase = v;
        return true;
    }

    /// <summary>直接设定基础 FOV。</summary>
    public void SetBaseFov(float deg) => _cfg.FovBase = Math.Clamp(deg, _cfg.FovMin, _cfg.FovMax);

    /// <summary>
    /// 手动触发一次视角踢（用于测试 / 受击反馈）。
    ///
    /// v0.3：所有冲量在**这一个入口**统一乘 <see cref="CameraEffectsConfig.KickScale"/>，
    /// 于是"整体调小镜头晃动"只需改一个数（需求：镜头晃动过于剧烈）。
    /// </summary>
    public void AddKick(float pitch, float yaw, float roll)
    {
        float k = _cfg.KickScale;
        if (k <= 0f) return;      // 0 = 完全关闭视角踢
        _kickPitchVel += pitch * k;
        _kickYawVel += yaw * k;
        _kickRollVel += roll * k;
    }

    /// <summary>
    /// 推进一帧。`dt` 是**渲染帧**时长（相机只影响观感，不参与确定性轨迹）。
    /// </summary>
    public CameraOutput Update(in MovementSnapshot s, float dt)
    {
        if (dt <= 0f) dt = 1f / 60f;
        if (dt > 0.1f) dt = 0.1f;   // 卡顿保护

        if (!_fovInitialized) { _fov = _cfg.FovBase; _fovInitialized = true; }
        if (!_hasPrevPosition) { _prevPosition = s.Position; _hasPrevPosition = true; }

        UpdateBlends(s, dt);
        HandleLandingKick(s);
        StepSpring(dt);
        UpdateRoll(s, dt);
        UpdateFov(dt);

        CameraOutput output = BuildOutput(s);

        _prevPosition = s.Position;
        _hasPrevPosition = true;

        return output;
    }

    // ───────────────────────── 内部：混合量 ─────────────────────────

    private void UpdateBlends(in MovementSnapshot s, float dt)
    {
        _sprintBlend = Approach(_sprintBlend, s.Sprinting ? 1f : 0f, 6f, dt);
        _slideBlend = Approach(_slideBlend, s.Sliding ? 1f : 0f, 10f, dt);

        // ★ v0.5.2 平滑修复（P1-3）：把 Hang 也算进翻越过渡。
        //
        //   原来少了 Hang ⇒ 从 Climb 切到 Hang 的那一帧，_traversalTargetBlend 从 1
        //   掉到 0（实测 blend 1.000 → 0.917 → 0.928 的 V 形），配合下面 rise × 0.45
        //   的滞后项，相机眼位世界 Y 的单帧增量会先猛冲 6 倍再几乎卡死 50 倍 ——
        //   这是"顿一下"在画面上最直接的来源。
        //
        //   注意**不要**把 WallHang 也加进来：那是可以长时间停留的独立状态，
        //   挂着的时候不应该一直保持翻越的 FOV/滞后系数。
        bool traversing = s.TraversalProgress >= 0f ||
                          s.State is MovementState.Mantle or MovementState.Vault
                                   or MovementState.Window or MovementState.Climb
                                   or MovementState.Hang;
        _traversalTargetBlend = traversing ? 1f : 0f;

        // 翻越相机的"脱开 → 回贴"用两个不同时长（对应 traversal_viewLerpOut / InDuration）
        float rate = traversing
            ? 1f / MathF.Max(1e-3f, _cfg.TraversalLerpOutDuration)
            : 1f / MathF.Max(1e-3f, _cfg.TraversalLerpInDuration);

        _traversalBlend = Approach(_traversalBlend, _traversalTargetBlend, rate, dt);
    }

    // ───────────────────────── 内部：落地视角踢 ─────────────────────────

    private void HandleLandingKick(in MovementSnapshot s)
    {
        if (s.LandingTier == LandingTier.None) return;
        if (s.TickCount == _lastLandingTick) return;   // 同一 tick 只处理一次
        _lastLandingTick = s.TickCount;

        int idx = (int)s.LandingTier;
        if (idx < 0 || idx >= _cfg.LandingPitchImpulse.Length) return;

        // 统一走 AddKick，保证 KickScale 对所有冲量（落地 + 跳跃）都可调。
        //
        // 俯仰冲量取负：落地瞬间镜头向下砸一下，然后弹簧把它拉回来。
        // 偏航/滚转用确定性的正负交替（不读随机源，保持可复现）。
        float sign = (s.TickCount & 1) == 0 ? 1f : -1f;
        AddKick(-_cfg.LandingPitchImpulse[idx],
                _cfg.LandingYawImpulse[idx] * sign,
                _cfg.LandingRollImpulse[idx] * sign);
    }

    // ───────────────────────── 内部：弹簧积分 ─────────────────────────

    /// <summary>
    /// 二阶弹簧：<c>a = −k·x − c·v</c>，半隐式欧拉。
    /// 子步长上限 1/240 s，保证低帧率下也不发散（确定性：同样 dt 序列 → 同样结果）。
    /// </summary>
    private void StepSpring(float dt)
    {
        const float maxStep = 1f / 240f;
        int steps = Math.Max(1, (int)MathF.Ceiling(dt / maxStep));
        float h = dt / steps;

        float k = _cfg.SpringStiffness;
        float c = _cfg.SpringDamping;

        for (int i = 0; i < steps; i++)
        {
            SpringStep(ref _kickPitch, ref _kickPitchVel, k, c, h);
            SpringStep(ref _kickYaw, ref _kickYawVel, k, c, h);
            SpringStep(ref _kickRoll, ref _kickRollVel, k, c, h);
        }

        _kickPitch = Math.Clamp(_kickPitch, -_cfg.MaxKickAngle, _cfg.MaxKickAngle);
        _kickYaw = Math.Clamp(_kickYaw, -_cfg.MaxKickAngle, _cfg.MaxKickAngle);
        _kickRoll = Math.Clamp(_kickRoll, -_cfg.MaxKickAngle, _cfg.MaxKickAngle);
    }

    private static void SpringStep(ref float x, ref float v, float k, float c, float h)
    {
        float a = -k * x - c * v;
        v += a * h;
        x += v * h;
    }

    // ───────────────────────── 内部：侧倾 ─────────────────────────

    private void UpdateRoll(in MovementSnapshot s, float dt)
    {
        // ── 滑铲侧倾：v0.4 已整体移除 ──────────────────────────────
        //
        // 需求原话：「滑铲时镜头我希望不要倾斜，而是与下蹲状态一致。」
        // 所以滑铲时**没有**任何额外的侧倾 —— 画面只有视点降低（那由
        // CurrentHeight 决定，和蹲下走的是同一条路径），倾斜角恒为 0。
        //
        // 保留字段 `SlideMaxRollDeg` / `SlideRollIncreaseSpeed` / `SlideRollDecreaseSpeed`
        // / `SlideRollPlayerSpeedRef` / `SlideRollSide` 是为了配置文件兼容，
        // 但这里不再读取它们 —— 改配置也不会让滑铲歪头。
        _slideRoll = 0f;

        // 冲刺侧倾（`sprinttiltMaxRoll`）：这是一个**缓慢的倾斜**，不是抖动，
        // 与需求里的"视角抖动"不是一回事，所以保留；但幅度已经很小（1.5° 上限）。
        float sprintTarget = _cfg.SprintMaxRollDeg * _sprintBlend * MathF.PI / 180f;
        _sprintRoll = MoveTowards(_sprintRoll, sprintTarget, _cfg.SprintRollSpeed * MathF.PI / 180f * dt);
    }

    // ───────────────────────── 内部：FOV ─────────────────────────

    private void UpdateFov(float dt)
    {
        // ── 滑铲 FOV 加宽：v0.4 已整体移除 ──────────────────────────
        //
        // 需求：「滑铲时镜头……与下蹲状态一致」。下蹲不改 FOV，滑铲也不改。
        // （字段 `FovSlideAdd` 保留仅为配置兼容，这里不读取。）
        float target = _cfg.FovBase
                     + _cfg.FovSprintAdd * _sprintBlend
                     + _cfg.FovTraversalAdd * _traversalBlend;

        target = Math.Clamp(target, _cfg.FovMin, _cfg.FovMax);

        float rate = _cfg.FovSmoothing * dt;
        if (rate > 1f) rate = 1f;

        if (_cfg.FovSpace == FovInterpSpace.Tangent)
        {
            // 在 tan(半角) 空间插值：与 Apex 的 `.fTanHalfFOV` 存储方式一致
            float curT = MathF.Tan(_fov * MathF.PI / 360f);
            float tgtT = MathF.Tan(target * MathF.PI / 360f);
            float nextT = curT + (tgtT - curT) * rate;
            _fov = 2f * MathF.Atan(nextT) * 180f / MathF.PI;
        }
        else
        {
            _fov = _fov + (target - _fov) * rate;
        }

        _fov = Math.Clamp(_fov, _cfg.FovMin, _cfg.FovMax);
    }

    // ───────────────────────── 内部：输出 ─────────────────────────

    private CameraOutput BuildOutput(in MovementSnapshot s)
    {
        F3 up = s.Up;
        F3 fwd = ForwardFromYaw(s.Yaw, up);
        F3 right = F3.Cross(fwd, up).Normalized();

        // ── 眼位偏移 ──────────────────────────────────────────────
        F3 offset = F3.Zero;

        // 冲刺前移（`sprintViewOffset`）
        offset += fwd * (_cfg.SprintEyeForwardOffset * _sprintBlend);

        // 蹲下前倾
        offset += fwd * (_cfg.CrouchEyeForwardOffset * s.CrouchAmount);

        // ── 走路摇晃（bob）—— v0.4 改为**步态推导** ────────────────
        //
        // 需求原话：「通过步行和奔跑的速度，以及角色身高对应的步幅步频来获得
        // 对应的镜头抖动频率和幅度，奔跑状态的步幅和步频需要考虑现实人物奔跑的
        // 略微腾空。」
        //
        // 分工：
        //   · **频率**（相位推进）在求解器的 UpdateBobPhase 里，按步态模型的
        //     CadenceHz 累加相位 —— 帧率无关，且跑/走自动切换节奏。
        //   · **幅度**（这里）直接读快照里的 GaitSample：
        //       VerticalAmplitude / LateralAmplitude  半幅（米）
        //       PitchDeg / RollDeg                    半幅（度）
        //     这些值由身高 → 腿长 → Froude 速度 → 步频 → 步幅 → 腾空比
        //     → 质心起伏逐级推出，不是手调常数。
        //
        // 为什么不再有 `bobAmp = speed/5` 那套斜坡：
        // 它让"走"和"跑"用同一个波形、只改幅度，于是频率永远对不上步频，
        // 玩家看到的就是"腿在走、画面在飘"的违和感 —— 这正是需求方说
        // 「抖动还是太剧烈」的根因之一。
        bool gaitStateOk = s.State is not (MovementState.Mantle or MovementState.Vault
                                        or MovementState.Window or MovementState.Hang
                                        or MovementState.Climb or MovementState.WallHang);
        // 腾空时是否保留 bob：**不保留**（脚踩地才有 bob）。
        //
        // v0.4.2 更正：这里原来写的是 s.OnGround || s.Gait.FlightFraction > 0.05f，
        // 想表达"奔跑的腾空相继续保留 bob"。但 UpdateBobPhase 只在 Ground/Slide
        // 时才把真实速度交给 GaitModel，其余状态一律传 speed=0
        // （见 ApexMovementCore.cs:2867-2869），于是空中 Evaluate 出来的一定是
        // "站立"样本，FlightFraction 恒等于 0 —— 那个分支**永远为假**，
        // 注释描述的是一个不会发生的行为，属于会误导后来人的死代码。
        // 现在直接写成真实口径：只有踩在地上才有 bob，空中一律关。
        bool gaitAirOk = s.OnGround;
        bool gaitActive = gaitStateOk && gaitAirOk && !s.Sliding;

        float bobVert = 0f, bobLat = 0f, bobPitch = 0f, bobRoll = 0f;

        // 角度累加器：bob 的角度模式要往这里加，所以提前声明。
        float yawOffset = _kickYaw;
        float pitchOffset = _kickPitch;
        if (gaitActive)
        {
            // ★ v0.4.3：波形搬进 core/BobWaveform.cs 并**重写**。
            //
            //   v0.4.2 在这里直接写 sin(φ) / cos(φ/2)，其中 cos(φ/2) 的周期是 4π
            //   而不是 2π —— 相位每次回绕，横向与侧倾就跳 2×幅度（每步一次）。
            //   这就是需求方说的「缓慢向左倾斜一点点，然后迅速回正」「单脚跳」。
            //   根因在相位口径，不在幅度，所以之前怎么调 VerticalScale 都没用。
            //
            //   现在：φ 是**步幅相位**（2π = 一个步幅），
            //         lateral = A·sin(φ)   （1 次/步幅）
            //         vertical = A·sin(2φ) （2 次/步幅 = 一步一次）
            //   ⇒ 屏幕平面上的轨迹是横放的 ∞，处处光滑、无回绕跳变。
            BobWaveform.Sample(
                s.BobPhase, s.Gait, _cfg.BobGaitScale,
                _cfg.BobVerticalPhaseDeg, _cfg.BobPitchPhaseDeg, _cfg.BobRollPhaseDeg,
                out bobVert, out bobLat, out bobPitch, out bobRoll);

            // ★ v0.5（需求 m01070）：**只保留角度模式**，平移模式已删除。
            //
            //   眼位不动，改让**视线方向**在垂直于前向的截面上画 ∞ ——
            //   yaw/pitch 各自按同一组 ∞ 波形摆动。这样屏幕中心点始终对应当前
            //   视线方向，∞ 是"看的方向在画"，而不是"眼睛在画"。
            //
            //   幅度换算：BobAngularRefDistance 是"把多少米的横向位移看作 1 米处的
            //   视线偏移"，即 b 米 ⇔ atan(b/ref) 弧度。默认 1 m 时，
            //   横向半幅 8 mm ⇒ 0.459° 的 yaw 摆幅。
            float k = 1f / _cfg.BobAngularRefDistance;
            yawOffset += MathF.Atan(bobLat * k);
            pitchOffset += MathF.Atan(bobVert * k);
        }

        // ── 翻越相机：位置滞后 + 高度上限 ──────────────────────────
        if (_traversalBlend > 1e-3f)
        {
            // 高度上限（traversal_mantle_cap_camera_height）：
            // 翻越高墙时相机不跟着升到很高，避免眩晕。
            float eyeAboveFeet = s.EyeOffsetFromFeet;
            float capped = MathF.Min(eyeAboveFeet, _cfg.TraversalCapCameraHeight);
            float capDrop = (eyeAboveFeet - capped) * _traversalBlend;
            offset -= up * capDrop;

            // 位置滞后（traversal_viewLerpOutPos 的语义）：
            // 相机只跟随本帧上升量的一部分，表现为"慢半拍"。
            float rise = F3.Dot(s.Position - _prevPosition, up);
            if (rise > 0f) offset -= up * (rise * _cfg.TraversalPositionLag * _traversalBlend);
        }

        // ── 角度叠加 ──────────────────────────────────────────────
        pitchOffset += bobPitch;
        // ★ v0.4.4（需求 m00818）：滚转默认关 —— 跑步侧倾与 bob 滚转都要受开关控制。
        //   滚转会让地平线倾斜（"镜头以视线为轴转一点点角度"），需求要的是
        //   视线/视点在截面上画 ∞，不是画面歪，所以默认只留滑铲侧倾与落地冲击。
        float sprintRoll = _cfg.SprintRollEnabled ? _sprintRoll : 0f;
        float bobRollApplied = _cfg.BobRollEnabled ? bobRoll : 0f;
        float roll = _slideRoll + sprintRoll + _kickRoll + bobRollApplied;

        // 翻越角度滞后：把俯仰往回压一部分（相机"慢半拍"）
        if (_traversalBlend > 1e-3f)
            pitchOffset -= s.Pitch * _cfg.TraversalAngleLag * _traversalBlend;

        return new CameraOutput
        {
            Fov = _fov,
            Roll = roll,
            PitchOffset = pitchOffset,
            YawOffset = yawOffset,
            EyeOffset = offset,
            TraversalBlend = _traversalBlend,
            // 物理半幅（米）：位移已不进入 EyeOffset，但自检仍需要这个量。
            BobVerticalMeters = gaitActive ? bobVert : 0f,
            BobLateralMeters = gaitActive ? bobLat : 0f,
            BobPitchRad = gaitActive ? bobPitch : 0f,
            BobRollRad = gaitActive ? bobRoll : 0f,
        };
    }

    // ───────────────────────── 工具 ─────────────────────────

    private static float Approach(float current, float target, float ratePerSec, float dt)
    {
        float k = ratePerSec * dt;
        if (k >= 1f) return target;
        if (k <= 0f) return current;
        return current + (target - current) * k;
    }

    /// <summary>
    /// 以**线性**速度趋近目标（每帧最多走 <paramref name="maxStep"/>）。
    ///
    /// 侧倾必须用它而不是 <see cref="Approach"/>：二进制里的
    /// `slide_viewTiltIncreaseSpeed` / `_DecreaseSpeed` / `sprinttilt*` 都是
    /// **度/秒的线性斜率**（所以才会分成增大/减小两个速率）。若把「度/秒」
    /// 当指数时间常数用，60°/s 会变成 1/1.047 ≈ 0.95 s 的时间常数，
    /// 侧倾要 1 秒才到位（实测只到目标的 65%），滑铲侧倾看起来像没生效。
    /// </summary>
    private static float MoveTowards(float current, float target, float maxStep)
    {
        float d = target - current;
        if (MathF.Abs(d) <= maxStep) return target;
        return current + MathF.Sign(d) * maxStep;
    }

    /// <summary>由 yaw 与任意 up 求水平前向（与求解器 <c>Forward()</c> 同一套基）。</summary>
    public static F3 ForwardFromYaw(float yaw, F3 up)
    {
        float cy = MathF.Cos(yaw), sy = MathF.Sin(yaw);
        F3 fwd = new(sy, 0f, -cy);

        if (MathF.Abs(up.Y) < 0.999f)
        {
            F3 right = F3.Cross(F3.Up, up).Normalized();
            F3 realUp = F3.Cross(up, right).Normalized();
            fwd = right * fwd.X + realUp * fwd.Z;
        }

        return F3.ProjectOnPlane(fwd, up).Normalized();
    }
}
