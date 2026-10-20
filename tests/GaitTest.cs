using System;
using System.Text;
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

        // 12. 垂直半幅落在舒适档（~2 cm）
        {
            GaitSample walk = At(1.43f);
            GaitSample run = At(RunSpeed);
            Check("⑫ 垂直起伏半幅：慢走 ~4.5 mm / 冲刺 ~10 mm（峰到峰 ≈2 cm）",
                  walk.VerticalAmplitude > 0.002f && walk.VerticalAmplitude < 0.008f &&
                  run.VerticalAmplitude > 0.007f && run.VerticalAmplitude < 0.014f,
                  $"1.43m/s={walk.VerticalAmplitude * 1000f:F1}mm / 6.60m/s={run.VerticalAmplitude * 1000f:F1}mm");
        }

        // 13. 峰值（峰到峰）不超过 2.5 cm 的舒适上限
        {
            float peakToPeak = 2f * At(RunSpeed).VerticalAmplitude;
            Check("⑬ 冲刺时垂直峰到峰 ≤ 2.5 cm（m02752 舒适档上限）",
                  peakToPeak <= 0.025f,
                  $"峰到峰 = 2×{At(RunSpeed).VerticalAmplitude * 1000f:F1}mm = {peakToPeak * 1000f:F1}mm");
        }

        // 14. 点头半幅 ≤ 0.8°，且冲刺时确实存在（不是被压没）
        {
            float pitchRun = At(RunSpeed).PitchDeg;
            Check("⑭ 点头半幅 ≤ MaxPitchDeg(0.8°) 且冲刺时 ≥0.3°（存在但轻）",
                  pitchRun <= P.MaxPitchDeg + 1e-5f && pitchRun >= 0.30f,
                  $"冲刺点头半幅 {pitchRun:F3}°（上限 {P.MaxPitchDeg:F2}°，要求 ≥0.30°）");
        }

        // 15. 侧向摆动被夹在 [LateralMin, LateralMax]
        {
            bool allInRange = true;
            float lo = float.MaxValue, hi = 0f;
            float[] speeds = { 0.5f, 1.43f, 3f, 4.407f, RunSpeed };
            for (int i = 0; i < speeds.Length; i++)
            {
                float lat = At(speeds[i]).LateralAmplitude;
                if (lat < P.LateralMin - 1e-6f || lat > P.LateralMax + 1e-6f) allInRange = false;
                if (lat < lo) lo = lat;
                if (lat > hi) hi = lat;
            }
            Check("⑮ 侧向摆动被夹在 [LateralMin 4mm, LateralMax 14mm]",
                  allInRange, $"实测区间 [{lo * 1000f:F1}mm, {hi * 1000f:F1}mm]");
        }

        // 16. 侧倾半幅 ≤ 0.35°（滑铲侧倾已删，走路侧倾是唯一的 roll 来源）
        {
            float rollRun = At(RunSpeed).RollDeg;
            Check("⑯ 走路侧倾半幅 ≤ MaxRollDeg(0.35°)",
                  rollRun <= P.MaxRollDeg + 1e-5f,
                  $"冲刺侧倾半幅 {rollRun:F3}°（上限 {P.MaxRollDeg:F2}°）");
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

        // 20. 参数区间自检（常数不是随手填的）
        {
            bool ok = P.LegLengthRatio is > 0.45f and < 0.62f
                   && P.FroudePref is > 0.30f and < 0.60f
                   && P.RunStartFroude is > 1.2f and < 2.5f
                   && P.WalkCadenceRef is > 1.6f and < 2.2f
                   && P.MaxFlightFraction is > 0f and <= 0.50f
                   && P.MaxPitchDeg is > 0f and <= 1.0f
                   && P.MaxRollDeg is > 0f and <= 1.0f
                   && P.MinSpeed is > 0f and < 1.0f;
            Check("⑳ 模型常数都在文献区间内（腿长比 0.53 / Froude 0.45 / 腾空 ≤0.5 …）",
                  ok,
                  $"legRatio={P.LegLengthRatio:F2} froude={P.FroudePref:F2} " +
                  $"runStart={P.RunStartFroude:F1} flight≤{P.MaxFlightFraction:F2} " +
                  $"pitch≤{P.MaxPitchDeg:F2}° roll≤{P.MaxRollDeg:F2}°");
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

        // 22. 相位推进速率 = 步频（帧率无关的基础：相位在 tick 内推进）
        {
            var world = new AnalyticWorld(0f);
            var core = new ApexMovementCore(MovementConfig.Apex());
            core.BindWorld(world);
            core.Reset(new F3(0f, 1.0f, 0f));
            for (int i = 0; i < 120; i++)
                core.Step(new MovementInput { MoveZ = 1f, SprintHeld = true }, core.Config.FixedDelta);

            float f0 = core.Snapshot.Gait.CadenceHz;
            float p0 = core.Snapshot.BobPhase;
            int ticks = 60;   // 1 秒
            float dt = core.Config.FixedDelta;
            float prev = p0;
            float unwrapped = 0f;
            for (int i = 0; i < ticks; i++)
            {
                core.Step(new MovementInput { MoveZ = 1f, SprintHeld = true }, dt);
                float p = core.Snapshot.BobPhase;
                float d = p - prev;
                if (d < -MathF.PI) d += MathF.PI * 2f;   // 跨 2π 回绕
                unwrapped += d;
                prev = p;
            }
            float cycles = unwrapped / (MathF.PI * 2f);
            Check("㉒ 步态相位每秒推进 = 步频周期数（相位在 tick 内推进，帧率无关）",
                  MathF.Abs(cycles - f0) < 0.15f,
                  $"1 秒推进 {cycles:F2} 个周期，步频 {f0:F2} Hz（差 {MathF.Abs(cycles - f0):F3}）");
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

        sb.AppendLine();
        sb.AppendLine($"  合计 {checks} 项断言，失败 {failures} 项");
        return (failures == 0, sb.ToString());
    }
}
