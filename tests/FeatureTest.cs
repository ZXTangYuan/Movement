using System;
using System.Text;
using ApexMovement.Core;

namespace ApexMovement.Tests;

/// <summary>
/// 功能验证：每个具体机制是否真的产生了预期的行为变化。
///
/// 与 DeterminismTest 互补 —— 那个证明"可复现"，这个证明"确实在做事"。
/// 跑在 <see cref="AnalyticWorld"/>（纯数学碰撞替身）上，因此无头、无引擎依赖。
///
/// **期望值全部来自 docs/wiki-reference.md 的换算表**（1 hu = 2.54 cm），
/// 因此这些断言同时充当"wiki 数值 ↔ 实现"的一致性检查。
/// </summary>
public static class FeatureTest
{
    private static ApexMovementCore MakeCore(AnalyticWorld world, MovementConfig? cfg = null)
    {
        var core = new ApexMovementCore(cfg ?? MovementConfig.Apex());
        core.BindWorld(world);
        return core;
    }

    private static MovementInput In(float mz = 0f, float mx = 0f, bool jump = false,
                                    bool crouch = false, bool sprint = false,
                                    bool holster = false) => new()
    {
        MoveX = mx,
        MoveZ = mz,
        JumpPressed = jump,
        JumpHeld = jump,
        CrouchHeld = crouch,
        SprintHeld = sprint,
        Holstered = holster,
    };

    /// <summary>跑 N 个 tick（跳跃只在第一 tick 是上升沿）。</summary>
    private static void Run(ApexMovementCore core, int ticks, MovementInput input)
    {
        float dt = core.Config.FixedDelta;
        for (int i = 0; i < ticks; i++)
        {
            MovementInput i2 = input;
            if (i > 0) { i2.JumpPressed = false; i2.JumpHeld = false; }
            core.Step(i2, dt);
        }
    }

    private static void Settle(ApexMovementCore core, int ticks = 30) => Run(core, ticks, In());

    /// <summary>
    /// 量一次滑铲减速度（m/s²，正值 = 变慢）。
    /// <paramref name="materialMu"/> 为 null 时世界不提供材质（用配置默认 μ）。
    /// </summary>
    private static float MeasureSlideDecel(float? materialMu, out float speed0, out float speed1)
    {
        var world = new AnalyticWorld(0f) { SurfaceFriction = materialMu };
        var core = MakeCore(world);
        core.Reset(new F3(0f, 1f, 0f));
        Settle(core, 10);
        Run(core, 90, In(mz: 1f, sprint: true));       // 冲刺到 6.60

        // 起滑（蹲下 + 有速度），前 3 tick 让 slideboost 结算完再取基准
        var slideIn = In(mz: 1f, sprint: true, crouch: true);
        Run(core, 3, slideIn);
        speed0 = core.HorizontalSpeed;
        Run(core, 24, slideIn);                        // 0.4 s
        speed1 = core.HorizontalSpeed;
        return (speed0 - speed1) / 0.4f;
    }

    public static (bool Pass, string Report) Run()
    {
        var sb = new StringBuilder();
        int failures = 0;
        int checks = 0;

        void Check(string name, bool ok, string detail = "")
        {
            checks++;
            if (!ok) failures++;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}" +
                          (detail.Length > 0 ? $"  （{detail}）" : ""));
        }

        void Near(string name, float got, float want, float tol) =>
            Check(name, MathF.Abs(got - want) <= tol, $"实测 {got:F3}，期望 {want:F3} ± {tol:F3}");

        var cfg0 = MovementConfig.Apex();

        // ═════════ 1. 落地 ═════════
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 3f, 0f));
            Settle(core, 120);
            Check("落地", core.OnGround && core.State == MovementState.Ground,
                  $"state={core.State} onGround={core.OnGround} feetY={core.FeetPosition.Y:F3}");
        }

        // ═════════ 2. 行走速度 = 173.5 hu/s = 4.407 ═════════
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 10);
            Run(core, 120, In(mz: 1f));
            Near("行走速度（173.5 hu/s）", core.HorizontalSpeed, 4.4069f, 0.15f);
        }

        // ═════════ 3. 冲刺速度 = 260 hu/s = 6.604 ═════════
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 10);
            Run(core, 150, In(mz: 1f, sprint: true));
            Near("冲刺速度（260 hu/s）", core.HorizontalSpeed, 6.604f, 0.15f);
        }

        // ═════════ 4. 收枪冲刺 = 299 hu/s = 7.595 ═════════
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 10);
            Run(core, 150, In(mz: 1f, sprint: true, holster: true));
            Near("收枪冲刺速度（299 hu/s）", core.HorizontalSpeed, 7.5946f, 0.15f);
            Check("收枪冲刺标记 Sprinting", core.Snapshot.Sprinting);
        }

        // ═════════ 4b. 收枪**行走**不能算冲刺 ═════════
        // 收枪行走 = 173.5×1.15 = 199 hu/s = 5.0546 m/s，已经超过 WalkSpeed×1.05
        // （4.627）。早期版本用「速度 > WalkSpeed×1.05」反推 Sprinting，导致
        // 收枪散步也被判成冲刺 ⇒ 相机 FOV +3°、冲刺侧倾 1.5° 全部误触发。
        // 修法：Sprinting = 冲刺**意图** ∧ 真跑起来了。
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 10);
            Run(core, 150, In(mz: 1f, holster: true));
            Near("收枪行走速度（199 hu/s）", core.HorizontalSpeed, 5.0546f, 0.15f);
            Check("收枪行走不标记 Sprinting", !core.Snapshot.Sprinting);
        }

        // ═════════ 5. 蹲行速度 = 80 hu/s = 2.032 ═════════
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 10);
            Run(core, 150, In(mz: 1f, crouch: true));
            Near("蹲行速度（80 hu/s）", core.HorizontalSpeed, 2.032f, 0.15f);
        }

        // ═════════ 6. 跳跃高度 = 56 hu = 1.422 ═════════
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 10);
            float groundFeet = core.FeetPosition.Y;
            Run(core, 1, In(jump: true));
            float peak = groundFeet;
            for (int i = 0; i < 60; i++)
            {
                core.Step(In(), core.Config.FixedDelta);
                peak = MathF.Max(peak, core.FeetPosition.Y);
            }
            Near("跳跃高度（56 hu）", peak - groundFeet, cfg0.JumpHeight, 0.10f);
        }

        // ═════════ 7. 滞空时间 = 0.74s（wiki 独立实测） ═════════
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 10);
            Run(core, 1, In(jump: true));
            int airTicks = 0;
            for (int i = 0; i < 200; i++)
            {
                core.Step(In(), core.Config.FixedDelta);
                airTicks++;
                if (core.OnGround) break;
            }
            float airTime = airTicks * core.Config.FixedDelta;
            Near("跳跃滞空时间（wiki 0.74s）", airTime, 0.74f, 0.06f);
        }

        // ═════════ 8. 蹲下身高 = 0.975 ═════════
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 10);
            Run(core, 30, In(crouch: true));
            Near("蹲下身高（站立一半）", core.CurrentHeight, 0.975f, 0.01f);
        }

        // ═════════ 9. 地面蹲下：脚底不动（向底部收缩） ═════════
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 30);
            float feet0 = core.FeetPosition.Y;
            Run(core, 30, In(crouch: true));
            Near("地面蹲下脚底不下沉", core.FeetPosition.Y, feet0, 0.03f);
        }

        // ═════════ 10. 空中蹲下：脚底上升（向中间收缩）═════════
        {
            // 对照组：空中不蹲
            var worldA = new AnalyticWorld(0f);
            var coreA = MakeCore(worldA);
            coreA.Reset(new F3(0f, 1f, 0f));
            Settle(coreA, 10);
            Run(coreA, 1, In(jump: true));
            Run(coreA, 6, In());
            float feetA0 = coreA.FeetPosition.Y;
            Run(coreA, 8, In());
            float feetA1 = coreA.FeetPosition.Y;

            // 实验组：空中蹲
            var worldB = new AnalyticWorld(0f);
            var coreB = MakeCore(worldB);
            coreB.Reset(new F3(0f, 1f, 0f));
            Settle(coreB, 10);
            Run(coreB, 1, In(jump: true));
            Run(coreB, 6, In());
            float feetB0 = coreB.FeetPosition.Y;
            Run(coreB, 8, In(crouch: true));
            float feetB1 = coreB.FeetPosition.Y;

            float riseA = feetA1 - feetA0;   // 只是继续上升（重力仍在）
            float riseB = feetB1 - feetB0;
            Check("空中蹲下向中间收缩（脚底抬升）", riseB - riseA > 0.25f,
                  $"对照组 Δfeet={riseA:F3}，实验组 Δfeet={riseB:F3}，差={riseB - riseA:F3}（期望 ≈ +0.49）");
        }

        // ═════════ 11. 空中没有摩擦（水平速度不衰减）═════════
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 10);
            Run(core, 150, In(mz: 1f, sprint: true));
            Run(core, 1, In(mz: 1f, sprint: true, jump: true));
            Run(core, 5, In());
            float s1 = core.HorizontalSpeed;
            Run(core, 35, In());
            float s2 = core.HorizontalSpeed;
            Check("空中无摩擦（水平速度不衰减）", MathF.Abs(s2 - s1) < 0.01f,
                  $"5 tick 后={s1:F4}，40 tick 后={s2:F4}（差 {s2 - s1:E2}）");
        }

        // ═════════ 12. Air strafe 能加速 ═════════
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 10);
            Run(core, 150, In(mz: 1f, sprint: true));
            Run(core, 1, In(mz: 1f, sprint: true, jump: true));
            Run(core, 5, In());
            float before = core.HorizontalSpeed;

            // 按住右移 + **朝右转**视角（wishDir 一直"贴"在速度矢量侧前方）
            // 转向速率是 air strafe 的关键：太快 wishDir 会转到与速度反向（减速），
            // 太慢则投影很快超过 AirWishSpeed(1.524) 而停止加速。实测最优 ≈ 0.02 rad/tick。
            // 注意：滞空只有 ~44 tick，必须在这之内测量，否则会落到地面被摩擦拖慢。
            float dt = core.Config.FixedDelta;
            int airTicks = 0;
            for (int i = 0; i < 30 && !core.OnGround; i++)
            {
                var si = new MovementInput { MoveX = 1f, MouseDeltaYaw = 0.02f };
                core.Step(si, dt);
                airTicks++;
            }
            float after = core.HorizontalSpeed;
            Check("Air strafe 加速", after > before + 0.8f,
                  $"转弯前={before:F2} m/s，{airTicks} tick 后={after:F2} m/s（+{after - before:F2}）");
        }

        // ═════════ 13. 滑铲触发（地面 200 hu/s 门槛）═════════
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 10);
            Run(core, 150, In(mz: 1f, sprint: true));
            float before = core.HorizontalSpeed;
            Run(core, 3, In(mz: 1f, sprint: true, crouch: true));
            Check("滑铲触发（地面 200 hu/s）", core.State == MovementState.Slide,
                  $"state={core.State}，进入前 {before:F2} m/s（门槛 5.08）");
        }

        // ═════════ 14. Slideboost = +150 hu/s，上限 400 hu/s ═════════
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 10);
            Run(core, 150, In(mz: 1f, sprint: true, holster: true));   // 7.59
            Run(core, 2, In(mz: 1f, sprint: true, crouch: true, holster: true));
            float boosted = core.HorizontalSpeed;
            Check("Slideboost 生效", core.State == MovementState.Slide && boosted > 9.0f,
                  $"冲刺 7.59 → 滑铲 {boosted:F2} m/s（期望 +3.81 后夹到 10.16 上限）");
            // wiki：上限 = 400 hu/s × (holster × walkBoost) = 10.16 × 1.15（收枪时）
            Near("Slideboost 夹到 400 hu/s 上限（含收枪 ×1.15）",
                 boosted, 10.16f * 1.15f, 0.7f);
        }

        // ═════════ 15. Slideboost 有 2s 冷却 ═════════
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 10);
            Run(core, 150, In(mz: 1f, sprint: true));
            Run(core, 2, In(mz: 1f, sprint: true, crouch: true));       // 第一次滑铲（有 boost）
            Run(core, 10, In(mz: 1f, sprint: true));                    // 退出滑铲
            Run(core, 40, In(mz: 1f, sprint: true));                    // 重新加速
            float before2 = core.HorizontalSpeed;
            Run(core, 2, In(mz: 1f, sprint: true, crouch: true));       // 第二次滑铲（冷却中）
            float after2 = core.HorizontalSpeed;
            Check("Slideboost 2s 冷却", core.State == MovementState.Slide && after2 < before2 + 1.0f,
                  $"第二次滑铲：{before2:F2} → {after2:F2} m/s（冷却中不应再 +3.81）");
        }

        // ═════════ 16. Slidejump 时间窗（0.24s 后允许保留速度）═════════
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 10);
            Run(core, 150, In(mz: 1f, sprint: true, holster: true));
            Run(core, 2, In(mz: 1f, sprint: true, crouch: true, holster: true));
            // 滑 0.3s（18 tick）后跳：跨过 0.24s 时间窗
            Run(core, 18, In(mz: 1f, sprint: true, crouch: true, holster: true));
            float slideSpeed = core.HorizontalSpeed;
            Run(core, 1, In(mz: 1f, sprint: true, crouch: true, holster: true, jump: true));
            float jumpSpeed = core.HorizontalSpeed;
            Check("Slidejump 时间窗（≥0.24s 允许保留速度）",
                  core.State == MovementState.Air && jumpSpeed > slideSpeed - 0.6f && core.SlideHopCount >= 1,
                  $"滑铲 {slideSpeed:F2} → 跳后 {jumpSpeed:F2}，slideHops={core.SlideHopCount}");
        }

        // ═════════ 17. Deadslide：速度 >350hu/s 且 <0.24s 时跳，速度掉回冲刺档 ═════════
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 10);
            Run(core, 150, In(mz: 1f, sprint: true, holster: true));
            Run(core, 2, In(mz: 1f, sprint: true, crouch: true, holster: true));   // 10.16 m/s
            float slideSpeed = core.HorizontalSpeed;
            Run(core, 1, In(mz: 1f, sprint: true, crouch: true, holster: true, jump: true));
            float jumpSpeed = core.HorizontalSpeed;
            Check("Deadslide（速度>350hu/s 且 <0.24s → 丢速度）",
                  slideSpeed > cfg0.SlideJumpSpeedThreshold && jumpSpeed < slideSpeed - 2.0f,
                  $"滑铲 {slideSpeed:F2} > 阈值 {cfg0.SlideJumpSpeedThreshold:F2}，跳后掉到 {jumpSpeed:F2}");
        }

        // ═════════ 18. 连续滑铲跳 ≥3 次 ═════════
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 10);
            Run(core, 150, In(mz: 1f, sprint: true, holster: true));

            var speeds = new System.Collections.Generic.List<float>();
            for (int hop = 0; hop < 3; hop++)
            {
                // 16 tick = 0.267s > 0.24s 时间窗 ⇒ 一定满足 slidejump 条件
                Run(core, 16, In(mz: 1f, sprint: true, crouch: true, holster: true));
                speeds.Add(core.HorizontalSpeed);
                Run(core, 1, In(mz: 1f, sprint: true, crouch: true, holster: true, jump: true));
                Run(core, 55, In(mz: 1f, sprint: true, holster: true));                // 空中 → 落地
            }
            Check("连续滑铲跳 ≥3 次", core.SlideHopCount >= 3,
                  $"slideHops={core.SlideHopCount}，各次滑铲速度={string.Join(" / ", speeds.ConvertAll(v => v.ToString("F2")))}");
        }

        // ═════════ 19. 跳跃疲劳：落地 0.15s 内再跳，高度压到 30% ═════════
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 30);

            // 第一次跳（无疲劳）
            Run(core, 1, In(jump: true));
            for (int i = 0; i < 120 && !core.OnGround; i++) core.Step(In(), core.Config.FixedDelta);

            // 落地后立刻再跳（疲劳窗口内）
            float groundFeet = core.FeetPosition.Y;
            Run(core, 1, In(jump: true));
            float peak = groundFeet;
            for (int i = 0; i < 60; i++)
            {
                core.Step(In(), core.Config.FixedDelta);
                peak = MathF.Max(peak, core.FeetPosition.Y);
            }
            float h = peak - groundFeet;
            float expected = cfg0.JumpHeight * cfg0.JumpFatigueHeightFrac;
            Check("跳跃疲劳（连跳高度压到 30%）", h < cfg0.JumpHeight * 0.55f && h > expected * 0.6f,
                  $"连跳高度 {h:F3}m，期望≈{expected:F3}m（满高 {cfg0.JumpHeight:F3}m）");
        }

        // ═════════ 20. 跳跃疲劳在 0.75s 后恢复 ═════════
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 30);
            Run(core, 1, In(jump: true));
            for (int i = 0; i < 120 && !core.OnGround; i++) core.Step(In(), core.Config.FixedDelta);
            Run(core, 50, In());   // 等 0.83s（> 0.75s）

            float groundFeet = core.FeetPosition.Y;
            Run(core, 1, In(jump: true));
            float peak = groundFeet;
            for (int i = 0; i < 60; i++)
            {
                core.Step(In(), core.Config.FixedDelta);
                peak = MathF.Max(peak, core.FeetPosition.Y);
            }
            Near("跳跃疲劳 0.75s 后恢复满高", peak - groundFeet, cfg0.JumpHeight, 0.15f);
        }

        // ═════════ 21. Bunnyhop 减速 ═════════
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 10);
            Run(core, 150, In(mz: 1f, sprint: true, holster: true));    // 7.59
            Run(core, 1, In(mz: 1f, sprint: true, holster: true, jump: true));
            float afterFirst = core.HorizontalSpeed;
            for (int i = 0; i < 120 && !core.OnGround; i++) core.Step(In(mz: 1f, sprint: true, holster: true), core.Config.FixedDelta);
            Run(core, 1, In(mz: 1f, sprint: true, holster: true, jump: true));   // 落地立刻再跳
            float afterBhop = core.HorizontalSpeed;
            Check("Bunnyhop 减速", core.BunnyHopCount >= 1 && afterBhop < afterFirst,
                  $"首跳 {afterFirst:F2} → bhop {afterBhop:F2} m/s，bhops={core.BunnyHopCount}");
        }

        // ═════════ 22. 小障碍档（≤1m）：W + Space 直接翻越，不触发攀爬 ═════════
        {
            var world = new AnalyticWorld(0f);
            world.AddBox(new F3(-5f, 0f, -3.5f), new F3(5f, 0.8f, -3.0f));
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 10);

            Run(core, 70, In(mz: 1f));      // 先走到障碍前（撞住，不会自动翻）
            bool reachedWall = core.Position.Z < -2.4f;
            bool neverClimbed = core.State is not (MovementState.Climb or MovementState.WallHang);

            bool vaulted = false;
            MantleClass asClass = MantleClass.Below;
            for (int i = 0; i < 90; i++)
            {
                core.Step(In(mz: 1f, jump: i == 0), core.Config.FixedDelta);
                if (core.State == MovementState.Vault) { vaulted = true; asClass = core.CurrentMantleClass; }
                if (vaulted && core.State == MovementState.Ground && core.OnGround) break;
            }
            Check("小障碍 0.8m：走近只撞停不自动翻 → W+Space 翻过去（Vault/Below）",
                  reachedWall && neverClimbed && vaulted && asClass == MantleClass.Below
                  && core.Position.Z < -3.5f,
                  $"走到 z={core.Position.Z:F2} 翻越={vaulted}/{asClass}（障碍 z=-3.5..-3.0）");
        }

        // ═════════ 23. 墙档（>1m）：W + Space 抓墙，按住 W 一路爬到台面上 ═════════
        {
            var world = new AnalyticWorld(0f);
            world.AddBox(new F3(-5f, 0f, -3.5f), new F3(5f, 2.5f, -3.0f));
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 10);

            Run(core, 70, In(mz: 1f));      // 走到墙前：撞停，不自动攀爬
            bool bumpedOnly = core.State == MovementState.Ground;

            bool grabbed = false;
            for (int i = 0; i < 60 && !grabbed; i++)
            {
                core.Step(In(mz: 1f, jump: i == 0), core.Config.FixedDelta);
                if (core.State is MovementState.Climb or MovementState.WallHang) grabbed = true;
            }
            // 一直按 W：上爬 → 到顶补爬 → Hang → 翻上台面。
            // 台面只有 0.5m 深，站上去之后必须松 W，否则会直接走下另一侧。
            float topFeet = 0f;
            for (int i = 0; i < 240; i++)
            {
                core.Step(In(mz: 1f), core.Config.FixedDelta);
                if (core.OnGround && core.FeetPosition.Y > 2.0f) { topFeet = core.FeetPosition.Y; break; }
            }
            Check("墙 2.5m：地面按 W 只撞停 → W+Space 抓墙 → 按住 W 爬到台面上",
                  bumpedOnly && grabbed && topFeet > 0f && MathF.Abs(topFeet - 2.5f) < 0.15f,
                  $"撞停={bumpedOnly} 抓到={grabbed} 站上台面时脚底={topFeet:F2}（台面 2.5m）");
        }

        // ═════════ 24. 高墙（6m）：能抓住并持续上爬，但不会瞬移到顶 ═════════
        {
            var world = new AnalyticWorld(0f);
            world.AddBox(new F3(-5f, 0f, -3.5f), new F3(5f, 6.0f, -3.0f));
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 10);

            Run(core, 70, In(mz: 1f));
            bool grabbed = false;
            for (int i = 0; i < 60 && !grabbed; i++)
            {
                core.Step(In(mz: 1f, jump: i == 0), core.Config.FixedDelta);
                if (core.State is MovementState.Climb or MovementState.WallHang) grabbed = true;
            }
            float y0 = core.FeetPosition.Y;
            Run(core, 60, In(mz: 1f));      // 1s 持续上爬
            float climbed = core.FeetPosition.Y - y0;
            // v0.4：攀爬/扒墙合并成一套贴墙状态机，上爬统一用 ClimbSpeed（2.0 m/s）
            Check("高墙 6m：抓住后按住 W 每秒爬 ≈2m（ClimbSpeed 2.0，是状态不是瞬移）",
                  grabbed && climbed > 1.7f && climbed < 2.3f,
                  $"抓到={grabbed} Δy={climbed:F2} m/s，state={core.State}");
        }

        // ═════════ 25. 超高墙（7.5m）：分段攀爬能上去，但单段不许超限 ═════════
        //
        // v0.4.1 起语义变了：到顶脱手（爬升距离到 ClimbAttachOffset 上限）后**允许
        // 重新抓墙继续爬**，于是任意高度的墙都能分段爬上去。这里同时验两件事：
        //   ① 单段爬升不超过 ClimbAttachOffset（不是瞬移到顶）
        //   ② 分段累积最终能站上台面
        {
            var world = new AnalyticWorld(0f);
            world.AddBox(new F3(-5f, 0f, -3.5f), new F3(5f, 7.5f, -3.0f));
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 10);

            Run(core, 70, In(mz: 1f));
            bool topped = false;
            float segBase = 0f;
            float maxSegGain = 0f;
            bool wasClimbing = false;
            for (int i = 0; i < 900; i++)
            {
                core.Step(In(mz: 1f, jump: i == 0), core.Config.FixedDelta);
                bool climbing = core.State is MovementState.Climb or MovementState.WallHang;
                if (climbing && !wasClimbing) segBase = core.FeetPosition.Y;
                if (climbing) maxSegGain = MathF.Max(maxSegGain, core.FeetPosition.Y - segBase);
                wasClimbing = climbing;
                if (core.OnGround && core.FeetPosition.Y > 7.0f) { topped = true; break; }
            }
            Check("超高墙 7.5m：单段爬升不超过 ClimbAttachOffset（不是瞬移到顶）",
                  maxSegGain <= cfg0.ClimbAttachOffset + 0.5f,
                  $"单段最大爬升={maxSegGain:F2}m（ClimbAttachOffset={cfg0.ClimbAttachOffset:F2}m）");
            Check("超高墙 7.5m：到顶脱手后能重新抓墙，分段爬到台面",
                  topped,
                  $"登顶={topped}，脚底={core.FeetPosition.Y:F2}（墙高 7.5m）");
        }

        // ═════════ 26. 窗口翻越（W + Space 穿开口）═════════
        {
            var world = new AnalyticWorld(0f);
            // 窗台：y 0..1.0，z 从 -4.5 到 -3.0（够厚，穿过后能落在窗台上）
            world.AddBox(new F3(-5f, 0f, -4.5f), new F3(5f, 1.0f, -3.0f));
            // 窗楣：y 2.5..4.0
            world.AddBox(new F3(-5f, 2.5f, -3.5f), new F3(5f, 4.0f, -3.0f));

            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 10);

            Run(core, 70, In(mz: 1f));
            bool triggered = false;
            float endZ = 0f;
            for (int i = 0; i < 120; i++)
            {
                core.Step(In(mz: 1f, jump: i == 0), core.Config.FixedDelta);
                if (core.State == MovementState.Window) { triggered = true; endZ = core.Position.Z; }
                if (triggered && core.State != MovementState.Window) break;
            }
            Check("窗口翻越（W+Space 穿过开口）",
                  triggered && endZ < -3.0f,
                  $"触发={triggered}，穿过后 z={endZ:F2}（墙 z=-3.5..-3.0）");
        }

        // ═════════ 27. Fallstun（300hu→800hu 二次曲线）═════════
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            // 抬高半个身高，使**脚底**正好在 15m ⇒ 下落距离恰为 15m
            core.Reset(new F3(0f, 15f + 0.975f, 0f));
            float stun = 0f;
            for (int i = 0; i < 200; i++)
            {
                core.Step(In(), core.Config.FixedDelta);
                if (core.OnGround) { stun = core.FallStunRemaining; break; }
            }
            float expected = cfg0.FallStunDuration(15f);
            Check("Fallstun 生效（15m 落下）", stun > 0.15f && MathF.Abs(stun - expected) < 0.05f,
                  $"stun={stun:F3}s，期望≈{expected:F3}s（7.62m 起算，20.32m 满值 1s）");
        }

        // ═════════ 28. 落地分档（Low/Medium/High/Hard）═════════
        {
            LandingTier TierFrom(float h)
            {
                var world = new AnalyticWorld(0f);
                var core = MakeCore(world);
                core.Reset(new F3(0f, h + 0.975f, 0f));
                for (int i = 0; i < 400; i++)
                {
                    core.Step(In(), core.Config.FixedDelta);
                    if (core.OnGround) return core.LastLandingTier;
                }
                return LandingTier.None;
            }
            LandingTier t3 = TierFrom(3f);
            LandingTier t7 = TierFrom(7f);
            LandingTier t12 = TierFrom(12f);
            LandingTier t25 = TierFrom(25f);
            Check("落地分档 Low/Medium/High/Hard",
                  t3 == LandingTier.Medium && t7 == LandingTier.High &&
                  t12 == LandingTier.Hard && t25 == LandingTier.Hard,
                  $"3m→{t3}，7m→{t7}，12m→{t12}，25m→{t25}");
        }

        // ═════════ 29. 摩擦能刹停 ═════════
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 10);
            Run(core, 90, In(mz: 1f));
            Run(core, 180, In());
            Check("摩擦刹停", core.HorizontalSpeed < 0.2f, $"3s 后 speed={core.HorizontalSpeed:F4}");
        }

        // ═════════ 30. tick 计数 ═════════
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Run(core, 37, In());
            Check("tick 计数", core.TickCount == 37, $"{core.TickCount}（期望 37）");
        }

        // ═════════ 31. 卡顿保护（死螺旋）═════════
        {
            var clock = new ApexTickClock(1f / 60f, maxCatchUpTicks: 4);
            clock.Accumulate(2.0f);
            int ticks = 0;
            while (clock.TryConsumeTick(out _)) ticks++;
            Check("卡顿保护（单帧最多 4 tick）", ticks <= 4 && clock.DroppedTicks > 0,
                  $"消费 {ticks} tick，丢弃 {clock.DroppedTicks}");
        }

        // ═════════ 32. 重力由 wiki 双数据反解自洽 ═════════
        {
            // g = 8h/t² ，h = 56hu, t = 0.74s
            float gDerived = 8f * (56f * MovementConfig.HuToMeter) / (0.74f * 0.74f);
            Check("重力与 wiki 双数据自洽", MathF.Abs(gDerived - cfg0.Gravity) < 0.02f,
                  $"由 56hu/0.74s 反解 g={gDerived:F4}，配置 {cfg0.Gravity:F4}");
        }

        // ═════════ 33. 滑铲物理化：a = g·sinθ − μ·g·cosθ ═════════
        //
        // v0.4.1：滑铲不再用"指数式摩擦"，改成真实的库仑摩擦 + 坡度重力。
        //   · 平地 θ=0  ⇒ a = −μg（匀减速）
        //   · 坡越陡 sinθ 越大 ⇒ 下坡越滑越快
        //   · 临界角 tanθ = μ：超过它重力压不住，一路加速
        {
            float g = cfg0.Gravity;
            float mu0 = cfg0.SlideFrictionCoefficient;
            float a15 = ApexMovementCore.SlideSlopeAcceleration(g, 15f * MathF.PI / 180f, mu0);
            float a30 = ApexMovementCore.SlideSlopeAcceleration(g, 30f * MathF.PI / 180f, mu0);
            float critRad = MathF.Atan(mu0);
            float aCrit = ApexMovementCore.SlideSlopeAcceleration(g, critRad, mu0);
            float critDeg = critRad * 180f / MathF.PI;

            Check("滑铲坡度公式：15° 减速 / 30° 加速 / 临界角 tanθ=μ",
                  a15 < -0.5f && a30 > 0.5f && MathF.Abs(aCrit) < 0.01f &&
                  MathF.Abs(critDeg - 24.23f) < 0.3f,
                  $"15°→{a15:F2} m/s²，30°→{a30:F2} m/s²，临界角 {critDeg:F2}°（a={aCrit:F4}）");

            float a20 = ApexMovementCore.SlideSlopeAcceleration(g, 20f * MathF.PI / 180f, mu0);
            Check("滑铲合加速度随坡角单调上升（越陡越不容易停）",
                  a15 < a20 && a20 < a30,
                  $"15°→{a15:F2} < 20°→{a20:F2} < 30°→{a30:F2} m/s²");
        }

        // ═════════ 34. 地面材质摩擦系数可调（IFrictionWorld）═════════
        {
            float g = cfg0.Gravity;
            float mu0 = cfg0.SlideFrictionCoefficient;

            float decelDefault = MeasureSlideDecel(null, out float d0, out float d1);
            Near("滑铲默认 μ=0.45：减速度 ≈ μg（匀减速）",
                 decelDefault, mu0 * g, mu0 * g * 0.25f);

            float decelIce = MeasureSlideDecel(0.05f, out float i0, out float i1);
            Near("冰面 μ=0.05：几乎不减速（减速度 ≈ 0.05g）",
                 decelIce, 0.05f * g, 0.05f * g * 0.5f + 0.4f);

            float decelRough = MeasureSlideDecel(0.90f, out float r0, out float r1);
            Check("粗糙地面 μ=0.90：减速明显更快（≈ 2 倍默认）",
                  decelRough > decelDefault * 1.5f,
                  $"μ=0.90 实测 {decelRough:F2} m/s² vs 默认 {decelDefault:F2} m/s²");

            Check("三种材质下滑行距离排序：冰面 > 默认 > 粗糙",
                  i1 > d1 && d1 > r1,
                  $"0.4s 后速度：冰面 {i1:F2} > 默认 {d1:F2} > 粗糙 {r1:F2} m/s");
        }

        sb.AppendLine();
        sb.AppendLine($"  合计 {checks} 项断言，失败 {failures} 项");
        return (failures == 0, sb.ToString());
    }
}
