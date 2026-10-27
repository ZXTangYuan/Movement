using System;
using System.Collections.Generic;
using System.Text;
using ApexMovement.Core;

namespace ApexMovement.Tests;

/// <summary>
/// v0.4.3 物理合规自检（对应需求方 m00460）。
///
/// 需求原文：「目前有重力加速度吗？我希望移动系统的逻辑都遵循物理规则……如果这个
/// 移动系统里面还有不遵循物理规则的地方，请列出来然后逐项修改。然后要在达到当前数值
/// 且符合物理规则的情况下为其他内容赋值。举例：想让跳跃高度到 10m，应赋值与重力相反
/// 的速度 x，x²/9.8/2=10 ⇒ x=14 m/s（可以按 Space 给 14 m/s，也可以先给一个合适的
/// 短暂加速时间，如 0.2 秒内匀加速到 14 m/s），之后按物理重力减速下落。」
///
/// 本文件守的是"**位移是速度的积分、速度是加速度的积分**"这条底线：
///   · 空中每个 tick 的 Δv 必须严格等于该 tick 所受合加速度 × dt（冲量式 = −g·dt；
///     加速式 = (a−g)·dt，加速段结束后回落成 −g·dt）；
///   · 峰高必须能用运动学闭式解预测（含离散偏置项），而不是"看起来差不多"；
///   · JumpVelocity 必须是 √(2gh) 的**派生量**，改 JumpHeight 自动跟着变；
///   · 有限加速时间的反解必须与 (−gT+√(g²T²+8gh))/2 逐位一致。
///
/// 完整审计清单（26 行、18 条不物理）见 .re-apex/work/physics/report.md。
/// </summary>
public static class PhysicsTest
{
    // ───────────────────────── 辅助 ─────────────────────────

    private static readonly MovementInput Idle = default;
    private static readonly MovementInput JumpInput = new()
    {
        JumpPressed = true,
        JumpHeld = true,
    };

    private sealed class Trace
    {
        public float Apex;                 // 相对起跳点的峰高（胶囊中心）
        public int JumpTickVyIndex = -1;   // vy 序列里第一个"空中 tick"的下标
        public readonly List<float> Vy = new();
        public readonly List<float> Y = new();
        public ApexMovementCore Core = null!;
        public float StartY;
    }

    /// <summary>从静止地面起跳，记录逐 tick 的竖直速度与中心高度，直到落地。</summary>
    private static Trace RunJump(MovementConfig cfg, int maxTicks = 400)
    {
        var world = new AnalyticWorld(0f);
        var core = new ApexMovementCore(cfg);
        core.BindWorld(world);
        core.Reset(new F3(0f, 1.2f, 0f));

        // 先落到地面上（贴地吸附会把胶囊放到 floor + 半高）
        for (int i = 0; i < 60; i++) core.Step(Idle, cfg.FixedDelta);

        var t = new Trace { Core = core };
        t.StartY = core.Position.Y;

        core.Step(JumpInput, cfg.FixedDelta);          // 起跳 tick
        t.Y.Add(core.Position.Y - t.StartY);
        t.Vy.Add(core.VerticalSpeed);
        t.Apex = core.Position.Y - t.StartY;

        for (int i = 0; i < maxTicks; i++)
        {
            core.Step(Idle, cfg.FixedDelta);
            t.Vy.Add(core.VerticalSpeed);
            t.Y.Add(core.Position.Y - t.StartY);
            t.Apex = MathF.Max(t.Apex, core.Position.Y - t.StartY);
            if (core.OnGround && i > 3) break;
        }
        return t;
    }

    // ───────────────────────── 主体 ─────────────────────────

    public static (bool Pass, string Report) Run()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== v0.4.3 物理合规自检（重力 / 弹道 / 加速起跳 / 派生量） ===");

        int failures = 0, checks = 0;
        void Check(string name, bool ok, string detail = "")
        {
            checks++;
            if (!ok) failures++;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}" + (detail.Length > 0 ? "  —— " + detail : ""));
        }

        MovementConfig baseCfg = MovementConfig.Apex();
        float g = baseCfg.Gravity;
        float dt = baseCfg.FixedDelta;

        // ── ① 空中每 tick 的 Δv 严格等于 −g·dt ────────────────────────
        {
            Trace t = RunJump(baseCfg);
            float gdt = g * dt;
            float worst = 0f;
            int airTicks = 0;
            // 下标 0 是起跳 tick（速度从 0 突变到 v0，不计入重力）；
            // 最后一拍是落地 tick（被地面接触清零），也不计入。
            for (int i = 1; i < t.Vy.Count - 1; i++)
            {
                if (t.Vy[i] <= 0f) continue;          // 只检查上升段
                worst = MathF.Max(worst, MathF.Abs((t.Vy[i - 1] - t.Vy[i]) - gdt));
                airTicks++;
            }
            Check("① 空中上升段每 tick 的 Δv 恒等于 g·dt（真重力积分，不是「先升到某高度再下落」）",
                worst < 1e-4f && airTicks >= 15,
                $"检查 {airTicks} 个 tick，最大偏差 {worst:E2} m/s（g·dt = {gdt:F6}）");
        }

        // ── ② 峰高 = 运动学闭式解（含离散偏置项）────────────────────
        {
            Trace t = RunJump(baseCfg);
            float v0 = baseCfg.JumpVelocity;
            float theory = v0 * v0 / (2f * g) + v0 * dt * 0.5f + g * dt * dt / 8f;
            Check("② 实测峰高 = v0²/(2g) + v0·dt/2 + g·dt²/8（离散半隐式欧拉的精确关系）",
                MathF.Abs(t.Apex - theory) < 1e-3f,
                $"实测 {t.Apex:F6} m，公式 {theory:F6} m，差 {t.Apex - theory:E2} m");
            Check("②b 该峰高就是 config.EffectiveJumpHeight（「标称 vs 实际」口径已显式化）",
                MathF.Abs(t.Apex - baseCfg.EffectiveJumpHeight) < 1e-3f,
                $"实测 {t.Apex:F6}，EffectiveJumpHeight {baseCfg.EffectiveJumpHeight:F6}，" +
                $"标称 JumpHeight {baseCfg.JumpHeight:F4}（离散偏置 +{(baseCfg.EffectiveJumpHeight / baseCfg.JumpHeight - 1f) * 100f:F2}%）");
        }

        // ── ③ 峰高随 dt 收敛：30 / 60 / 120 Hz 都应该收敛到标称 h ────────
        {
            var rows = new List<string>();
            float prevErr = float.MaxValue;
            bool monotone = true;
            foreach (int rate in new[] { 30, 60, 120 })
            {
                MovementConfig c = MovementConfig.Apex();
                c.TickRate = rate;
                c.Validate();
                Trace t = RunJump(c);
                float err = MathF.Abs(t.Apex - c.JumpHeight);
                if (err > prevErr) monotone = false;
                prevErr = err;
                rows.Add($"{rate}Hz→{t.Apex:F4}m(误差 {(t.Apex / c.JumpHeight - 1f) * 100f:+0.00;-0.00}%)");
            }
            Check("③ 离散积分指纹：TickRate↑ 时实测峰高单调收敛到标称 JumpHeight",
                monotone,
                string.Join("，", rows));
        }

        // ── ④ JumpVelocity 是 √(2gh) 的派生量（改 h 自动跟着变）────────
        {
            MovementConfig c = MovementConfig.Apex();
            float v0 = c.JumpVelocity;
            c.JumpHeight = 10f;
            float v10 = c.JumpVelocity;
            // ★ v0.5（需求 m01619）：起跳速度是**固定力度**，只由"地球设计高度 + 地球重力"决定，
            //   **不随当前星球重力变**。扣掉半隐式欧拉的 O(dt) 偏置后地球实测峰高精确等于设计高度。
            float gEarth = PlanetProfile.Earth.SurfaceGravity;
            float bias = gEarth * c.FixedDelta * 0.5f;
            float expect0 = MathF.Sqrt(2f * gEarth * baseCfg.JumpHeight) - bias;
            float expect10 = MathF.Sqrt(2f * gEarth * 10f) - bias;
            Check("④ JumpVelocity = √(2·g_earth·h) − g_earth·dt/2（固定力度，与当前星球无关）",
                MathF.Abs(v0 - expect0) < 1e-4f && MathF.Abs(v10 - expect10) < 1e-4f,
                $"h={baseCfg.JumpHeight:F4}→v={v0:F4}；h=10→v={v10:F4}" +
                $"（基准 g_earth={gEarth:F5} − 偏置 {bias:F5}）");
        }

        // ── ⑤ 有限加速时间：物理反解 ──────────────────────────────
        {
            bool formulaOk = true;
            var rows = new List<string>();
            foreach (int ticks in new[] { 6, 12, 18 })   // 0.10 / 0.20 / 0.30 s @60Hz
            {
                MovementConfig c = MovementConfig.Apex();
                c.JumpAccelTime = ticks * c.FixedDelta;
                c.Validate();

                float T = c.JumpAccelTimeActual;
                // v0.5：有限加速只把**同一份冲量**分摊到 n 个 tick，出口速度恒等于固定起跳速度。
                bool ok = c.JumpAccelTicks == ticks &&
                          MathF.Abs(c.JumpRampExitVelocity - c.JumpVelocity) < 1e-6f;
                formulaOk &= ok;
                rows.Add($"t_a={T:F3}s→u={c.JumpRampExitVelocity:F4}（dTicks={c.JumpAccelTicks}）");
            }
            Check("⑤ 加速段出口速度恒为固定起跳速度（与 t_a 无关 ⇒ 力度不因加速时间改变）",
                formulaOk, string.Join("，", rows));
        }

        // ── ⑥ 有限加速：实际弹道形态（加速段内加速度 ≠ −g，之后回到 −g）──
        {
            MovementConfig c = MovementConfig.Apex();
            int ticks = 12;                       // 0.2 s，需求方举例的量级
            c.JumpAccelTime = ticks * c.FixedDelta;
            c.Validate();

            Trace t = RunJump(c, maxTicks: 600);
            float a = (c.JumpRampExitVelocity + (ticks - 1) * g * dt) / (ticks * dt);

            // 加速段（含起跳 tick）内每 tick 的 Δv 应当是 a·dt − g·dt = (a−g)·dt
            // 注意符号：加速段里速度是**递增**的，所以 Δv = v[i] − v[i−1] 为正。
            float worstRamp = 0f;
            for (int i = 1; i < ticks; i++)
                worstRamp = MathF.Max(worstRamp, MathF.Abs((t.Vy[i] - t.Vy[i - 1]) - (a - g) * dt));

            // 加速段结束后应回到纯重力
            float worstAfter = 0f;
            for (int i = ticks + 1; i < t.Vy.Count - 1; i++)
            {
                if (t.Vy[i] <= 0f) continue;
                worstAfter = MathF.Max(worstAfter, MathF.Abs((t.Vy[i - 1] - t.Vy[i]) - g * dt));
            }

            // v0.5：加速模式下人在**蹬伸过程中就已经上升了一段**，所以总峰高
            // = 蹬伸位移 + 固定起跳速度对应的弹道高度 > 纯冲量的 0.75 m。
            // 这是物理正确的（腿伸得越长、离地前已经升得越高），断言对齐解析值。
            float hErr = MathF.Abs(t.Apex - c.EffectiveJumpHeight) / c.EffectiveJumpHeight;
            Check("⑥ 有限加速起跳：加速段内 dv/dt = (a−g)，之后严格回到 −g（能量结构正确）",
                worstRamp < 1e-3f && worstAfter < 1e-3f,
                $"加速段 {ticks} tick 内最大偏差 {worstRamp:E2}，之后最大偏差 {worstAfter:E2} m/s");
            Check("⑥b 有限加速起跳峰高 = 蹬伸位移 + 固定起跳速度的弹道高度（解析值一致）",
                hErr < 0.02f,
                $"实测峰高 {t.Apex:F4} m vs 解析 {c.EffectiveJumpHeight:F4} m（偏差 {hErr * 100f:F2}%）" +
                $"；纯冲量档 {c.JumpApex:F4} m");
        }

        // ── ⑦ 默认配置下"加速分支"完全不参与（逐位等价）─────────────
        {
            Check("⑦ 默认 JumpAccelTime=0 ⇒ 不进入加速分支，起跳速度逐位等于 JumpVelocity",
                baseCfg.JumpAccelTicks == 0 &&
                baseCfg.EffectiveJumpVelocity == baseCfg.JumpVelocity &&
                baseCfg.JumpRampThrust == 0f,
                $"JumpAccelTicks={baseCfg.JumpAccelTicks}，EffectiveJumpVelocity 与 JumpVelocity 逐位相等");
        }

        // ── ⑧ 由物理量反推的派生量 ────────────────────────────────
        {
            float crit = MathF.Atan(baseCfg.SlideFrictionCoefficient) * 180f / MathF.PI;
            Check("⑧ 滑铲临界角 = arctan(μ)（μ=0.45 ⇒ 24.23°；超过它重力压不住摩擦，一路加速）",
                MathF.Abs(baseCfg.SlideCriticalAngleDeg - crit) < 1e-3f &&
                MathF.Abs(crit - 24.23f) < 0.02f,
                $"{baseCfg.SlideCriticalAngleDeg:F4}°");

            float k = g / (baseCfg.MaxFallSpeed * baseCfg.MaxFallSpeed);
            Check("⑧b 终端速度硬夹 ⇔ 二次阻力系数 k = g/v_t²（换算关系，当前实现仍是硬夹）",
                MathF.Abs(baseCfg.TerminalDragK - k) < 1e-9f,
                $"k = {baseCfg.TerminalDragK:E4} 1/m（v_t = {baseCfg.MaxFallSpeed:F2} m/s）");

            Check("⑧c 地面加速时间常数 = 1/GroundAccel；StepTilt 等效坡角 arctan(OverwalkableHeight/半径)",
                MathF.Abs(baseCfg.AccelTimeToTopSpeed - 1f / baseCfg.GroundAccel) < 1e-6f &&
                MathF.Abs(MathF.Atan(baseCfg.OverwalkableHeight / baseCfg.CapsuleRadius) * 180f / MathF.PI - 31.62f) < 0.2f,
                $"T = {baseCfg.AccelTimeToTopSpeed:F3}s；台阶等效坡角 " +
                $"{MathF.Atan(baseCfg.OverwalkableHeight / baseCfg.CapsuleRadius) * 180f / MathF.PI:F2}°");
        }

        // ── ⑨ 滞空时间与 v/g 一致（顶点在 v/g 附近）──────────────────
        {
            Trace t = RunJump(baseCfg);
            int apexIdx = 0;
            for (int i = 1; i < t.Y.Count; i++) if (t.Y[i] > t.Y[apexIdx]) apexIdx = i;
            float tApex = apexIdx * dt;
            float theory = baseCfg.JumpVelocity / g;
            Check("⑨ 到达顶点的时间 ≈ v0/g（闭式解），且顶点处竖直速度换号",
                MathF.Abs(tApex - theory) < 2.5f * dt &&
                t.Vy[apexIdx - 1] > 0f && t.Vy[Math.Min(apexIdx + 1, t.Vy.Count - 1)] < 0.01f,
                $"实测 {tApex:F4}s（v0/g = {theory:F4}s，差 {MathF.Abs(tApex - theory) / dt:F2} tick）");
        }

        // ── ⑩ 上台阶不再"前向瞬移"（物理审计 P0-2）────────────────────
        //
        //     改前：每个台阶 tick 无条件推 max(hLen, 0.75×半径) = 0.3048 m，
        //           冲刺一 tick 本该 0.1101 m ⇒ 多推 3.69 倍；
        //     改后：从 hLen 起按 1/4 半径递增试，取第一个能站住的推进量。
        //     断言：单个 tick 的水平位移 ≤ 0.20 m（≈ 0.5×半径），且确实爬上了台阶。
        {
            var world = new AnalyticWorld(0f);
            world.AddBox(new F3(-6f, 0f, -6f), new F3(6f, 0.2f, -2f));   // 0.2 m 高的台面
            var core = new ApexMovementCore(baseCfg);
            core.BindWorld(world);
            core.Reset(new F3(0f, 1.2f, 3f));
            for (int i = 0; i < 40; i++) core.Step(Idle, dt);            // 落地

            var walk = new MovementInput { MoveZ = 1f, SprintHeld = true };
            F3 prev = core.Position;
            float maxStepDelta = 0f;
            float maxFeet = core.FeetPosition.Y;
            for (int i = 0; i < 120; i++)
            {
                core.Step(walk, dt);
                F3 d = core.Position - prev;
                d = d - F3.Up * F3.Dot(d, F3.Up);                        // 只看水平位移
                maxStepDelta = MathF.Max(maxStepDelta, d.Length());
                maxFeet = MathF.Max(maxFeet, core.FeetPosition.Y);
                prev = core.Position;
            }
            float nominal = baseCfg.SprintSpeed * dt;
            Check("⑩ 上台阶单 tick 水平位移不再瞬移（改前 3.69×本 tick 位移，改后 ≤ 0.5 半径）",
                maxStepDelta <= 0.20f && maxStepDelta <= MathF.Max(nominal * 2f, 0.12f) + 1e-3f && maxFeet > 0.15f,
                $"单 tick 最大水平位移 {maxStepDelta:F4} m（本 tick 名义 {nominal:F4} m，" +
                $"改前下限 0.75×半径 = {baseCfg.CapsuleRadius * 0.75f:F4} m）；脚底最高 {maxFeet:F3} m");
        }
        // ── ⑪ 零距离射线：脚底恰好贴地时向下探地必须命中（物理审计 P1-5）──
        //
        //     改前要求 t > 1e-6，于是 t=0（起点就在地面上）返回"无命中"。
        //     后果：TryAttachWallGrab 的"脚下有地就别抓"守卫失效，站着会被墙吸走。
        {
            var world = new AnalyticWorld(0f);
            var origin = new F3(0f, 0f, 0f);
            bool hit0 = world.Raycast(origin, -F3.Up, 0.5f, out F3 hp0, out F3 hn0);
            bool hitSmall = world.Raycast(new F3(0f, 0.05f, 0f), -F3.Up, 0.5f, out _, out _);
            Check("⑪ 射线起点恰在表面上（t=0）也算命中，而不是\"脚下没地\"",
                hit0 && hitSmall && MathF.Abs(hp0.Y) < 1e-5f && F3.Dot(hn0, F3.Up) > 0.99f,
                $"t=0 命中={hit0}（法线·up={F3.Dot(hn0, F3.Up):F3}），0.05m 高度命中={hitSmall}");
        }
        // ── ⑫ 星球档案：换星球只改常规系数，设计目标（跳跃高度）不变 ──────
        //
        //     需求 m00818：「常规系数定为真实地球量…… 设成快捷可调，因为我要设计
        //     不同的星球」「以保留效果的情况下反推修改特殊量」。
        //     判据：每颗星球上 v = √(2gh) 必须自动成立，且实测峰高仍≈ h。
        {
            var rows = new List<string>();
            bool allOk = true;
            foreach (PlanetProfile planet in new[]
                     { PlanetProfile.Earth, PlanetProfile.Moon, PlanetProfile.Mars, PlanetProfile.ApexDefault })
            {
                MovementConfig c = MovementConfig.Apex();
                c.ApplyPlanet(planet);
                float v = c.JumpVelocity;
                // ★ v0.5（需求 m01619）：**起跳速度是固定力度，不随星球变**；
                //   换星球变的是峰高（apex = v²/2g 的离散版）。
                //   所以这里断言两件事：① 四颗星球起跳速度逐位相同；
                //   ② 实测峰高 == JumpApexOnPlanet(该星球 g)。
                float vEarth = MovementConfig.Apex().JumpVelocity;
                Trace t = RunJump(c, maxTicks: 8000);
                float airTime = t.Vy.Count * dt;
                bool ok = MathF.Abs(v - vEarth) < 1e-6f &&
                          MathF.Abs(t.Apex - c.JumpApex) < 2e-3f;
                allOk &= ok;
                rows.Add($"{planet.Name}: g={planet.SurfaceGravity:F2} v={v:F4} 峰高{t.Apex:F3}" +
                         $"(解析{c.JumpApex:F3}) 滞空{airTime:F2}s");
            }
            // 地球 0.75 是设计目标；重力越小跳得越高（月球约 4.4 m），重力越大跳得越低。
            Check("⑫ 换星球：起跳速度固定、峰高随该星球重力变（地球 0.75 / 月球≈4.4）",
                allOk, string.Join("；", rows));
        }

        // ── ⑬ 重力方向可改（球形行星接口）：移动 ⊥ 重力、跳跃 ∥ −重力 ──────
        //
        //     需求 m00818：「重力方向需要是可改变的，因为星球是球形…… 移动是与重力
        //     方向为 90° 夹角方向，跳跃是与重力相反方向」。
        {
            // 说明：AnalyticWorld 的胶囊是固定 Y 轴形状，表达不了「参考系整体旋转」，
            // 所以这里只验证**方向关系**：跳跃位移沿 −重力（+up），水平移动位移 ⊥ up。
            // 真实球形行星还需要世界层在 Sweep/Raycast 里带上朝向（已列为 TODO）。
            var world = new AnalyticWorld(0f);
            // 大块实体，其 +X 面即「地面」（法线 +X）⇒ 重力方向 −X。
            world.AddBox(new F3(-6f, -6f, -6f), new F3(0f, 6f, 6f));
            MovementConfig c = MovementConfig.Apex();
            var core = new ApexMovementCore(c);
            core.BindWorld(world);
            F3 up = new F3(1f, 0f, 0f);
            core.Reset(new F3(1.5f, 3f, 0f), 0f, 0f, up: up);
            for (int i = 0; i < 120; i++) core.Step(Idle, dt);

            bool grounded = core.OnGround;
            F3 rest = core.Position;

            // 跳跃：位移必须沿 −重力方向（+up）
            core.Step(JumpInput, dt);
            for (int i = 0; i < 8; i++) core.Step(Idle, dt);
            F3 jumpD = core.Position - rest;
            float jumpAlong = F3.Dot(jumpD, up);
            F3 jumpAcross = jumpD - up * jumpAlong;

            // 落回地面后再走：位移必须整体落在垂直于 up 的平面里
            for (int i = 0; i < 240; i++) core.Step(Idle, dt);
            F3 before = core.Position;
            var walk = new MovementInput { MoveZ = 1f };
            for (int i = 0; i < 40; i++) core.Step(walk, dt);
            F3 walkD = core.Position - before;
            float walkAlong = F3.Dot(walkD, up);
            F3 walkAcross = walkD - up * walkAlong;


            Check("⑬ 重力方向可改：跳跃位移 ∥ −重力，水平移动位移 ⊥ 重力",
                grounded && jumpAlong > 0.2f && jumpAcross.Length() < 0.05f &&
                MathF.Abs(walkAlong) < 0.05f && walkAcross.Length() > 0.4f,
                $"贴地={grounded}；跳跃沿up={jumpAlong:F3}（横向 {jumpAcross.Length():F3}）；" +
                $"行走位移沿up={walkAlong:F4}、横向 {walkAcross.Length():F3}");
        }
        // ── ⑭ 空中按蹲收腿：脚底能比站立跳更高（需求 m00952）────────────
        //
        //     需求：「空中蹲可以收腿跳得更高」，即跳跃过程中按住蹲，碰撞胶囊向中心
        //     收拢到蹲伏大小 —— 中心轨迹不变，**脚底**相对中心上抬 (H−C)/2。
        //     这条决定了 0.75 m 的跳跃能上多高的台子。
        {
            float MaxFeet(bool crouchInAir)
            {
                var world = new AnalyticWorld(0f);
                var c = MovementConfig.Apex();
                var core = new ApexMovementCore(c);
                core.BindWorld(world);
                core.Reset(new F3(0f, 1.2f, 0f));
                for (int i = 0; i < 60; i++) core.Step(Idle, dt);
                float feet0 = core.FeetPosition.Y;
                core.Step(JumpInput, dt);
                var hold = new MovementInput { JumpHeld = true, CrouchHeld = crouchInAir };
                float maxFeet = core.FeetPosition.Y;
                for (int i = 0; i < 200; i++)
                {
                    core.Step(crouchInAir ? hold : Idle, dt);
                    maxFeet = MathF.Max(maxFeet, core.FeetPosition.Y);
                    if (core.OnGround && i > 3) break;
                }
                return maxFeet - feet0;
            }
            float stand = MaxFeet(false);
            float crouched = MaxFeet(true);
            float gainTheory = (baseCfg.StandHeight - baseCfg.CrouchHeight) * 0.5f;
            Check("⑭ 空中按蹲收腿：脚底抬升 = (站立高−蹲高)/2，因此跳得更高",
                crouched > stand + gainTheory * 0.8f &&
                MathF.Abs((crouched - stand) - gainTheory) < 0.05f,
                $"站立跳脚底峰值 {stand:F3} m，按住蹲 {crouched:F3} m，" +
                $"增益 {crouched - stand:F3}（理论 (H−C)/2 = {gainTheory:F3}）");
        }
        // ── ⑮ 落地不再「被瞬间吸到地上」（需求 m01070 的实机反馈）──────────
        //
        //     现象：跳到落地时，即将落地的瞬间像被吸到地上、突然大幅加速。
        //     根因：贴地吸附一次能吃掉 0.20 m，落地前最后那 0.2 m 被一帧走完，
        //           于是末段实际下落速度恒为 0.2/dt = 12 m/s，与真实落速无关。
        //     判据：任意一 tick 的向下位移都不得超过「这一 tick 物理上会走的距离」。
        {
            var world = new AnalyticWorld(0f);
            var c = MovementConfig.Apex();
            var core = new ApexMovementCore(c);
            core.BindWorld(world);
            core.Reset(new F3(0f, 6.0f, 0f));

            float prevY = core.Position.Y;
            float worstExcess = 0f;
            int fallTicks = 0;
            for (int i = 0; i < 400; i++)
            {
                float vyBefore = core.VerticalSpeed;
                core.Step(Idle, dt);
                float dy = prevY - core.Position.Y;
                // 物理上这一 tick 的向下位移上限：|vy|·dt + g·dt²（本 tick 新增的重力）
                float allowed = MathF.Max(0f, -vyBefore) * dt + c.Gravity * dt * dt + 0.003f;
                worstExcess = MathF.Max(worstExcess, dy - allowed);
                prevY = core.Position.Y;
                fallTicks++;
                if (core.OnGround && i > 2) break;
            }
            Check("⑮ 落地不瞬移：每 tick 下落位移 ≤ 物理落距（不再一帧吃掉 0.2m 吸附距离）",
                worstExcess < 0.003f,
                $"最差超量 {worstExcess * 1000f:F3} mm（判据 <3mm）；下落 {fallTicks} tick 后落地" +
                $"；旧实现末段固定 ≈{c.GroundSnapLength / dt:F1} m/s");
        }
        // ── ⑯ 空中收腿：头顶与脚底**同时向中间收**（中心不动）────────────
        //
        //     需求 m01202：「在空中收腿时是头顶和脚底同时向中间收，这样更符合物理规则」。
        //     判据：空中按住蹲，**胶囊中心轨迹与不按蹲时逐 tick 相同**（重力才是唯一原因），
        //           而头顶下降 (H−C)/2、脚底上升 (H−C)/2。
        {
            float[] CenterTrace(bool crouchInAir, out float headRel, out float feetRel, out float h)
            {
                var world = new AnalyticWorld(0f);
                var c = MovementConfig.Apex();
                var core = new ApexMovementCore(c);
                core.BindWorld(world);
                core.Reset(new F3(0f, 40f, 0f));
                var trace = new float[40];
                var hold = new MovementInput { CrouchHeld = crouchInAir };
                for (int i = 0; i < 40; i++)
                {
                    core.Step(crouchInAir ? hold : Idle, dt);
                    trace[i] = core.Position.Y;
                }
                h = core.CurrentHeight;
                headRel = h * 0.5f;
                feetRel = -h * 0.5f;
                return trace;
            }
            float[] standTrace = CenterTrace(false, out float headS, out float feetS, out float hS);
            float[] crouchTrace = CenterTrace(true, out float headC, out float feetC, out float hC);
            float maxCenterDiff = 0f;
            for (int i = 0; i < standTrace.Length; i++)
                maxCenterDiff = MathF.Max(maxCenterDiff, MathF.Abs(standTrace[i] - crouchTrace[i]));
            float delta = hS - hC;
            float want = delta * 0.5f;
            Check("⑯ 空中收腿：中心不动，头顶下降 (H−C)/2、脚底上升 (H−C)/2",
                maxCenterDiff < 1e-4f &&
                MathF.Abs(headS - headC - want) < 1e-4f &&
                MathF.Abs(feetC - feetS - want) < 1e-4f,
                $"中心轨迹最大差 {maxCenterDiff:E1} m；头顶↓{headS - headC:F4}、脚底↑{feetC - feetS:F4}" +
                $"（理论 (H−C)/2 = {want:F4}，H={hS:F2}→C={hC:F2}）");
        }
        // ── ⑰ 空中起身不把脚底伸进地面（用户 m01300 BUG 1）──────────────
        //
        //     症状：「连续滑铲跳后松开按键，角色一直蹲着不起身，只有跳一下才站起来」。
        //     根因：空中收腿是"中心不动"，所以**起身时脚底向下伸 delta/2**。
        //           如果脚底离地不足 delta/2，胶囊就伸进地里 4 cm；而贴地吸附只会向下、
        //           不会把人顶出来 ⇒ 永久陷在地面以下，接着"站立头顶检测"把地面当成障碍
        //           ⇒ 蹲伏 ctrl 被永远钉成 1 ⇒ 松开按键也站不起来。跳一次把胶囊抬起来
        //           （重叠消失）才恢复。
        //     判据：蹲跳 → 空中松蹲 → 整段过程脚底不得低于地面 5 mm；最终必须站起来。
        {
            var world = new AnalyticWorld(0f);
            var c = MovementConfig.Apex();
            var core = new ApexMovementCore(c);
            core.BindWorld(world);
            core.Reset(new F3(0f, c.StandHeight * 0.5f, 0f));
            for (int i = 0; i < 20; i++) core.Step(Idle, dt);
            for (int i = 0; i < 20; i++) core.Step(new MovementInput { CrouchHeld = true }, dt);

            core.Step(new MovementInput { CrouchHeld = true, JumpPressed = true, JumpHeld = true }, dt);
            float minFeet = float.MaxValue;
            for (int i = 0; i < 60; i++)
            {
                core.Step(Idle, dt);
                minFeet = MathF.Min(minFeet, core.FeetPosition.Y);
            }
            Check("⑰ 空中起身时脚底不伸进地面（连续滑铲跳后仍能站起来的根因）",
                minFeet > -0.005f,
                $"全程脚底最低 {minFeet * 1000f:F2} mm（判据 > -5 mm）");
            Check("⑰b 落地后能正常站起（蹲伏量归零、站立不被地面误判挡住）",
                core.CrouchAmount < 0.01f && !core.HeadBlockedForStanding,
                $"蹲伏量 {core.CrouchAmount:F3}，站立被挡={core.HeadBlockedForStanding}，" +
                $"身高 {core.CurrentHeight:F3} m");
        }
        // ── ⑱ 地面摩擦：真库仑（v0.5 / 需求 m01070 第 3 点）──────────────
        //
        //     需求原文：「地面摩擦改真库仑 μ≈0.7（停距 3.18m，且随坡度/星球变）」
        //
        //     模型：a_brake = μ·g·cosθ（平地 cosθ=1）。与旧的 Source 粘性模型
        //     （drop = speed·friction·dt，减速度 ∝ 速度）相比：库仑是**匀减速**，
        //     停距 ∝ v²，这才是真实刹车距离。
        {
            // ⑱a 平地：逐 tick Δv = μ·g·dt，停距 = v²/(2μg)
            {
                var world = new AnalyticWorld(0f);
                var c = MovementConfig.Apex();
                var core = new ApexMovementCore(c);
                core.BindWorld(world);
                core.Reset(new F3(0f, c.StandHeight * 0.5f, 0f));
                for (int i = 0; i < 40; i++) core.Step(Idle, dt);

                core.SetVelocity(new F3(0f, 0f, -6.604f));
                float v0 = core.HorizontalSpeed;
                float z0 = core.Position.Z;
                float expectedDrop = c.GroundFrictionCoefficient * c.Gravity * dt;
                float worst = 0f;
                float prev = v0;
                int guard = 0;
                while (core.HorizontalSpeed > 1e-3f && guard++ < 600)
                {
                    core.Step(Idle, dt);
                    float d = prev - core.HorizontalSpeed;
                    if (core.HorizontalSpeed > 1e-3f) worst = MathF.Max(worst, MathF.Abs(d - expectedDrop));
                    prev = core.HorizontalSpeed;
                }
                float dist = MathF.Abs(core.Position.Z - z0);
                float theoryDist = v0 * v0 / (2f * c.GroundFrictionCoefficient * c.Gravity);
                Check("⑱a 平地匀减速：每 tick Δv = μ·g·dt（库仑，不是粘性）",
                    worst < 2e-3f,
                    $"实测最差偏差 {worst:E2} m/s（理论 {expectedDrop:F4}，μ={c.GroundFrictionCoefficient:F2}）");
                Check("⑱a 平地停距 = v²/(2μg)（冲刺 6.604 ⇒ 约 3.18 m）",
                    MathF.Abs(dist - theoryDist) / theoryDist < 0.08f,
                    $"实测 {dist:F3} m，理论 {theoryDist:F3} m（偏差 " +
                    $"{(dist - theoryDist) / theoryDist * 100f:F1}%，离散求和略短）");
            }

            // ⑱b 主动急停：输入与速度反向 ⇒ 摩擦乘 ActiveBrakeMultiplier
            //
            //     注意这里的物理分工：按 S 时**加速度本身**就在反向推（Apex 的
            //     Accelerate 会朝 wishDir 加速），所以"按 S 刹得比松手快"这件事
            //     主要来自加速度，摩擦加成只是叠加项。要**单独量出摩擦加成**，
            //     唯一干净的办法是固定同一个输入、只改 ActiveBrakeMultiplier 做差。
            {
                (float dist, float firstDrop) Brake(float multiplier)
                {
                    var c = MovementConfig.Apex();
                    c.ActiveBrakeMultiplier = multiplier;
                    var world = new AnalyticWorld(0f);
                    var core = new ApexMovementCore(c);
                    core.BindWorld(world);
                    core.Reset(new F3(0f, c.StandHeight * 0.5f, 0f));
                    for (int i = 0; i < 40; i++) core.Step(Idle, dt);

                    // 朝 -Z 跑（yaw=0 的前向），然后按 S（MoveZ=-1 ⇒ 朝 +Z）刹停
                    core.SetVelocity(new F3(0f, 0f, -6.604f));
                    float z0 = core.Position.Z;
                    var brake = new MovementInput { MoveZ = -1f };
                    float before = core.HorizontalSpeed;
                    core.Step(brake, dt);
                    float first = before - core.HorizontalSpeed;
                    int guard = 0;
                    while (F3.Dot(core.Velocity, new F3(0f, 0f, -1f)) > 0f && guard++ < 600)
                        core.Step(brake, dt);
                    return (MathF.Abs(core.Position.Z - z0), first);
                }

                // v0.5.2 摩擦重做后，主动急停不再是"μ×2"（滑动摩擦加倍）——
                // 现在的模型里，按反向键是**静摩擦朝反方向做正功**（推动身体减速），
                // 其上限是 μs·g（最大静摩擦），不是 μk·g·倍率。
                // 所以下面换成两条**行为级**断言（比扣公式更抗模型变化）：
                //   ① 倍率 2.0 的停距必须短于倍率 1.0（倍率确实起作用）；
                //   ② 主动急停停距必须短于"松手滑停"（需求：人能主动杀停）。
                var one = Brake(1.0f);
                var two = Brake(2.0f);

                Check("⑱b 主动急停倍率生效：2.0 的停距短于 1.0",
                    two.dist < one.dist - 1e-3f,
                    $"倍率 2.0 停距 {two.dist:F3} m < 倍率 1.0 停距 {one.dist:F3} m");

                // 松手滑停（无输入）作对照
                float coast = 0f;
                {
                    var c = MovementConfig.Apex();
                    var world = new AnalyticWorld(0f);
                    var core = new ApexMovementCore(c);
                    core.BindWorld(world);
                    core.Reset(new F3(0f, c.StandHeight * 0.5f, 0f));
                    for (int i = 0; i < 40; i++) core.Step(Idle, dt);
                    core.SetVelocity(new F3(0f, 0f, -6.604f));
                    float z0 = core.Position.Z;
                    int guard = 0;
                    while (core.HorizontalSpeed > 1e-3f && guard++ < 2000) core.Step(Idle, dt);
                    coast = MathF.Abs(core.Position.Z - z0);
                }
                Check("⑱b 主动急停比松手滑停更短（松手 ≈ v²/(2μk·g)）",
                    two.dist < coast - 0.05f,
                    $"主动急停 {two.dist:F3} m < 松手滑停 {coast:F3} m");
            }

            // ⑱c 星球切换：摩擦力随 g 自动变（同一 μ，月球 g=1.62）
            {
                var c = MovementConfig.Apex();
                c.ApplyPlanet(PlanetProfile.Moon);
                var world = new AnalyticWorld(0f);
                var core = new ApexMovementCore(c);
                core.BindWorld(world);
                core.Reset(new F3(0f, c.StandHeight * 0.5f, 0f));
                for (int i = 0; i < 40; i++) core.Step(Idle, dt);
                core.SetVelocity(new F3(0f, 0f, -6.604f));
                float prev = core.HorizontalSpeed, worst = 0f;
                float expectedDrop = c.GroundFrictionCoefficient * c.Gravity * dt;
                int guard = 0;
                while (core.HorizontalSpeed > 1e-3f && guard++ < 2000)
                {
                    core.Step(Idle, dt);
                    if (core.HorizontalSpeed > 1e-3f)
                        worst = MathF.Max(worst, MathF.Abs((prev - core.HorizontalSpeed) - expectedDrop));
                    prev = core.HorizontalSpeed;
                }
                Check("⑱c 换星球后摩擦随重力变（月球 μ·g 更小 ⇒ 滑得更远）",
                    worst < 2e-3f && guard > 300,
                    $"月球每 tick Δv {expectedDrop:F4} m/s，刹停用了 {guard} tick（地球约 " +
                    $"{MathF.Round(6.604f / (0.7f * 9.80665f * dt))} tick）");
            }

            // ⑱d 地面材质覆盖：IFrictionWorld 给 0.05（冰面）⇒ 摩擦变小
            {
                var world = new AnalyticWorld(0f) { SurfaceFriction = 0.05f };
                var c = MovementConfig.Apex();
                var core = new ApexMovementCore(c);
                core.BindWorld(world);
                core.Reset(new F3(0f, c.StandHeight * 0.5f, 0f));
                for (int i = 0; i < 40; i++) core.Step(Idle, dt);
                core.SetVelocity(new F3(0f, 0f, -6.604f));
                float prev = core.HorizontalSpeed, worst = 0f;
                float expectedDrop = 0.05f * c.Gravity * dt;
                int guard = 0;
                while (core.HorizontalSpeed > 1e-3f && guard++ < 4000)
                {
                    core.Step(Idle, dt);
                    if (core.HorizontalSpeed > 1e-3f)
                        worst = MathF.Max(worst, MathF.Abs((prev - core.HorizontalSpeed) - expectedDrop));
                    prev = core.HorizontalSpeed;
                }
                Check("⑱d 冰面（材质 μ=0.05）覆盖配置默认值 0.7",
                    worst < 2e-3f,
                    $"冰面每 tick Δv {expectedDrop:F4} m/s（配置默认会是 " +
                    $"{c.GroundFrictionCoefficient * c.Gravity * dt:F4}）");
            }
        }
        // ── ⑲ 加速度分解（三维调试渲染器的数据源）──────────────────
        //
        //     需求 m01621：「除了速度还有作用力产生的加速度，也实时渲染三维箭头」。
        //     渲染器要画"重力 / 摩擦 / 输入 / 冲量 / 碰撞"五根箭头，它们必须**自洽**：
        //     各项之和 == 合计 == (v_end − v_start)/dt。否则玩家看到的箭头是假的。
        {
            static float Mag(F3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

            // (a) 站立静止：重力 ↓g、支撑力 ↑g，合计 ≈ 0
            {
                var world = new AnalyticWorld(0f);
                var core = new ApexMovementCore(baseCfg);
                core.BindWorld(world);
                core.Reset(new F3(0f, baseCfg.StandHeight * 0.5f, 0f));
                for (int i = 0; i < 40; i++) core.Step(Idle, dt);

                ApexMovementCore.AccelBreakdown a = core.LastAccel;
                Check("⑲a 站立静止：重力 ↓g 与支撑力 ↑g 相互抵消，合计 ≈ 0",
                    MathF.Abs(Mag(a.Gravity) - baseCfg.Gravity) < 1e-2f &&
                    MathF.Abs(Mag(a.Collision) - baseCfg.Gravity) < 0.2f &&
                    Mag(a.Net) < 0.05f,
                    $"重力 {Mag(a.Gravity):F3}、碰撞/支撑 {Mag(a.Collision):F3}、" +
                    $"合计 {Mag(a.Net):F4} m/s²");
            }

            // (b) 冲刺中：五项之和恒等于合计，且合计就是真实速度变化率
            {
                var world = new AnalyticWorld(0f);
                var core = new ApexMovementCore(baseCfg);
                core.BindWorld(world);
                core.Reset(new F3(0f, baseCfg.StandHeight * 0.5f, 0f));
                for (int i = 0; i < 40; i++) core.Step(Idle, dt);

                var sprint = new MovementInput { MoveZ = 1f, SprintHeld = true };
                float worst = 0f;
                for (int i = 0; i < 60; i++)
                {
                    F3 before = core.Velocity;
                    core.Step(sprint, dt);
                    ApexMovementCore.AccelBreakdown a = core.LastAccel;
                    F3 sum = a.Gravity + a.Friction + a.Input + a.Impulse + a.Collision;
                    worst = MathF.Max(worst, Mag(sum - a.Net));
                    worst = MathF.Max(worst, Mag((core.Velocity - before) / dt - a.Net));
                }
                Check("⑲b 加速度分解自洽：五项之和 == 合计 == (v_end−v_start)/dt",
                    worst < 1e-2f,
                    $"逐 tick 最大偏差 {worst:E2} m/s²（60 tick 冲刺）");
            }

            // (c) 起跳那一 tick 记在"冲量"项上（不是输入推进）
            {
                var world = new AnalyticWorld(0f);
                var core = new ApexMovementCore(baseCfg);
                core.BindWorld(world);
                core.Reset(new F3(0f, baseCfg.StandHeight * 0.5f, 0f));
                for (int i = 0; i < 40; i++) core.Step(Idle, dt);

                core.Step(JumpInput, dt);
                ApexMovementCore.AccelBreakdown a = core.LastAccel;
                float wantImpulse = baseCfg.EffectiveJumpVelocity / dt;
                Check("⑲c 起跳记在'冲量'项上（瞬时突变，不是输入推进）",
                    Mag(a.Impulse) > wantImpulse * 0.5f &&
                    Mag(a.Input) < wantImpulse * 0.5f,
                    $"冲量 {Mag(a.Impulse):F1} m/s²、输入 {Mag(a.Input):F1} m/s²" +
                    $"（期望 {wantImpulse:F1} = v/dt）");
            }

            // (d) 空中没有摩擦项（AirFriction 默认 0）
            {
                var world = new AnalyticWorld(0f);
                var core = new ApexMovementCore(baseCfg);
                core.BindWorld(world);
                core.Reset(new F3(0f, 8f, 0f));
                core.SetVelocity(new F3(0f, 0f, 0f));
                for (int i = 0; i < 5; i++) core.Step(Idle, dt);
                Check("⑲d 空中没有摩擦力（AirFriction=0 ⇒ 摩擦项恒为 0）",
                    Mag(core.LastAccel.Friction) < 1e-4f,
                    $"摩擦 {Mag(core.LastAccel.Friction):E2} m/s²");
            }
        }


        sb.AppendLine($"合计 {checks} 项断言，失败 {failures} 项");
        return (failures == 0, sb.ToString());
    }
}
