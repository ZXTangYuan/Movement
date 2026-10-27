using System;

namespace ApexMovement.Core;

/// <summary>
/// 步态模型参数（v0.4 新增）。
///
/// ## 为什么要有这个模型
///
/// v0.3 的走路摇晃是**拍出来的**：<c>_bobPhase += dt * (1.2 + speed * 0.55) * 2π</c>，
/// 也就是"步频 = 1.2 + 0.55×速度"，与身高无关、与步幅无关、也不区分走和跑。
/// 结果是：
///   · 冲刺 7.4 m/s 时步频 5.27 Hz —— 比真实短跑步频（4.5~5 Hz）还高，而且**没有腾空相**；
///   · 慢走 2 m/s 时步频 2.3 Hz —— 比真实步频（1.9 Hz）高 20%；
///   · 幅度是个 `clamp(speed/5, 0, 1.4)` 的粗糙斜坡。
/// 需求原话：「通过步行和奔跑的速度，以及角色身高对应的步幅步频来获得对应的镜头抖动频率和幅度，
/// 奔跑状态的步幅和步频需要考虑现实人物奔跑的略微腾空」。
///
/// ## 模型（C 级：从人体测量学 + 步态力学推导，常数可调）
///
/// 1. **腿长** <c>Lleg = LegLengthRatio × H</c>（人体测量学：腿长 ≈ 0.53 × 身高）。
/// 2. **偏好步速** 由 Froude 数给出：<c>vPref = FroudePref × √(g·Lleg)</c>。
///    取 Froude = 0.45（人类偏好行走的经典值），H = 1.95 m 时
///    <c>vPref = 0.45 × √(9.81 × 1.0335) = 1.43 m/s</c> —— 正好等于实测的人类舒适步速 1.4 m/s。
///    这是本模型"不是拍出来"的第一个硬证据。
/// 3. **走→跑切换**：<c>vRunStart = RunStartFroude × vPref</c>（1.9 → 2.72 m/s），
///    对应人类从走切换到跑的实测阈值（约 2.0~2.7 m/s，Froude ≈ 0.5）。
/// 4. **步频**：
///    · 走：<c>f = WalkCadenceRef × (v / vPref)^WalkCadenceExp</c>，指数 0.6
///      （步态文献里行走步频随速度近似 v^0.5~0.7）。标定点：v = vPref 时 f = 1.9 steps/s，
///      与实测人类步频 1.9 steps/s（114 steps/min）一致。
///    · 跑：<c>f = RunCadenceBase + RunCadenceGain × (v − vRunStart)</c>。
///      跑步的步频**近似饱和**（2.6~3.0 steps/s），提速主要靠步幅而不是步频，
///      所以这里是线性小幅增长而不是幂律。
///    · 两者在 <c>vRunStart × (1 ± GaitBlendWidth)</c> 之间用 smoothstep 混合，避免突变。
/// 5. **步幅** <c>Lstep = v / f</c>（由步频反推，不是独立拍的）。
/// 6. **腾空比** <c>flight = clamp((v − vRunStart)/(vRun − vRunStart), 0, 1) × MaxFlightFraction</c>。
///    这就是需求里"奔跑的略微腾空"：走永远 0，跑随速度增大，冲刺时约 38%。
/// 7. **垂直起伏**（两段相加，见 <see cref="Evaluate"/> 里的推导注释）：
///    · 走：倒立摆 —— 质心绕支撑脚翻越，几何抬升 <c>Lleg·(1−cosθ)</c>，θ = 半步幅角；
///      膝踝缓冲吸收掉大部分，保留 <see cref="GaitParams.WalkAbsorb"/>。
///    · 跑：弹道腾空 <c>g·(T_flight/2)²/2</c>（精确解）+ 支撑相腿部压缩
///      <c>RunCompression × Lleg</c>，两者按腾空比加权。
///    竖直方向取"半幅"（峰到峰 = 2×）。
/// 8. **侧向摆动** <c>lateral = clamp(LateralPerStride × Lstep, LateralMin, LateralMax)</c>
///    —— 一个**完整步幅**（两步）左右摆一次。
/// 9. **点头 / 侧倾**：按垂直 / 侧向幅度线性折算（颈部与躯干的顺应性），并各自夹上限。
/// 10. **蹲行**：步频 ×(1 − CrouchCadenceDrop × crouch)，幅度 ×(1 − CrouchAmplitudeDrop × crouch)。
///
/// ## 数值落点（H = 1.95 m，vRun = 6.604 m/s）
///
/// | 速度 | 步态 | 步频 | 步幅 | 腾空比 | 垂直半幅（峰到峰） | 点头半幅（峰到峰） |
/// |---|---|---|---|---|---|---|
/// | 1.43 m/s（偏好步速） | 走 | 1.90 Hz | 0.75 m | 0 | 2.7 mm（5.5 mm） | 0.06°（0.11°） |
/// | 4.41 m/s（本插件行走） | 跑 | 2.99 Hz | 1.48 m | 0.165 | 2.2 mm（4.4 mm） | 0.04°（0.09°） |
/// | 6.60 m/s（本插件冲刺） | 跑 | 3.29 Hz | 2.01 m | 0.380 | 6.2 mm（12.4 mm） | 0.12°（0.25°） |
///
/// 舒适档判据（m02752）：**垂直峰到峰 ≤ 2.5 cm**、**点头 ≤ 0.8°**；
/// v0.4.2 需求方再次反馈「走路/奔跑的持续摇晃还是太剧烈」后，把判据收紧到
/// **垂直峰到峰 ≤ 1.5 cm、点头峰到峰 ≤ 0.3°**，并在相机侧把相位步频
/// 压到 <see cref="BobCadenceMaxHz"/>（3.0 Hz）—— 见下方"三段收敛史"。
///
/// 对照 v0.3 的"拍出来"版本：4.41 m/s 时 3.62 Hz、6.60 m/s 时 4.83 Hz，
/// 幅度是 <c>clamp(speed/5,0,1.4)×26mm</c>（冲刺峰到峰 72 mm，是现在的 6 倍）。
///
/// ## 三段收敛史（都是需求方逐轮反馈驱动的）
///
/// | 版本 | 冲刺垂直峰到峰 | 冲刺点头峰到峰 | 频率 | 需求方反馈 |
/// |---|---|---|---|---|
/// | v0.3 | 72 mm | ≈1.9° | 4.83 Hz | 「还是太剧烈」 |
/// | v0.4 | 21 mm | 0.82° | 3.29 Hz | 「相机视角晃动函数你没改」 |
/// | v0.4.2 | **12.4 mm** | **0.25°** | **3.0 Hz（封顶）** | 本轮 |
///
/// 注意 v0.4 → v0.4.2 这一跳：**幅度只降了 41%，但主观感受差别很大** ——
/// 因为"难受"主要来自**旋转**（点头/侧倾）而不是平移。所以这一轮把
/// <c>PitchPerCm</c> 从 0.40 砍到 0.20、<c>RollPerCm</c> 从 0.30 砍到 0.16，
/// 旋转幅度直接减半，平移只按比例温和下调。
/// </summary>
public struct GaitParams
{
    /// <summary>腿长 / 身高（人体测量学 0.53）。</summary>
    public float LegLengthRatio;

    /// <summary>偏好行走速度的 Froude 数（人类实测 0.45）。</summary>
    public float FroudePref;

    /// <summary>走→跑切换阈值（相对 vPref 的倍数，实测 1.9）。</summary>
    public float RunStartFroude;

    /// <summary>v = vPref 时的行走步频 (steps/s)。</summary>
    public float WalkCadenceRef;

    /// <summary>行走步频随速度的指数（文献 0.5~0.7）。</summary>
    public float WalkCadenceExp;

    /// <summary>跑步基础步频 (steps/s)。</summary>
    public float RunCadenceBase;

    /// <summary>跑步步频随速度的线性增益 (steps/s per m/s)。</summary>
    public float RunCadenceGain;

    /// <summary>走/跑混合带的半宽（相对 vRunStart，0.15 = ±15%）。</summary>
    public float GaitBlendWidth;

    /// <summary>冲刺时的腾空相占比上限。</summary>
    public float MaxFlightFraction;

    /// <summary>行走时垂直起伏的保留系数（膝踝缓冲吸收掉其余部分）。</summary>
    public float WalkAbsorb;

    /// <summary>奔跑支撑相的腿部压缩（相对腿长）。</summary>
    public float RunCompression;

    /// <summary>
    /// 垂直起伏总缩放（舒适度总闸）。
    /// v0.4.2：0.40 → <b>0.24</b>。冲刺（7.6 m/s，带收枪 1.15 倍）峰到峰从
    /// 2.0 cm 降到 ≈1.2 cm —— 需求方反馈「走路/奔跑的持续摇晃还是太剧烈」，
    /// 这一步是主降幅旋钮。
    /// </summary>
    public float VerticalScale;

    /// <summary>侧向摆动 / 步幅。</summary>
    public float LateralPerStride;

    /// <summary>
    /// 起步软着陆跨度 (m/s)：幅度在 <c>MinSpeed → MinSpeed + RampInSpan</c>
    /// 之间用 smoothstep 从 0 升到满值，避免「啪」地开始晃。
    /// </summary>
    public float RampInSpan;

    /// <summary>
    /// 相机摇晃使用的步频上限 (Hz)。相位推进用
    /// <c>min(CadenceHz, BobCadenceMaxHz)</c>：
    /// 冲刺真实步频 3.4 Hz 直接用在画面上会「嗡嗡」抖，压到 3.0 Hz 仍然跟得上
    /// 步点，但明显更稳。<see cref="GaitSample.CadenceHz"/> 本身保持真实值（HUD 用）。
    /// </summary>
    public float BobCadenceMaxHz;

    /// <summary>侧向摆动下限 (m)。</summary>
    public float LateralMin;

    /// <summary>侧向摆动上限 (m)。</summary>
    public float LateralMax;

    /// <summary>点头幅度 (deg per cm 垂直起伏)。</summary>
    public float PitchPerCm;

    /// <summary>侧倾幅度 (deg per cm 侧向摆动)。</summary>
    public float RollPerCm;

    /// <summary>点头幅度上限 (deg)。</summary>
    public float MaxPitchDeg;

    /// <summary>侧倾幅度上限 (deg)。</summary>
    public float MaxRollDeg;

    /// <summary>低于此水平速度完全不晃 (m/s)。</summary>
    public float MinSpeed;

    /// <summary>蹲行时步频的下降比例。</summary>
    public float CrouchCadenceDrop;

    /// <summary>蹲行时幅度的下降比例。</summary>
    public float CrouchAmplitudeDrop;

    /// <summary>模型用的重力常数（**地球重力，不是游戏重力** —— 这是生物力学模型）。</summary>
    public const float EarthGravity = 9.81f;

    /// <summary>默认参数（见类型注释里的数值落点表）。</summary>
    public static GaitParams Default => new()
    {
        LegLengthRatio = 0.53f,
        FroudePref = 0.45f,
        RunStartFroude = 1.9f,
        WalkCadenceRef = 1.9f,
        WalkCadenceExp = 0.6f,
        RunCadenceBase = 2.75f,
        RunCadenceGain = 0.14f,
        GaitBlendWidth = 0.15f,
        MaxFlightFraction = 0.38f,
        WalkAbsorb = 0.16f,
        RunCompression = 0.05f,
        VerticalScale = 0.24f,
        LateralPerStride = 0.004f,
        LateralMin = 0.0025f,
        LateralMax = 0.010f,
        RampInSpan = 0.90f,
        BobCadenceMaxHz = 3.0f,
        PitchPerCm = 0.20f,
        RollPerCm = 0.16f,
        MaxPitchDeg = 0.45f,
        MaxRollDeg = 0.30f,
        MinSpeed = 0.35f,
        CrouchCadenceDrop = 0.35f,
        CrouchAmplitudeDrop = 0.20f,
    };
}

/// <summary>一次步态求值结果（每 tick 写进 <see cref="MovementSnapshot"/>，相机只读不算）。</summary>
public struct GaitSample
{
    /// <summary>步频 (steps/s)：一步一个周期（垂直起伏、点头各一周期/步）。</summary>
    public float CadenceHz;

    /// <summary>步幅 (m/步)。</summary>
    public float StrideLength;

    /// <summary>腾空相占比 0..1（走恒 0）。</summary>
    public float FlightFraction;

    /// <summary>走/跑混合量 0..1。</summary>
    public float RunBlend;

    /// <summary>垂直起伏**半幅** (m)。</summary>
    public float VerticalAmplitude;

    /// <summary>侧向摆动**半幅** (m)。</summary>
    public float LateralAmplitude;

    /// <summary>点头**半幅** (deg)。</summary>
    public float PitchDeg;

    /// <summary>侧倾**半幅** (deg)。</summary>
    public float RollDeg;

    /// <summary>是否处于奔跑步态。</summary>
    public bool Running;
}

/// <summary>
/// 步态求解器（**纯静态数学，无状态**，可直接在自检里断言）。
///
/// 输入：水平速度、当前身高、冲刺参考速度、蹲伏量。
/// 输出：步频 / 步幅 / 腾空比 / 各方向抖动半幅。
/// </summary>
public static class GaitModel
{
    /// <summary>
    /// 求值。`runSpeed` 是角色的冲刺参考速度（用来把"腾空比"归一化到 0..1），
    /// 传 &lt;= 0 时退回 <paramref name="speed"/> × 1.5。
    /// </summary>
    public static GaitSample Evaluate(float speed, float height, float runSpeed,
                                      float crouchAmount, in GaitParams p)
    {
        GaitSample s = default;

        if (height <= 0.1f) height = 1.95f;
        if (runSpeed <= 0.1f) runSpeed = speed * 1.5f;

        // ── 1) 腿长与两个特征速度 ────────────────────────────────
        float lleg = p.LegLengthRatio * height;
        if (lleg <= 0.05f) lleg = 0.05f;

        float vPref = p.FroudePref * MathF.Sqrt(GaitParams.EarthGravity * lleg);
        if (vPref <= 0.05f) vPref = 0.05f;

        float vRunStart = p.RunStartFroude * vPref;

        s.CadenceHz = 0f;
        s.StrideLength = 0f;
        s.FlightFraction = 0f;
        s.RunBlend = 0f;
        s.VerticalAmplitude = 0f;
        s.LateralAmplitude = 0f;
        s.PitchDeg = 0f;
        s.RollDeg = 0f;
        s.Running = false;

        // ── 2) 静止：不晃 ───────────────────────────────────────
        if (speed <= p.MinSpeed) return s;

        // ── 3) 走 / 跑混合量 ────────────────────────────────────
        float lo = vRunStart * (1f - p.GaitBlendWidth);
        float hi = vRunStart * (1f + p.GaitBlendWidth);
        float runBlend = SmoothStep01((speed - lo) / MathF.Max(1e-4f, hi - lo));
        s.RunBlend = runBlend;
        s.Running = runBlend >= 0.5f;

        // ── 4) 步频 ─────────────────────────────────────────────
        // 走：幂律（标定点 v = vPref ⇒ WalkCadenceRef）
        float fWalk = p.WalkCadenceRef * MathF.Pow(speed / vPref, p.WalkCadenceExp);
        // 跑：近似饱和的线性小幅增长
        float fRun = p.RunCadenceBase + p.RunCadenceGain * (speed - vRunStart);
        if (fRun < p.RunCadenceBase) fRun = p.RunCadenceBase;

        float cadenceStand = fWalk + (fRun - fWalk) * runBlend;

        // 蹲行：步频下降（相位变慢）
        // ⚠ 这里**不能**把蹲伏修正混进几何项：若先用"降频后的步频"算步幅
        //   （L = v/f），蹲下会让步幅变长 ⇒ 摆腿角变大 ⇒ 垂直起伏反而变大，
        //   方向完全反了（v0.4 自检 ⑱ 抓到的真实 bug）。正确顺序是：
        //   几何项一律用**站立步频**推，蹲伏只按比例缩小最终幅度。
        float crouch = clamp01(crouchAmount);
        float cadence = cadenceStand * (1f - p.CrouchCadenceDrop * crouch);
        if (cadence < 0.2f) cadence = 0.2f;

        s.CadenceHz = cadence;
        s.StrideLength = speed / cadence;

        // 站立步幅（几何项专用，不含蹲伏修正）
        float strideStand = speed / cadenceStand;

        // ── 5) 腾空比（"现实人物奔跑的略微腾空"） ──────────────────
        float runSpan = MathF.Max(0.1f, runSpeed - vRunStart);
        float flightNorm = clamp01((speed - vRunStart) / runSpan);
        s.FlightFraction = flightNorm * p.MaxFlightFraction;

        // ── 6) 垂直起伏（半幅） ──────────────────────────────────
        // 半步幅角 θ：腿从竖直摆到"步幅一半"的位置。
        // sin θ = (Lstep / 2) / Lleg，夹到 0.98 避免 asin 越界。
        float sinTheta = clamp01(MathF.Min(0.98f, (strideStand * 0.5f) / lleg));
        float theta = MathF.Asin(sinTheta);

        // (a) 走：倒立摆翻越的几何抬升，被膝踝缓冲吸收掉大部分
        float walkTerm = lleg * (1f - MathF.Cos(theta)) * p.WalkAbsorb * (1f - runBlend);

        // (b) 跑：弹道腾空抬升（精确解 h = g·(T_flight/2)²/2）
        //         + 支撑相腿部压缩（相对腿长）
        float period = 1f / cadenceStand;
        float halfFlight = s.FlightFraction * period * 0.5f;
        float ballistic = GaitParams.EarthGravity * halfFlight * halfFlight * 0.5f;
        float runTerm = (ballistic + p.RunCompression * lleg) * s.FlightFraction;

        // 起步/收尾软着陆：速度刚从 MinSpeed 爬起来时幅度不该一步到位。
        float ramp = SmoothStep01((speed - p.MinSpeed) / MathF.Max(1e-4f, p.RampInSpan));

        float vertical = (walkTerm + runTerm) * p.VerticalScale * ramp;
        vertical *= 1f - p.CrouchAmplitudeDrop * crouch;
        s.VerticalAmplitude = vertical;

        // ── 7) 侧向摆动（一个完整步幅左右摆一次） ────────────────
        float lateral = p.LateralPerStride * strideStand * ramp;
        lateral = MathF.Max(p.LateralMin * ramp, MathF.Min(p.LateralMax, lateral));
        lateral *= 1f - p.CrouchAmplitudeDrop * crouch;
        s.LateralAmplitude = lateral;

        // ── 8) 点头 / 侧倾（按幅度折算，各自夹上限） ──────────────
        float pitch = p.PitchPerCm * vertical * 100f;
        s.PitchDeg = MathF.Min(p.MaxPitchDeg, pitch);

        float roll = p.RollPerCm * lateral * 100f;
        s.RollDeg = MathF.Min(p.MaxRollDeg, roll);

        return s;
    }

    /// <summary>由身高求腿长（自检与文档用）。</summary>
    public static float LegLength(float height, in GaitParams p)
        => (height <= 0.1f ? 1.95f : height) * p.LegLengthRatio;

    /// <summary>由身高求偏好行走速度（自检与文档用）。</summary>
    public static float PreferredSpeed(float height, in GaitParams p)
    {
        float lleg = LegLength(height, p);
        return p.FroudePref * MathF.Sqrt(GaitParams.EarthGravity * lleg);
    }

    /// <summary>走→跑切换速度（自检与文档用）。</summary>
    public static float RunStartSpeed(float height, in GaitParams p)
        => p.RunStartFroude * PreferredSpeed(height, p);

    private static float clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

    private static float SmoothStep01(float t)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        return t * t * (3f - 2f * t);
    }
}
