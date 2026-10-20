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

        bool traversing = s.TraversalProgress >= 0f ||
                          s.State is MovementState.Mantle or MovementState.Vault
                                   or MovementState.Window or MovementState.Climb;
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
        // 滑铲侧倾：角度与**玩家速度**相关（`slide_viewTiltPlayerSpeed`），
        // 且增大/减小用两个不同速率（`slide_viewTiltIncreaseSpeed` / `DecreaseSpeed`）。
        float slideTargetDeg = 0f;
        if (s.Sliding)
        {
            float k = Math.Clamp(s.HorizontalSpeed / _cfg.SlideRollPlayerSpeedRef, 0f, 1f);
            slideTargetDeg = _cfg.SlideMaxRollDeg * k * _cfg.SlideRollSide;
        }

        float slideTarget = slideTargetDeg * MathF.PI / 180f;
        float slideRateDeg = MathF.Abs(slideTarget) > MathF.Abs(_slideRoll)
            ? _cfg.SlideRollIncreaseSpeed
            : _cfg.SlideRollDecreaseSpeed;
        _slideRoll = MoveTowards(_slideRoll, slideTarget, slideRateDeg * MathF.PI / 180f * dt);

        // 冲刺侧倾（`sprinttiltMaxRoll`）
        float sprintTarget = _cfg.SprintMaxRollDeg * _sprintBlend * MathF.PI / 180f;
        _sprintRoll = MoveTowards(_sprintRoll, sprintTarget, _cfg.SprintRollSpeed * MathF.PI / 180f * dt);
    }

    // ───────────────────────── 内部：FOV ─────────────────────────

    private void UpdateFov(float dt)
    {
        float target = _cfg.FovBase
                     + _cfg.FovSprintAdd * _sprintBlend
                     + _cfg.FovSlideAdd * _slideBlend
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

        // 滑铲下沉（贴地感）
        offset -= up * (_cfg.SlideEyeDrop * _slideBlend);

        // ── 走路摇晃（bob） ───────────────────────────────────────
        // 相位来自求解器的固定 tick（帧率无关）；这里只负责把它变成位移与角度。
        float bobAmp = 0f;
        if (s.OnGround && !s.Sliding && s.HorizontalSpeed > _cfg.BobMinSpeed)
        {
            bobAmp = Math.Clamp(s.HorizontalSpeed / 5f, 0f, 1.4f);
            if (s.Sprinting) bobAmp *= _cfg.BobSprintMultiplier;
        }

        float sinP = MathF.Sin(s.BobPhase);
        float cosHalfP = MathF.Cos(s.BobPhase * 0.5f);

        offset += up * (sinP * _cfg.BobAmplitude * bobAmp);
        offset += right * (cosHalfP * _cfg.BobLateralAmplitude * bobAmp);

        float bobPitch = MathF.Cos(s.BobPhase) * _cfg.BobPitchDeg * bobAmp * MathF.PI / 180f;
        float bobRoll = MathF.Cos(s.BobPhase * 0.5f) * _cfg.BobRollDeg * bobAmp * MathF.PI / 180f;

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
        float pitchOffset = _kickPitch + bobPitch;
        float yawOffset = _kickYaw;
        float roll = _slideRoll + _sprintRoll + _kickRoll + bobRoll;

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
