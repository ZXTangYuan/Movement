using System;
using System.Text;
using ApexMovement.Core;

namespace ApexMovement.Tests;

/// <summary>
/// v0.3 三组需求的专项验收：
///
/// ① **攀爬必须是非强制的**（用户原话）：
///    必须同时满足「面向墙壁 + 角色在空中 + 有朝向墙壁的速度」才触发；
///    触发后进入攀爬**状态**（不会自己爬到顶）；按住 W 才向上爬，
///    短时间不按 W 就取消攀爬开始下落；快贴到墙顶时再向上补爬一小段，
///    然后进入**扒墙（Hang）**状态。
/// ② **移动 tick 固定 60/s** 为基准，性能开销大的探针降到 **30/s**。
/// ③ 相机幅度收敛（在 <see cref="CameraTest"/> 里验收）。
///
/// 与 FeatureTest 一样跑在 <see cref="AnalyticWorld"/>（纯数学碰撞替身）上，
/// 因此无头、无引擎依赖、逐 tick 确定性。
/// </summary>
public static class ClimbTest
{
    // ───────────────────────── 辅助 ─────────────────────────

    private static ApexMovementCore MakeCore(AnalyticWorld world, MovementConfig? cfg = null)
    {
        var core = new ApexMovementCore(cfg ?? MovementConfig.Apex());
        core.BindWorld(world);
        return core;
    }

    private static MovementInput In(float mz = 0f, float mx = 0f, bool jump = false,
                                    bool crouch = false, bool sprint = false) => new()
    {
        MoveX = mx,
        MoveZ = mz,
        JumpPressed = jump,
        JumpHeld = jump,
        CrouchHeld = crouch,
        SprintHeld = sprint,
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
    /// 一堵竖直墙：世界地面 y=0，墙占 z ∈ [-4, -2]（朝玩家那面在 z=-2，法线 +Z），
    /// 玩家在 z=0 附近、yaw=0（前向 = -Z），于是"面向墙"天然成立。
    /// 厚度取 2m 让台面足够深，避免上墙后站在悬崖边上。
    /// </summary>
    private static AnalyticWorld WallWorld(float wallTop, float groundY = 0f)
    {
        var w = new AnalyticWorld(groundY);
        w.AddBox(new F3(-5f, groundY, -4f), new F3(5f, groundY + wallTop, -2f));
        return w;
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

        var cfg0 = MovementConfig.Apex();

        // ═════════ 1. 非强制①：站在地面上贴墙按 W —— 不许攀爬 ═════════
        {
            var world = WallWorld(wallTop: 6f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.0f, -1.0f));
            Settle(core, 40);          // 落到地面

            bool everClimbed = false;
            MovementState firstNonGround = MovementState.Ground;
            bool sawNonGround = false;
            for (int i = 0; i < 120; i++)
            {
                core.Step(In(mz: 1f), core.Config.FixedDelta);
                if (core.State == MovementState.Climb) everClimbed = true;
                if (!sawNonGround && core.State != MovementState.Ground)
                {
                    sawNonGround = true;
                    firstNonGround = core.State;
                }
            }

            Check("非强制①：地面贴墙按住 W 不会进入攀爬",
                  !everClimbed,
                  $"onGround 起手，120 tick 内是否 Climb={everClimbed}（首个非地面状态={firstNonGround}）");
        }

        // ═════════ 2. 非强制②：空中 + 面向墙，但没有朝墙速度 —— 不许攀爬 ═════════
        {
            var world = WallWorld(wallTop: 6f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 3.0f, -1.0f));   // 空中，水平速度 0
            core.SetVelocity(F3.Zero);

            // 注意：这里**不给 W**。给 W 会让空中加速产生朝墙速度（那是另一条正确的
            // 触发路径），就测不到"没有朝墙速度不挂"这件事了 —— 视角依旧朝向墙。
            float maxApproach = 0f;
            bool everClimbed = false;
            for (int i = 0; i < 20; i++)
            {
                core.Step(In(), core.Config.FixedDelta);
                // 朝墙速度 = -dot(vel, 墙法线 +Z) = -vel.Z
                maxApproach = MathF.Max(maxApproach, -core.Velocity.Z);
                if (core.State == MovementState.Climb) everClimbed = true;
            }

            Check("非强制②：空中面向墙但无朝墙速度 → 不攀爬",
                  !everClimbed,
                  $"30 tick 内 max(朝墙速度)={maxApproach:F3}（门槛 {cfg0.ClimbMinApproachSpeed:F2}），Climb={everClimbed}");

            // 对照组必须证明确实"在空中"——否则"没挂住"可能只是因为早就落地了。
            // 起点脚底 2.025m、重力 20.78 m/s²：20 tick（0.33s）只落 ~1.15m，
            // 还离地 0.8m 以上；跑 30 tick 就会落地，对照组失去意义。
            Check("非强制②：确实处于空中（对照组有效）",
                  !core.OnGround && core.FeetPosition.Y > 0.5f,
                  $"onGround={core.OnGround} feetY={core.FeetPosition.Y:F3}（起点 2.025，20 tick 后应仍 >0.5）");
        }

        // ═════════ 3. 非强制③：面向墙 + 背对负速度 —— 必须有朝墙速度才挂 ═════════
        {
            var world = WallWorld(wallTop: 6f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 3.0f, -1.0f));
            core.SetVelocity(new F3(0f, 0f, -6f));   // 朝墙（-Z）6 m/s

            core.Step(In(mz: 1f), core.Config.FixedDelta);

            Check("非强制③：空中 + 面向墙 + 朝墙速度 → 进入攀爬",
                  core.State == MovementState.Climb,
                  $"state={core.State}，朝墙速度 6.0（门槛 {cfg0.ClimbMinApproachSpeed:F2}）");

            Check("攀爬是**状态**而不是直接爬顶（挂上瞬间竖直速度≈0）",
                  MathF.Abs(core.VerticalSpeed) < 0.01f && core.FeetPosition.Y < 3.0f,
                  $"vertSpeed={core.VerticalSpeed:F4} feetY={core.FeetPosition.Y:F3}（挂住时不该自己上升）");
        }

        // ═════════ 4. 按住 W 才向上爬 ═════════
        {
            var world = WallWorld(wallTop: 6f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 3.0f, -1.0f));
            core.SetVelocity(new F3(0f, 0f, -6f));
            core.Step(In(mz: 1f), core.Config.FixedDelta);

            float y0 = core.FeetPosition.Y;
            Run(core, 30, In(mz: 1f));        // 0.5 s
            float y1 = core.FeetPosition.Y;

            Check("按住 W 向上爬（0.5s 至少 +0.6m）",
                  core.State == MovementState.Climb && y1 - y0 > 0.6f,
                  $"ΔfeetY={y1 - y0:F3}（ClimbSpeed={cfg0.ClimbSpeed:F2} → 期望≈{cfg0.ClimbSpeed * 0.5f:F2}）");
        }

        // ═════════ 5. 完全松手 → 宽限后取消攀爬、开始下落 ═════════
        {
            var world = WallWorld(wallTop: 6f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 3.0f, -1.0f));
            core.SetVelocity(new F3(0f, 0f, -6f));
            core.Step(In(mz: 1f), core.Config.FixedDelta);
            Run(core, 10, In(mz: 1f));        // 先爬一小段

            Check("攀爬中（对照组）", core.State == MovementState.Climb,
                  $"state={core.State}");

            // 宽限期内仍挂在墙上
            int graceTicks = (int)MathF.Ceiling(cfg0.ClimbNoInputGrace / cfg0.FixedDelta) - 1;
            Run(core, Math.Max(1, graceTicks), In(mz: 0f));
            MovementState duringGrace = core.State;

            // 再跑 20 tick：早已过宽限
            Run(core, 20, In(mz: 0f));
            MovementState afterGrace = core.State;
            float vyAfter = core.VerticalSpeed;

            Check("松手后在宽限期内仍保持攀爬",
                  duringGrace == MovementState.Climb,
                  $"宽限 {cfg0.ClimbNoInputGrace:F2}s（{graceTicks} tick）后 state={duringGrace}");

            Check("松手超时 → 取消攀爬并开始下落",
                  afterGrace == MovementState.Air && vyAfter < -0.1f,
                  $"state={afterGrace} vertSpeed={vyAfter:F3}");
        }

        // ═════════ 6. 爬到墙顶 → 补爬一小段 → 扒墙（Hang）═════════
        {
            var world = WallWorld(wallTop: 2f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.0f, -1.0f));
            core.SetVelocity(new F3(0f, 0f, -6f));
            core.Step(In(mz: 1f), core.Config.FixedDelta);

            Check("攀爬起步（对照组）", core.State == MovementState.Climb,
                  $"state={core.State} feetY={core.FeetPosition.Y:F3}");

            bool everApproaching = false;
            bool everHang = false;
            float attachFeet = core.FeetPosition.Y;
            float hangFeet = 0f;

            for (int i = 0; i < 200; i++)
            {
                core.Step(In(mz: 1f), core.Config.FixedDelta);
                var s = core.Snapshot;
                if (s.ClimbApproaching) everApproaching = true;
                if (core.State == MovementState.Hang) { everHang = true; hangFeet = core.FeetPosition.Y; break; }
            }

            Check("爬到墙顶会先进入「补爬」阶段",
                  everApproaching,
                  $"ClimbApproaching 出现过={everApproaching}");

            Check("补爬结束进入扒墙状态（Hang）",
                  everHang,
                  $"state={core.State}，攀爬起点脚底={attachFeet:F3} → 挂姿脚底={hangFeet:F3}（墙顶 2.00，期望 ≈ 1.75）");

            Check("挂姿位置在墙顶下方约 0.25m",
                  everHang && MathF.Abs(hangFeet - (2.0f - 0.25f)) < 0.12f,
                  $"hangFeet={hangFeet:F3}，期望 1.750 ± 0.120");
        }

        // ═════════ 7. 扒墙里按 W 上墙（复用 v0.2 Mantle pull）═════════
        {
            var world = WallWorld(wallTop: 2f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.0f, -1.0f));
            core.SetVelocity(new F3(0f, 0f, -6f));
            core.Step(In(mz: 1f), core.Config.FixedDelta);

            // 爬到 Hang
            bool reachedHang = false;
            for (int i = 0; i < 200 && !reachedHang; i++)
            {
                core.Step(In(mz: 1f), core.Config.FixedDelta);
                if (core.State == MovementState.Hang) reachedHang = true;
            }

            Check("（前置）已进入扒墙", reachedHang, $"state={core.State}");

            // 上拉过程：一旦 Mantle/Vault 结束就停手，**不要**继续按着 W 往台面上走
            // （台面只有 2m 深，按着 W 会直接走过沿口掉下去 —— 那是正常物理，
            //  不是这条断言要测的东西）。
            bool sawMantle = false;
            for (int i = 0; i < 150; i++)
            {
                core.Step(In(mz: 1f), core.Config.FixedDelta);
                if (core.State is MovementState.Mantle or MovementState.Vault)
                {
                    sawMantle = true;
                    continue;
                }
                if (sawMantle) break;
            }
            Settle(core, 12);   // 松手站定，让地面吸附生效

            Check("扒墙里按 W 能上墙（进入 Mantle 并落到台面）",
                  sawMantle && core.State == MovementState.Ground && core.OnGround,
                  $"sawMantle={sawMantle} state={core.State} onGround={core.OnGround}");

            Check("上墙后脚底站在台面高度（≈2.00m）",
                  MathF.Abs(core.FeetPosition.Y - 2.0f) < 0.20f,
                  $"feetY={core.FeetPosition.Y:F3}，期望 2.000 ± 0.200");
        }

        // ═════════ 7b. 站在台面上 → 走出边缘落地（不许卡进墙体）═════════
        //
        // 这条防的是一个很容易复发的坑：运动学落点（翻越 / 攀爬补爬）会把胶囊
        // **恰好**放到台面上（脚底 == 台面高度）。若 Sweep 把"刚好贴住"当成没撞到，
        // 角色上墙后立刻失重、而且会一边"空中加速"一边钻进墙体内部。
        {
            var world = WallWorld(wallTop: 2f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 3.0f, -2.6f));   // 台面上方（台面 y=2.0）
            Settle(core, 20);

            Check("台面上站得住（贴面接触算着地）",
                  core.OnGround && MathF.Abs(core.FeetPosition.Y - 2.0f) < 0.05f,
                  $"onGround={core.OnGround} feetY={core.FeetPosition.Y:F3}（台面 2.000）");

            Run(core, 90, In(mz: 1f));   // 朝 -Z 走出台面边缘

            Check("走出台面边缘会掉到地面",
                  core.OnGround && core.FeetPosition.Y < 0.2f,
                  $"onGround={core.OnGround} feetY={core.FeetPosition.Y:F3} z={core.Position.Z:F3}");

            // 墙占 z∈[-4,-2]，胶囊半径 0.35 ⇒ 中心落在 [-4.35,-1.65] 且脚底在台面
            // 以下，就意味着胶囊穿进了墙体内部。
            bool insideWall = core.Position.Z > -4.35f && core.Position.Z < -1.65f &&
                              core.FeetPosition.Y < 1.9f;
            Check("不会卡进墙体内部",
                  !insideWall,
                  $"pos.z={core.Position.Z:F3} feetY={core.FeetPosition.Y:F3}（穿模区间 z∈(-4.35,-1.65) 且 feet<1.9）");
        }

        // ═════════ 8. 扒墙里按 S / 蹲 取消 → 掉回空中 ═════════
        {
            var world = WallWorld(wallTop: 2f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.0f, -1.0f));
            core.SetVelocity(new F3(0f, 0f, -6f));
            core.Step(In(mz: 1f), core.Config.FixedDelta);

            bool reachedHang = false;
            for (int i = 0; i < 200 && !reachedHang; i++)
            {
                core.Step(In(mz: 1f), core.Config.FixedDelta);
                if (core.State == MovementState.Hang) reachedHang = true;
            }

            Run(core, 3, In(mz: -1f));   // 按 S

            Check("扒墙里按 S 取消并掉回空中",
                  reachedHang && core.State == MovementState.Air,
                  $"reachedHang={reachedHang} state={core.State}");
        }

        // ═════════ 9. 重附着规则：爬过的面不能立刻再挂回去 ═════════
        {
            var world = WallWorld(wallTop: 6f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 3.0f, -1.0f));
            core.SetVelocity(new F3(0f, 0f, -6f));
            core.Step(In(mz: 1f), core.Config.FixedDelta);
            float attachY = core.Position.Y;

            Run(core, 20, In(mz: 1f));   // 往上爬
            float climbedY = core.Position.Y;

            // 松手脱离（宽限 0.22s ≈ 13 tick，逐 tick 推进到真的掉下来为止）
            bool detached = false;
            for (int i = 0; i < 40; i++)
            {
                core.Step(In(mz: 0f), core.Config.FixedDelta);
                if (core.State != MovementState.Climb) { detached = true; break; }
            }
            float dropY = core.Position.Y;

            // 立刻再次朝墙推：位置仍高于上次附着点 ⇒ 同一面墙不允许立刻重附着
            core.SetVelocity(new F3(0f, 0f, -6f));
            core.Step(In(mz: 1f), core.Config.FixedDelta);
            bool reattached = core.State == MovementState.Climb;

            Check("同一面墙爬过之后不能在同一高度以上立刻重附着",
                  detached && !reattached && dropY > attachY,
                  $"attachY={attachY:F2} climbedY={climbedY:F2} dropY={dropY:F2} " +
                  $"detached={detached} reattached={reattached}");
        }

        // ═════════ 10. tick 基准固定 60/s ═════════
        {
            var cfg = MovementConfig.Apex();
            Check("tick 基准固定 60/s（配置）",
                  cfg.TickRate == 60 && cfg.MaxCatchUpTicks == 4,
                  $"TickRate={cfg.TickRate} MaxCatchUpTicks={cfg.MaxCatchUpTicks}");
            Check("FixedDelta = 1/60",
                  MathF.Abs(cfg.FixedDelta - 1f / 60f) < 1e-6f,
                  $"FixedDelta={cfg.FixedDelta:F6}（1/60={1f / 60f:F6}）");

            // 30 fps 帧 → 每帧吃 2 tick
            var clock30 = new ApexTickClock(1f / 60f, 4);
            clock30.Accumulate(1f / 30f);
            int t30 = 0;
            while (t30 < 100 && clock30.TryConsumeTick(out _)) t30++;
            Check("30fps 下每帧消费 2 个 tick（60 tick/s 不漂移）",
                  t30 == 2,
                  $"1 帧 1/30s 消费 {t30} tick（期望 2）");

            // 144 fps 帧 → 10 秒累计应正好 600 tick（最多差 1 个残 tick）
            //
            // 用 10 秒而不是 1 秒：float 累加 1/144 本身有舍入误差，
            // 单秒可能只凑出 59 个 tick（残差留在 accumulator 里），这不叫漂移；
            // 真正要证明的是"误差不会随时间累积"——10 秒仍是 599/600 就成立。
            var clock144 = new ApexTickClock(1f / 60f, 4);
            long total = 0;
            for (int i = 0; i < 1440; i++)
            {
                clock144.Accumulate(1f / 144f);
                while (clock144.TryConsumeTick(out _)) total++;
            }
            Check("144fps 下 10 秒整好 600 个 tick（无累积漂移）",
                  total >= 599 && total <= 600,
                  $"1440 帧 × 1/144s → {total} tick（期望 599–600）");

            // 卡顿保护：一帧 1s 只放 4 tick，其余丢弃
            var clockStall = new ApexTickClock(1f / 60f, 4);
            clockStall.Accumulate(1f);
            int stallTicks = 0;
            while (stallTicks < 100 && clockStall.TryConsumeTick(out _)) stallTicks++;
            Check("卡顿时最多补 4 tick（其余丢弃，有计数）",
                  stallTicks <= 4 && clockStall.DroppedTicks > 0,
                  $"消费 {stallTicks} tick，丢弃 {clockStall.DroppedTicks}（上限 4）");
        }

        // ═════════ 11. 昂贵探针降到 30/s ═════════
        {
            Check("翻越探针抽样间隔为 2（60Hz → 30Hz）",
                  cfg0.TraversalProbeInterval == 2,
                  $"TraversalProbeInterval={cfg0.TraversalProbeInterval}");

            // 地面站定 60 tick：翻越探针应只跑约 30 次
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.0f, 0f));
            Settle(core, 40);
            int before = core.TraversalProbeCount;
            Run(core, 60, In());
            int traumaProbes = core.TraversalProbeCount - before;

            Check("60 个地面 tick 只做约 30 次翻越探针",
                  traumaProbes >= 28 && traumaProbes <= 32,
                  $"实测 {traumaProbes} 次（期望 30 ± 2，60Hz 全量会是 60）");

            // 空中 40 tick：攀爬探针每 tick 都跑（很便宜）
            var core2 = MakeCore(WallWorld(6f));
            core2.Reset(new F3(3f, 10f, 0f));   // 高空、离墙很远（射线打不到），不会真的挂住
            int before2 = core2.ClimbProbeCount;
            Run(core2, 40, In());
            int climbProbes = core2.ClimbProbeCount - before2;

            Check("攀爬探针每 tick 都跑（间隔 1）",
                  climbProbes == 40,
                  $"实测 {climbProbes} 次（期望 40）");
        }

        sb.AppendLine();
        sb.AppendLine($"  合计 {checks} 项断言，失败 {failures} 项");
        return (failures == 0, sb.ToString());
    }
}
