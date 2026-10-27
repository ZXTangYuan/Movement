using System;
using System.Collections.Generic;
using System.Text;
using ApexMovement.Core;

namespace ApexMovement.Tests;

/// <summary>
/// 确定性验证套件（纯 C#，不依赖 Godot）。
///
/// 核心命题：**同一输入序列 + 同一世界 ⇒ 同一轨迹，与渲染帧率无关。**
///
/// 验证方法：
/// 1. 用不同"帧率"把同一段按秒计时的输入切成不同的累积块，
/// 再各自切成固定 tick —— 模拟 30/60/144/240/1000 fps。
/// 2. 记录每个 tick 结束时的位置/速度。
/// 3. 逐 tick 比对：必须**逐位相等**（不是近似）。
/// </summary>
public static class DeterminismTest
{
    /// <summary>一段按真实时间描述的操作脚本。</summary>
    public sealed class Script
    {
        public readonly List<(float Time, MovementAction Action)> Steps = new();

        public Script Add(float time, MovementAction action)
        {
            Steps.Add((time, action));
            return this;
        }
    }

    public enum MovementAction
    {
        Idle, Forward, Jump, Sprint, SprintCrouch, Crouch, Back,
    }

    /// <summary>把脚本在 <paramref name="time"/> 时刻映射成一份输入。</summary>
    private static MovementInput SampleAt(Script script, float time, float prevTime,
                                          MovementConfig cfg, out bool jumpEdge)
    {
        MovementAction south = MovementAction.Idle;
        float best = -1f;
        foreach ((float t, MovementAction a) in script.Steps)
        {
            if (t <= time && t > best) { best = t; south = a; }
        }
        MovementAction prev = MovementAction.Idle;
        float bestPrev = -1f;
        foreach ((float t, MovementAction a) in script.Steps)
        {
            if (t <= prevTime && t > bestPrev) { bestPrev = t; prev = a; }
        }

        jumpEdge = south == MovementAction.Jump && prev != MovementAction.Jump;

        return new MovementInput
        {
            MoveX = 0f,
            MoveZ = south == MovementAction.Back ? -1f : MovementAction.Idle == south ? 0f : 1f,
            JumpPressed = jumpEdge,
            CrouchHeld = south is MovementAction.Crouch or MovementAction.SprintCrouch,
            SprintHeld = south is MovementAction.Sprint or MovementAction.SprintCrouch,
        };
    }

    /// <summary>
    /// 以给定"帧率"跑完脚本，返回每个 tick 结束时的状态快照。
    /// </summary>
    public static List<(F3 Pos, F3 Vel)> RunAtFrameRate(
        Script script, float seconds, float fps, MovementConfig cfg, AnalyticWorld world)
    {
        var core = new ApexMovementCore(cfg);
        core.BindWorld(world);
        core.Reset(new F3(0f, 1.2f, 0f));

        var samples = new List<(F3, F3)>();

        float frameDelta = 1f / fps;
        var clock = new ApexTickClock(cfg.FixedDelta, maxCatchUpTicks: 1000);

        float elapsed = 0f;
        int tick = 0;

        // 用帧数循环，避免浮点累加误差影响"帧率"本身的语义
        int totalFrames = (int)MathF.Ceiling(seconds * fps);
        for (int f = 0; f < totalFrames; f++)
        {
            float frameTime = f * frameDelta;
            clock.Accumulate(frameDelta);

            while (clock.TryConsumeTick(out float dt))
            {
                float tickTime = tick * cfg.FixedDelta;
                MovementInput input = SampleAt(script, tickTime,
                                               (tick - 1) * cfg.FixedDelta, cfg, out _);
                core.Step(input, dt);
                samples.Add((core.Position, core.Velocity));
                tick++;
            }

            elapsed += frameDelta;
        }

        return samples;
    }

    /// <summary>逐位比对两组轨迹，返回差异报告（空 = 完全一致）。</summary>
    public static string CompareTrajectories(
        string labelA, List<(F3 Pos, F3 Vel)> a,
        string labelB, List<(F3 Pos, F3 Vel)> b)
    {
        var sb = new StringBuilder();

        // 有效 tick 数取两者较短的（不同帧率下总 tick 数应相同，除了尾部不足一帧的余数）
        int n = Math.Min(a.Count, b.Count);
        if (n == 0) return $"{labelA} 与 {labelB} 都没有 tick，无法比较\n";

        int mismatches = 0;
        float maxDiff = 0f;
        int firstBad = -1;

        for (int i = 0; i < n; i++)
        {
            F3 dp = a[i].Pos - b[i].Pos;
            F3 dv = a[i].Vel - b[i].Vel;
            bool same = dp.Equals(F3.Zero) && dv.Equals(F3.Zero);
            if (!same)
            {
                mismatches++;
                if (firstBad < 0) firstBad = i;
                float d = dp.Length();
                if (d > maxDiff) maxDiff = d;
            }
        }

        if (mismatches == 0)
        {
            sb.Append($"  {labelA} vs {labelB}: 一致（{n} ticks，逐位相等），");
            sb.Append($"末位置 {a[n - 1].Pos}\n");
        }
        else
        {
            sb.Append($"  {labelA} vs {labelB}: **不一致** {mismatches}/{n} ticks，");
            sb.Append($"首个差异 @tick {firstBad}，最大位置差 {maxDiff:E3} m\n");
            sb.Append($"    A@{firstBad} pos={a[firstBad].Pos} vel={a[firstBad].Vel}\n");
            sb.Append($"    B@{firstBad} pos={b[firstBad].Pos} vel={b[firstBad].Vel}\n");
        }
        return sb.ToString();
    }

    /// <summary>运行完整确定性套件，返回 (通过, 报告文本)。</summary>
    public static (bool Pass, string Report) Run()
    {
        var sb = new StringBuilder();
        bool pass = true;

        // 场景：加速 → 冲刺 → 跳跃 → 滑铲 → 滑铲跳 → 落地静止
        var script = new Script()
            .Add(0.0f, MovementAction.Forward)
            .Add(0.5f, MovementAction.Sprint)
            .Add(1.2f, MovementAction.Jump)
            .Add(1.6f, MovementAction.Sprint)
            .Add(2.2f, MovementAction.SprintCrouch)
            .Add(2.45f, MovementAction.Jump)
            .Add(2.8f, MovementAction.Sprint)
            .Add(3.2f, MovementAction.Idle);

        float seconds = 4.0f;
        float[] fpsSet = { 30f, 60f, 90f, 144f, 240f, 500f };

        var results = new Dictionary<float, List<(F3 Pos, F3 Vel)>>();
        foreach (float fps in fpsSet)
        {
            var cfg = new MovementConfig();
            cfg.Validate();
            var world = new AnalyticWorld(0f);
            results[fps] = RunAtFrameRate(script, seconds, fps, cfg, world);
        }

        sb.AppendLine("=== 跨帧率确定性（同一输入脚本，不同 fps） ===");
        int refTicks = results[60f].Count;
        sb.AppendLine($"  参考（60fps）tick 数 = {refTicks}");

        foreach (float fps in fpsSet)
        {
            if (MathF.Abs(fps - 60f) < 0.01f) continue;
            string cmp = CompareTrajectories("60fps", results[60f], $"{fps:0}fps", results[fps]);
            sb.Append(cmp);
            if (cmp.Contains("不一致")) pass = false;
        }

        // 兜底：参考轨迹必须真的动了（防止"两边都是空"造成假通过）
        var refTrace = results[60f];
        F3 finalPos = refTrace[refTrace.Count - 1].Pos;
        bool moved = MathF.Abs(finalPos.X) > 0.5f || MathF.Abs(finalPos.Z) > 1.0f;
        sb.AppendLine($"  轨迹有效性：末位置={finalPos}  移动过={(moved ? "是" : "**否**")}");
        if (!moved) pass = false;

        // tick 数守恒（60fps 下 4 秒应约 240 ticks）
        int expected = (int)MathF.Round(seconds * 60f);
        bool tickOk = Math.Abs(refTicks - expected) <= 2;
        sb.AppendLine($"  tick 数守恒：期望≈{expected}  实际={refTicks}  {(tickOk ? "OK" : "**异常**")}");
        if (!tickOk) pass = false;

        return (pass, sb.ToString());
    }
}
