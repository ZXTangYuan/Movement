using System;
using System.Text;
using ApexMovement.Camera;
using ApexMovement.Core;

namespace ApexMovement.Tests;

/// <summary>
/// v0.4 步态模型 / 镜头抖动推导 的专项验收（纯数学，无引擎）。
///
/// 对应需求方 m02704 第 1 条：
/// 「视角抖动频率和幅度还是太剧烈了我建议通过步行和奔跑的速度，以及角色身高对应的
///   步幅步频来获得对应的镜头抖动频率和幅度，奔跑状态的步幅和步频需要考虑现实人物
///   奔跑的略微腾空」
/// 以及 m02752 的澄清答复：「抖动目标 = 舒适档：垂直 ~2 cm、点头 ≤0.8°、
///   落地冲击 1.5–2.5°」。
///
/// ★ v0.4.2：需求方再次反馈「走路/奔跑的持续摇晃还是太剧烈」，判据收紧为
///   **垂直峰到峰 ≤ 1.5 cm、点头峰到峰 ≤ 0.30°**，相机侧相位步频封顶
///   <see cref="GaitParams.BobCadenceMaxHz"/> = 3.0 Hz。本文件的幅度断言
///   已按新口径重写（见 ⑫⑬⑭⑮㉒ 的推导注释），不是"为了变绿而放宽"。
///
/// 本组断言要证明的不是"数看起来像"，而是**频率与幅度确实由身高与速度解释**：
///   · 腿长 = 0.53 × 身高 → Froude 偏好步速 → 走/跑切换阈值 → 步频 → 步幅 → 腾空比
///     → 垂直/侧向幅度 → 点头/侧倾幅度，每一步都有可断言的落点；
///   · 步幅是"由步频反推"（L = v/f），不是独立拍的参数；
///   · 腾空比在走时恒为 0、冲刺时约 38%（"现实人物奔跑的略微腾空"）；
///   · 同一速度下**个子越高步频越低**（腿越长）；
///   · 相机侧只读 <see cref="GaitSample"/>，不再自己拍斜坡（v0.3 的
///     <c>clamp(speed/5,0,1.4)</c> 与 <c>1.2+0.55×speed</c> 已删除）。
/// </summary>
public static class GaitTest
{
    // ───────────────────────── 辅助 ─────────────────────────

    private static GaitParams P => GaitParams.Default;

    /// <summary>插件默认站立身高（需求方口径）。</summary>
    private const float StandHeight = 1.95f;

    /// <summary>插件默认冲刺速度（260 hu/s），用作"腾空比"归一化的上限。</summary>
    private const float RunSpeed = 6.604f;

    private static GaitSample At(float speed, float height = StandHeight, float crouch = 0f)
        => GaitModel.Evaluate(speed, height, RunSpeed, crouch, P);

    private static bool Near(float v, float target, float tol) => MathF.Abs(v - target) <= tol;

    /// <summary>由 yaw 与 up 求水平前向（与 ApexCameraRig 内部同一套基，自检里用来投影眼位偏移）。</summary>
    private static F3 ForwardFromYaw(float yaw, F3 up)
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

    /// <summary>端到端实测的一次采样结果（峰到峰 / 相位频率 / 当时的步态样本）。</summary>
    /// <param name="CyclesPerSec">**竖直**起伏每秒周期数（= 一步一次 ⇒ 应等于相机步频）。</param>
    /// <param name="LatCyclesPerSec">**横向**摆动每秒周期数（= 一步幅一次 ⇒ 应为竖直的一半）。</param>
    private readonly record struct LiveResult(
        float VertP2Pmm, float LatP2Pmm, float PitchP2P, float RollP2P,
        float CyclesPerSec, float LatCyclesPerSec, float Speed, GaitSample Gait);

    /// <summary>
    /// 跑真实的 core + rig 链路：预热后逐 tick 采样相机输出，量出各方向峰到峰。
    /// 这是"相机侧真的用上了新参数"的唯一硬证据 —— 解析式断言只能证明模型算得对。
    /// </summary>
    /// <remarks>
    /// ★ v0.5.3：测量窗口 180 tick(3 s) → **600 tick(10 s)**。
    ///
    /// 过零计数法的频率分辨率 = 0.5 周期 / 窗口长度：
    ///   3 s  → ±0.167 Hz（比断言容差 0.15 还大 ⇒ 相位起点稍微偏一点就红）；
    ///   10 s → ±0.05 Hz（稳稳落在容差内）。
    /// 这不是"放宽断言"：容差没动，改的是**尺子的精度**。之前 180 tick 能过只是
    /// 恰好落在整周期边界上；把驱动加速度从 10 提到 14 以后暖机段的相位积累变了，
    /// 起点偏移就让计数差了半个周期。
    /// </remarks>
    private static LiveResult LiveMeasure(MovementConfig cfg, bool holster, float bobScale,
                                          int warmTicks = 240, int measureTicks = 600)
    {
        var world = new AnalyticWorld(0f);
        var core = new ApexMovementCore(cfg);
        core.BindWorld(world);
        core.Reset(new F3(0f, 1.0f, 0f));

        var camCfg = new CameraEffectsConfig { BobGaitScale = bobScale };
        var rig = new ApexCameraRig(camCfg);
        rig.Reset();

        var input = new MovementInput { MoveZ = 1f, SprintHeld = true, Holstered = holster };
        float dt = cfg.FixedDelta;

        for (int i = 0; i < warmTicks; i++)
        {
            core.Step(input, dt);
            rig.Update(core.Snapshot, dt);
        }

        float minV = float.MaxValue, maxV = float.MinValue;
        float minL = float.MaxValue, maxL = float.MinValue;
        float minP = float.MaxValue, maxP = float.MinValue;
        float minR = float.MaxValue, maxR = float.MinValue;
        int vertSign = 0, latSign = 0, vertCross = 0, latCross = 0;

        for (int i = 0; i < measureTicks; i++)
        {
            core.Step(input, dt);
            CameraOutput o = rig.Update(core.Snapshot, dt);
            MovementSnapshot s = core.Snapshot;

            // v0.5（需求 m01070）：∞ 改由**视线方向**承载，位移不再进入 EyeOffset。
            // 这里直接读相机输出的物理半幅（米），与 v0.4.x 的刻度完全等价，
            // 所以下面所有"毫米"阈值都不需要改。
            float up = o.BobVerticalMeters;
            float lat = o.BobLateralMeters;

            if (up < minV) minV = up;
            if (up > maxV) maxV = up;
            if (lat < minL) minL = lat;
            if (lat > maxL) maxL = lat;
            // v0.5：点头只量 bob 的**点头通道** —— PitchOffset 现在还含"竖直起伏
            // 换算成的视线俯仰"（∞ 的竖直分量），混进来会把点头量成 0.91°。
            if (o.BobPitchRad < minP) minP = o.BobPitchRad;
            if (o.BobPitchRad > maxP) maxP = o.BobPitchRad;
            if (o.BobRollRad < minR) minR = o.BobRollRad;
            if (o.BobRollRad > maxR) maxR = o.BobRollRad;

            // 频率用**过零次数**量，而不是量相位推进速率 ——
            // 相位口径在 v0.4.3 从"步频相位"改成了"步幅相位"（见 BobWaveform），
            // 直接看相位会得到步频的一半，但玩家耳朵/眼睛看到的是**起伏次数**。
            int vs = up > 0f ? 1 : (up < 0f ? -1 : 0);
            if (vs != 0) { if (vertSign != 0 && vs != vertSign) vertCross++; vertSign = vs; }
            int ls = lat > 0f ? 1 : (lat < 0f ? -1 : 0);
            if (ls != 0) { if (latSign != 0 && ls != latSign) latCross++; latSign = ls; }
        }

        float secs = measureTicks * dt;
        const float RadToDeg = 180f / MathF.PI;
        return new LiveResult(
            (maxV - minV) * 1000f, (maxL - minL) * 1000f,
            (maxP - minP) * RadToDeg, (maxR - minR) * RadToDeg,
            vertCross / 2f / secs, latCross / 2f / secs,
            core.HorizontalSpeed, core.Snapshot.Gait);
    }

    // ───────────────────────── 主体 ─────────────────────────

    public static (bool Pass, string Report) Run()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== v0.4 步态模型 / 镜头抖动推导 ===");

        int failures = 0;
        int checks = 0;

        void Check(string name, bool ok, string detail = "")
        {
            checks++;
            if (!ok) failures++;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}" +
                          (detail.Length > 0 ? $"  （{detail}）" : ""));
        }

        // ═════════════ A. 由身高推出的特征量 ═════════════

        // 1. 腿长
        {
            float lleg = GaitModel.LegLength(StandHeight, P);
            Check("① 腿长 = 0.53 × 身高（人体测量学）",
                  Near(lleg, 0.53f * StandHeight, 0.002f),
                  $"H={StandHeight:F2} → 腿长 {lleg:F4} m（0.53×H = {0.53f * StandHeight:F4}）");
        }

        // 2. Froude 偏好步速 —— 模型"不是拍出来"的第一个硬证据
        {
            float vPref = GaitModel.PreferredSpeed(StandHeight, P);
            Check("② Froude 偏好步速 ≈ 1.43 m/s（人类实测舒适步速 1.4）",
                  vPref > 1.25f && vPref < 1.60f,
                  $"vPref = 0.45×√(9.81×腿长) = {vPref:F3} m/s（人类 1.4 ± 0.15）");
        }

        // 3. 走→跑切换阈值
        {
            float vRunStart = GaitModel.RunStartSpeed(StandHeight, P);
            Check("③ 走→跑切换阈值 ≈ 2.7 m/s（人类实测 2.0–2.7）",
                  vRunStart > 2.2f && vRunStart < 3.0f,
                  $"vRunStart = 1.9 × vPref = {vRunStart:F3} m/s");
        }

        // 4. 身高越高，偏好步速越快（腿长 → 摆腿周期 → 步速）
        {
            float vShort = GaitModel.PreferredSpeed(1.60f, P);
            float vTall = GaitModel.PreferredSpeed(2.20f, P);
            Check("④ 个子越高偏好步速越快（腿长 ∝ 摆腿周期）",
                  vTall > vShort + 0.10f,
                  $"1.60m → {vShort:F3} m/s；2.20m → {vTall:F3} m/s");
        }

        // ═════════════ B. 静止 / 走 / 跑 三态 ═════════════

        // 5. 静止不晃
        {
            GaitSample s = At(0.20f);
            bool zero = s.CadenceHz == 0f && s.VerticalAmplitude == 0f &&
                        s.LateralAmplitude == 0f && s.PitchDeg == 0f && !s.Running;
            Check("⑤ 低于 MinSpeed(0.35 m/s) 完全不晃（全零，非 Running）",
                  zero, $"speed=0.20 → cadence={s.CadenceHz:F2} vAmp={s.VerticalAmplitude * 1000f:F2}mm");
        }

        // 6. 偏好步速处：走、步频 1.9、无腾空
        {
            GaitSample s = At(GaitModel.PreferredSpeed(StandHeight, P));
            Check("⑥ 偏好步速处是「走」：步频 ≈1.9 Hz、腾空比 = 0、不是 Running",
                  Near(s.CadenceHz, 1.90f, 0.05f) && s.FlightFraction == 0f && !s.Running,
                  $"cadence={s.CadenceHz:F2} Hz flight={s.FlightFraction:F3} running={s.Running}");
        }

        // 7. 步幅是"由步频反推"，不是独立参数
        {
            float speed = 4.407f;
            GaitSample s = At(speed);
            Check("⑦ 步幅 = 速度 / 步频（由步频反推，不是独立拍的）",
                  Near(s.StrideLength, speed / s.CadenceHz, 1e-4f),
                  $"v={speed:F3} / f={s.CadenceHz:F3} = {s.StrideLength:F3} m（模型 {s.StrideLength:F3}）");
        }

        // 8. 本插件行走速度 4.407 m/s 落在跑步区间
        {
            GaitSample s = At(4.407f);
            Check("⑧ 行走速度 4.407 m/s 已是「跑」：步频 2.8–3.2 Hz、步幅 1.3–1.7 m、有腾空",
                  s.Running && s.CadenceHz > 2.8f && s.CadenceHz < 3.2f &&
                  s.StrideLength > 1.3f && s.StrideLength < 1.7f &&
                  s.FlightFraction > 0.08f,
                  $"cadence={s.CadenceHz:F2} Hz stride={s.StrideLength:F2} m flight={s.FlightFraction:F3}");
        }

        // 9. 冲刺 6.604 m/s
        {
            GaitSample s = At(RunSpeed);
            Check("⑨ 冲刺 6.604 m/s：步频 3.1–3.5 Hz、腾空比 ≈0.38（MaxFlightFraction）",
                  s.Running && s.CadenceHz > 3.1f && s.CadenceHz < 3.5f &&
                  Near(s.FlightFraction, P.MaxFlightFraction, 0.02f),
                  $"cadence={s.CadenceHz:F2} Hz flight={s.FlightFraction:F3}（上限 {P.MaxFlightFraction:F2}）");
        }

        // 10. 步频随速度单调递增，且不会超过人类跑步上限
        {
            float[] speeds = { 1.0f, 1.43f, 2.0f, 2.72f, 3.5f, 4.407f, 5.5f, RunSpeed };
            bool mono = true;
            float prev = 0f;
            for (int i = 0; i < speeds.Length; i++)
            {
                float f = At(speeds[i]).CadenceHz;
                if (i > 0 && f <= prev) mono = false;
                prev = f;
            }
            float fMax = At(RunSpeed).CadenceHz;
            Check("⑩ 步频随速度单调递增，且冲刺时 < 4 Hz（人类跑步上限约 3.5）",
                  mono && fMax < 4.0f,
                  $"1.0→{At(1.0f).CadenceHz:F2} / 4.41→{At(4.407f).CadenceHz:F2} / 6.60→{fMax:F2} Hz（单调={mono}）");
        }

        // 11. 腾空比：走恒 0、跑随速度增长、冲刺到上限
        {
            float fWalk = At(1.43f).FlightFraction;
            float fMid = At(4.407f).FlightFraction;
            float fTop = At(RunSpeed).FlightFraction;
            Check("⑪ 腾空比：走恒 0 → 跑随速度增长 → 冲刺到 MaxFlightFraction",
                  fWalk == 0f && fMid > 0f && fTop > fMid,
                  $"1.43m/s={fWalk:F3} 4.41m/s={fMid:F3} 6.60m/s={fTop:F3}");
        }

        // ═════════════ C. 幅度落点（舒适档） ═════════════

        // 12. 垂直半幅：走"看得见但温和"、跑"更强"，且都在收紧后的舒适档内
        //
        // v0.4.2 把舒适档从「峰到峰 ≤ 2.5 cm」收紧到「≤ 1.5 cm」，同时把
        // VerticalScale 0.40 → 0.24。旧断言写的是"冲刺半幅 > 7 mm"（= 峰到峰
        // > 1.4 cm，贴着旧上限），与新判据**直接矛盾** —— 冲刺一旦压到
        // 1.5 cm 以内，旧断言必然红。所以这里按新口径重写，并且保留三件真实性质：
        //   (a) 走 / 跑都必须**存在**（峰到峰有下限，不能靠"压没了"蒙混）；
        //   (b) 冲刺不得超过新上限 1.5 cm（舒适档）；
        //   (c) 跑必须比走**明显更晃**（否则走跑不分，就不是步态驱动了）。
        {
            float walkP2P = 2f * At(1.43f).VerticalAmplitude;
            float runP2P = 2f * At(RunSpeed).VerticalAmplitude;
            const float walkLo = 0.004f;   // 走：≥4 mm 峰到峰 —— 再小就"看不见"，等于功能被压没
            const float walkHi = 0.008f;   // 走：≤8 mm 峰到峰 —— 走不该晃得像跑
            const float runLo = 0.008f;    // 跑：≥8 mm 峰到峰 —— 跑必须明显比走强
            const float runHi = 0.015f;    // 跑：≤15 mm 峰到峰 —— v0.4.2 舒适档硬上限
            Check("⑫ 垂直峰到峰：慢走 ∈[4,8]mm / 冲刺 ∈[8,15]mm / 跑 > 走×1.5",
                  walkP2P >= walkLo && walkP2P <= walkHi &&
                  runP2P >= runLo && runP2P <= runHi &&
                  runP2P > walkP2P * 1.5f,
                  $"1.43m/s={walkP2P * 1000f:F2}mm（区间 4–8）/ 6.60m/s={runP2P * 1000f:F2}mm（区间 8–15）" +
                  $"/ 跑÷走 = {runP2P / walkP2P:F2}×");
        }

        // 13. 冲刺峰到峰不超过 v0.4.2 **收紧后**的 1.5 cm 舒适上限
        //     （旧断言用的 2.5 cm 是 m02752 的第一版口径，已被需求方推翻）
        {
            float peakToPeak = 2f * At(RunSpeed).VerticalAmplitude;
            Check("⑬ 冲刺时垂直峰到峰 ≤ 1.5 cm（v0.4.2 收紧后的舒适档上限）",
                  peakToPeak <= 0.015f,
                  $"峰到峰 = 2×{At(RunSpeed).VerticalAmplitude * 1000f:F2}mm = {peakToPeak * 1000f:F2}mm（上限 15mm）");
        }

        // 14. 点头：峰到峰 ≤ 0.30°（v0.4.2 舒适档），且确实存在、上限只是保险丝
        //
        // 旧断言要求「半幅 ≥ 0.30°」，把**半幅**当成了舒适档指标 —— 但 v0.4.2 的
        // 判据是**峰到峰 ≤ 0.30°**（即半幅 ≤ 0.15°）。两者差 2 倍，旧断言必然红。
        // 重写后保留三件真实性质，谁也不掩盖谁：
        //   (a) 峰到峰 ≤ 0.30°（新舒适档）；
        //   (b) 半幅 ≥ 0.05° —— 点头确实存在、没被压没（冲刺 6.2 mm 垂直半幅 ×
        //       0.20 °/cm = 0.124°，下限取 0.05° 是"还有一点点"而不是"归零"）；
        //   (c) MaxPitchDeg 只作**保险丝**（未成为工作点）—— 否则"舒适档达标"
        //       只是被 clamp 掩盖的结果，换个速度就爆。
        {
            float pitchRun = At(RunSpeed).PitchDeg;
            const float capP2P = 0.30f;
            Check("⑭ 冲刺点头：峰到峰 ≤0.30°（v0.4.2 舒适档）且半幅 ≥0.05°（存在、未被压没）",
                  pitchRun * 2f <= capP2P + 1e-5f && pitchRun >= 0.05f,
                  $"冲刺点头半幅 {pitchRun:F3}°（峰到峰 {pitchRun * 2f:F3}°，上限 {capP2P:F2}°）");
            Check("⑭b MaxPitchDeg 只作保险丝（未成为工作点，否则舒适档达标是被 clamp 掩盖的）",
                  pitchRun < P.MaxPitchDeg * 0.9f,
                  $"半幅 {pitchRun:F3}° < MaxPitchDeg {P.MaxPitchDeg:F2}° × 0.9 = {P.MaxPitchDeg * 0.9f:F3}°");
        }

        // 15. 侧向摆动：稳态夹在 [LateralMin, LateralMax]；起步带按 ramp 软起
        //
        // 旧断言在 0.5 m/s 处取样，那时旧代码给的是硬下限 4 mm。v0.4.2 把下限改成
        // LateralMin × ramp（起步软着陆），0.5 m/s 处 ramp ≈ 0.06 ⇒ 只有 0.2 mm。
        // 这是**有意的**：一步跨出 4 mm 的侧向瞬移正是"啪"地开始晃的来源。
        // 重写后把两条性质分开断言，谁也不会掩盖谁：
        //   (a) 硬上限：所有速度都 ≤ LateralMax（10 mm 是真上限，不是摆设）；
        //   (b) 软起步：0.5/0.8/1.0 m/s 处 < LateralMin（被 ramp 压着，未到硬下限）；
        //   (c) 稳态区间：≥ 偏好步速后一律 ∈ [LateralMin, LateralMax]。
        {
            float[] rampSpeeds = { 0.5f, 0.8f, 1.0f };
            bool rampBelowMin = true;
            for (int i = 0; i < rampSpeeds.Length; i++)
                if (At(rampSpeeds[i]).LateralAmplitude >= P.LateralMin) rampBelowMin = false;

            float[] steadySpeeds = { 1.43f, 3f, 4.407f, RunSpeed };
            bool steadyInRange = true;
            float lo = float.MaxValue, hi = 0f;
            for (int i = 0; i < steadySpeeds.Length; i++)
            {
                float lat = At(steadySpeeds[i]).LateralAmplitude;
                if (lat < P.LateralMin - 1e-6f || lat > P.LateralMax + 1e-6f) steadyInRange = false;
                if (lat < lo) lo = lat;
                if (lat > hi) hi = lat;
            }

            float[] allSpeeds = { 0.5f, 0.8f, 1.0f, 1.43f, 3f, 4.407f, RunSpeed };
            bool hardBounded = true;
            for (int i = 0; i < allSpeeds.Length; i++)
            {
                float lat = At(allSpeeds[i]).LateralAmplitude;
                if (lat < 0f || lat > P.LateralMax + 1e-6f) hardBounded = false;
            }

            Check("⑮ 侧向摆动：稳态 ∈[LateralMin 2.5mm, LateralMax 10mm]、起步带按 ramp 软起、全程 ≤LateralMax",
                  steadyInRange && rampBelowMin && hardBounded,
                  $"稳态区间 [{lo * 1000f:F2}mm, {hi * 1000f:F2}mm]；起步带 0.5/0.8/1.0 m/s = " +
                  $"{At(0.5f).LateralAmplitude * 1000f:F2}/{At(0.8f).LateralAmplitude * 1000f:F2}/" +
                  $"{At(1.0f).LateralAmplitude * 1000f:F2}mm（均 < LateralMin {P.LateralMin * 1000f:F1}mm）");
        }

        // 16. 侧倾半幅 ≤ MaxRollDeg（v0.4.2：0.35° → 0.30°）
        //     （滑铲侧倾已删，走路侧倾是唯一的 roll 来源）
        {
            float rollRun = At(RunSpeed).RollDeg;
            Check("⑯ 走路侧倾半幅 ≤ MaxRollDeg(0.30°) 且未被 clamp 掩盖",
                  rollRun <= P.MaxRollDeg + 1e-5f && rollRun < P.MaxRollDeg * 0.9f,
                  $"冲刺侧倾半幅 {rollRun:F3}°（上限 {P.MaxRollDeg:F2}°，保险丝未触发）");
        }

        // 17. 同一速度下，个子越高步频越低（腿越长 → 摆动周期越长）
        {
            float fShort = At(4.407f, 1.60f).CadenceHz;
            float fMid = At(4.407f, 1.95f).CadenceHz;
            float fTall = At(4.407f, 2.20f).CadenceHz;
            Check("⑰ 同一速度下个子越高步频越低（1.60 > 1.95 > 2.20 m）",
                  fShort > fMid && fMid > fTall,
                  $"1.60m={fShort:F3} / 1.95m={fMid:F3} / 2.20m={fTall:F3} Hz");
        }

        // 18. 蹲行：步频 ×(1-0.35)、幅度 ×(1-0.20)
        {
            GaitSample stand = At(2.0f, StandHeight, 0f);
            GaitSample crouch = At(2.0f, StandHeight, 1f);
            float expectF = stand.CadenceHz * (1f - P.CrouchCadenceDrop);
            float expectA = stand.VerticalAmplitude * (1f - P.CrouchAmplitudeDrop);
            Check("⑱ 蹲行：步频 ×0.65、垂直幅度 ×0.80（重心低、步幅小）",
                  Near(crouch.CadenceHz, expectF, 1e-3f) &&
                  Near(crouch.VerticalAmplitude, expectA, 1e-6f),
                  $"cadence {stand.CadenceHz:F3}→{crouch.CadenceHz:F3}（期望 {expectF:F3}）、" +
                  $"vAmp {stand.VerticalAmplitude * 1000f:F2}→{crouch.VerticalAmplitude * 1000f:F2}mm");
        }

        // 19. 兜底：非法身高 / 非法 runSpeed 不产生 NaN
        {
            GaitSample a = GaitModel.Evaluate(4f, 0f, 0f, 0f, P);
            GaitSample b = GaitModel.Evaluate(4f, -1f, -5f, 0f, P);
            bool finite = float.IsFinite(a.CadenceHz) && float.IsFinite(a.StrideLength) &&
                          float.IsFinite(a.VerticalAmplitude) && float.IsFinite(b.CadenceHz) &&
                          float.IsFinite(b.VerticalAmplitude);
            Check("⑲ 兜底：身高 ≤0.1m 退回 1.95m、runSpeed ≤0 退回 1.5×speed（不出 NaN/Inf）",
                  finite && a.CadenceHz > 0f,
                  $"height=0 → cadence={a.CadenceHz:F3} Hz；height=-1/runSpeed=-5 → cadence={b.CadenceHz:F3} Hz");
        }

        // 20. 参数区间自检（常数不是随手填的；v0.4.2 新增的三个旋钮也要有界）
        {
            bool ok = P.LegLengthRatio is > 0.45f and < 0.62f
                   && P.FroudePref is > 0.30f and < 0.60f
                   && P.RunStartFroude is > 1.2f and < 2.5f
                   && P.WalkCadenceRef is > 1.6f and < 2.2f
                   && P.MaxFlightFraction is > 0f and <= 0.50f
                   && P.MaxPitchDeg is > 0f and <= 1.0f
                   && P.MaxRollDeg is > 0f and <= 1.0f
                   && P.MinSpeed is > 0f and < 1.0f
                   // v0.4.2 新增 / 改动的旋钮
                   && P.VerticalScale is > 0f and <= 1.0f
                   && P.RampInSpan is > 0f and <= 3.0f
                   && P.BobCadenceMaxHz is > 1.5f and < 4.0f
                   && P.LateralMin is > 0f and < 0.05f
                   && P.LateralMax is > 0f and < 0.05f
                   && P.LateralMin < P.LateralMax
                   && P.PitchPerCm is > 0f and <= 2.0f
                   && P.RollPerCm is > 0f and <= 2.0f;
            Check("⑳ 模型常数都在文献 / 手感区间内（腿长比 0.53、Froude 0.45、腾空 ≤0.5、步频封顶 1.5–4 Hz …）",
                  ok,
                  $"legRatio={P.LegLengthRatio:F2} froude={P.FroudePref:F2} " +
                  $"runStart={P.RunStartFroude:F1} flight≤{P.MaxFlightFraction:F2} " +
                  $"pitch≤{P.MaxPitchDeg:F2}° roll≤{P.MaxRollDeg:F2}° vScale={P.VerticalScale:F2} " +
                  $"ramp={P.RampInSpan:F2} bobCap={P.BobCadenceMaxHz:F1}Hz " +
                  $"lat=[{P.LateralMin * 1000f:F1}, {P.LateralMax * 1000f:F1}]mm " +
                  $"pitchPerCm={P.PitchPerCm:F2} rollPerCm={P.RollPerCm:F2}");
        }

        // ═════════════ D. 与移动核心的联动（快照 / 相位） ═════════════

        // 21. 快照里的 Gait 与模型一致；静止时为 0
        {
            var world = new AnalyticWorld(0f);
            var core = new ApexMovementCore(MovementConfig.Apex());
            core.BindWorld(world);
            core.Reset(new F3(0f, 1.0f, 0f));
            for (int i = 0; i < 30; i++) core.Step(MovementInput.Empty, core.Config.FixedDelta);

            bool idleZero = core.Snapshot.Gait.CadenceHz == 0f;

            for (int i = 0; i < 90; i++)
                core.Step(new MovementInput { MoveZ = 1f }, core.Config.FixedDelta);
            GaitSample live = core.Snapshot.Gait;
            GaitSample expect = GaitModel.Evaluate(core.HorizontalSpeed, core.Config.StandHeight,
                                                   MathF.Max(core.Config.WalkSpeed, core.Config.SprintSpeed),
                                                   0f, core.Config.Gait);
            Check("㉑ 快照里的 Gait 来自模型求值（静止为 0，走动后 cadence/vAmp > 0）",
                  idleZero && live.CadenceHz > 0f && live.VerticalAmplitude > 0f &&
                  Near(live.CadenceHz, expect.CadenceHz, 0.05f),
                  $"静止 cadence=0；走动 v={core.HorizontalSpeed:F2} → cadence={live.CadenceHz:F2} Hz " +
                  $"(模型 {expect.CadenceHz:F2}) vAmp={live.VerticalAmplitude * 1000f:F1}mm");
        }

        // 22. 频率口径 = min(步频, BobCadenceMaxHz)（v0.4.3 重新定义）
        //
        // v0.4.2 在 UpdateBobPhase 里加了 camCadence = min(CadenceHz, BobCadenceMaxHz)：
        // 真实冲刺步频 3.29 Hz 直接推相位会在画面上「嗡嗡」抖，压到 3.0 Hz 更稳；
        // HUD 里的 CadenceHz 仍报真实值（3.29）。
        //
        // ★ v0.4.3 把相位**改口径**成「步幅相位」（2π = 一个步幅 = 左右各一步），
        //   于是相位推进速率变成步频的一半 —— 再断言「相位 = 步频」已经没有意义。
        //   真正该断言的是**玩家眼睛看到的两个频率**（都用相机输出的过零次数量）：
        //     (a) 竖直起伏 = camCadence（一步一次）
        //     (b) 横向摆动 = camCadence / 2（一步幅一次）
        //   两者之比必须是 2:1 —— 这正是屏幕平面能画出 ∞ 的必要条件。
        //   另外仍要验证"封顶确实被走到"（真实步频 > 上限），否则断言空过。
        {
            LiveResult m22 = LiveMeasure(MovementConfig.Apex(), holster: false, bobScale: 1f);
            float f0 = m22.Gait.CadenceHz;
            float cap = MovementConfig.Apex().Gait.BobCadenceMaxHz;
            float expect = MathF.Min(f0, cap);
            Check("㉒ 相机实测：竖直起伏 = min(步频, BobCadenceMaxHz)，横向摆动 = 它的一半（2:1）",
                  Near(m22.CyclesPerSec, expect, 0.15f) &&
                  Near(m22.LatCyclesPerSec, expect * 0.5f, 0.15f) &&
                  f0 > cap,
                  $"1 秒内：竖直 {m22.CyclesPerSec:F2} 个周期、横向 {m22.LatCyclesPerSec:F2} 个（比 " +
                  $"{(m22.LatCyclesPerSec > 1e-4f ? m22.CyclesPerSec / m22.LatCyclesPerSec : 0f):F2}:1）；" +
                  $"min({f0:F2}, {cap:F2}) = {expect:F2} Hz（真实步频已超上限 ⇒ 封顶生效）");
        }

        // 23. 滑铲时步态按"蹲行"算，不按 10 m/s 的真实滑行速度算
        {
            var world = new AnalyticWorld(0f);
            var core = new ApexMovementCore(MovementConfig.Apex());
            core.BindWorld(world);
            core.Reset(new F3(0f, 1.0f, 0f));
            for (int i = 0; i < 120; i++)
                core.Step(new MovementInput { MoveZ = 1f, SprintHeld = true }, core.Config.FixedDelta);
            core.Step(new MovementInput { MoveZ = 1f, SprintHeld = true, CrouchHeld = true },
                      core.Config.FixedDelta);
            for (int i = 0; i < 10; i++)
                core.Step(new MovementInput { MoveZ = 1f, CrouchHeld = true }, core.Config.FixedDelta);

            bool sliding = core.State == MovementState.Slide;
            float cadence = core.Snapshot.Gait.CadenceHz;
            Check("㉓ 滑铲时步态按蹲行速度算（步频 ≤2.0 Hz，不跟着 10 m/s 滑行速度飙）",
                  sliding && cadence > 0f && cadence <= 2.0f,
                  $"state={core.State} speed={core.HorizontalSpeed:F2} → cadence={cadence:F2} Hz");
        }

        // ═════════════ E. 相机侧真的用上了新参数（端到端，实测而非解析） ═════════════

        // 24. 冲刺（含收枪 1.15 倍速，最坏档）的相机实际输出 = 模型推导，且落在 v0.4.2 舒适档
        //
        // 前面 ⑫⑭ 断言的是**模型**给的值。这一条走完整链路：
        //   AnalyticWorld → ApexMovementCore（UpdateBobPhase 推相位）
        //   → MovementSnapshot.Gait/BobPhase → ApexCameraRig.BuildOutput（读幅度）
        // 用 60 Hz 逐 tick 采样 3 秒，从**相机输出本身**量峰到峰。
        // 它能抓到 ⑫⑭ 抓不到的东西：相机没读 Gait、幅度单位搞错（mm/cm）、
        // 相位与幅度用了不同的步频等等。
        {
            var cfg = MovementConfig.Apex();
            var m = LiveMeasure(cfg, holster: true, bobScale: 1f);
            GaitSample g = m.Gait;
            float expectV = 2f * g.VerticalAmplitude * 1000f;
            float expectP = 2f * g.PitchDeg;
            float expectL = 2f * g.LateralAmplitude * 1000f;
            float camCad = MathF.Min(g.CadenceHz, cfg.Gait.BobCadenceMaxHz);

            bool ampOk = Near(m.VertP2Pmm, expectV, expectV * 0.05f) &&
                         Near(m.LatP2Pmm, expectL, expectL * 0.05f) &&
                         Near(m.PitchP2P, expectP, expectP * 0.05f);
            bool comfortOk = m.VertP2Pmm <= 15f && m.PitchP2P <= 0.30f;
            // 频率口径（v0.4.3）：竖直 = camCadence、横向 = camCadence/2，见 ㉒ 的说明。
            bool freqOk = Near(m.CyclesPerSec, camCad, 0.15f) &&
                          Near(m.LatCyclesPerSec, camCad * 0.5f, 0.15f);

            Check("㉔ 收枪冲刺端到端：相机实测峰到峰 = 模型推导（≤5% 偏差），且垂直 ≤1.5cm / 点头 ≤0.30°",
                  ampOk && comfortOk && freqOk,
                  $"speed={m.Speed:F2} m/s → 垂直 {m.VertP2Pmm:F2}mm（模型 {expectV:F2}）、" +
                  $"侧向 {m.LatP2Pmm:F2}mm（模型 {expectL:F2}）、" +
                  $"点头 {m.PitchP2P:F3}°（模型 {expectP:F3}）、" +
                  $"侧倾 {m.RollP2P:F3}°、竖直 {m.CyclesPerSec:F2} Hz / 横向 {m.LatCyclesPerSec:F2} Hz" +
                  $"（camCadence {camCad:F2}）");
        }

        // 25. 调试菜单的「步态摇晃倍率」(BobGaitScale) 真的接在相机输出上
        //
        // 需求要求这个滑条"默认 1.0 / 范围 0–2"且能实时改摇晃。只断言字段默认值
        // 是没用的 —— 必须证明 k 确实乘进了 BuildOutput。所以量三档：
        //   k=0 → 摇晃完全关掉（峰到峰 ≈ 0）
        //   k=1 → 物理值
        //   k=0.5 → 恰好一半（线性，不是被别的东西夹住）
        {
            var cfg = MovementConfig.Apex();
            LiveResult k0 = LiveMeasure(cfg, holster: false, bobScale: 0f);
            LiveResult k1 = LiveMeasure(cfg, holster: false, bobScale: 1f);
            LiveResult kh = LiveMeasure(cfg, holster: false, bobScale: 0.5f);
            float ratio = k1.VertP2Pmm > 1e-6f ? kh.VertP2Pmm / k1.VertP2Pmm : 0f;
            Check("㉕ BobGaitScale 线性生效：0× 关掉摇晃、0.5× = 1× 的一半（默认 1.0，范围 0–2）",
                  k0.VertP2Pmm < 0.01f && Near(ratio, 0.5f, 0.02f),
                  $"k=0 → {k0.VertP2Pmm:F3}mm；k=1 → {k1.VertP2Pmm:F2}mm；" +
                  $"k=0.5 → {kh.VertP2Pmm:F2}mm（比值 {ratio:F3}）");
        }

        sb.AppendLine();
        sb.AppendLine($"  合计 {checks} 项断言，失败 {failures} 项");
        return (failures == 0, sb.ToString());
    }
}
