using System;
using System.Text;
using ApexMovement.Core;

namespace ApexMovement.Tests;

/// <summary>
/// v0.4 贴墙 / 三档障碍 / 探针 的专项验收（纯数学世界，逐 tick 确定性）。
///
/// 这一组断言逐条对应需求方 m02704 的四条反馈：
///
/// **需求 2（贴墙门控）**
///   「人物在墙壁旁时不要按一下 W 就开始攀爬，按 W 时会撞墙然后停止移动
///    （或是贴着墙壁朝 W 方向移动），只有按 W 贴墙 + Space 跳起后才会爬墙」
///   → 断言 1 / 2 / 3 / 4
///
/// **需求 3（扒墙默认化）**
///   「扒墙我希望是默认扒墙，不需要按任何按键，玩家攀爬时应该持续按住 W 才能一直往上攀爬，
///    途中松开了 W 就会进入扒墙状态，如果持续一小段时间没有按键输入，就会掉落，
///    中途如果按 a 或 d 则会在墙上左右移动，按 wa 或 wd 则会向左上右上移动，
///    按 w 继续向上攀爬，只要按了 s，就会取消扒墙直接垂直下落，
///    下落的过程中按 w 可以继续扒住墙」
///   → 断言 5 … 18
///
/// **需求 5（三档障碍分级）**
///   「>1m 是墙 / ≤1m 是小障碍（W+Space 翻越，不触发攀爬）/ ≤0.25m 是台阶
///    （无速度衰减，只改方向不改大小，tan θ = b/a）」
///   → 断言 19 … 27
///
/// **v0.3 遗留（tick 基准与探针开销）**
///   → 断言 28 … 34
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
                                    bool crouch = false, bool sprint = false,
                                    float mouseYaw = 0f) => new()
    {
        MoveX = mx,
        MoveZ = mz,
        JumpPressed = jump,
        JumpHeld = jump,
        CrouchHeld = crouch,
        SprintHeld = sprint,
        MouseDeltaYaw = mouseYaw,
    };

    /// <summary>跑 N 个 tick（跳跃只在第一 tick 是上升沿）。</summary>
    private static void Run(ApexMovementCore core, int ticks, MovementInput input)
    {
        float dt = core.Config.FixedDelta;
        for (int i = 0; i < ticks; i++)
        {
            MovementInput i2 = input;
            if (i > 0) { i2.JumpPressed = false; i2.JumpHeld = false; i2.MouseDeltaYaw = 0f; }
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

    /// <summary>
    /// 「贴墙 + W + Space 起跳 → 抓墙」的完整驱动。
    /// 返回 true 表示成功挂上（Climb 或 WallHang）。
    /// </summary>
    private static bool JumpAndGrab(ApexMovementCore core, float wallTop, int maxTicks = 60)
    {
        // 先贴着墙走几 tick 建立朝墙姿态（v0.4 的抓墙不再看朝墙动量，
        // 但"先走到墙边"仍然是玩家真实操作顺序）。
        Run(core, 6, In(mz: 1f));
        for (int i = 0; i < maxTicks; i++)
        {
            MovementInput input = In(mz: 1f, jump: i == 0);
            core.Step(input, core.Config.FixedDelta);
            if (core.State is MovementState.Climb or MovementState.WallHang) return true;
        }
        return false;
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

        // ═════════════ 需求 2：贴墙门控 ═════════════

        // 1. 地面贴墙按住 W —— 撞墙停住，绝不攀爬
        {
            var world = WallWorld(wallTop: 6f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.0f, -1.0f));
            Settle(core, 40);

            bool everGrab = false;
            for (int i = 0; i < 120; i++)
            {
                core.Step(In(mz: 1f), core.Config.FixedDelta);
                if (core.State is MovementState.Climb or MovementState.WallHang) everGrab = true;
            }

            Check("① 地面贴墙按住 W 120 tick → 不攀爬不扒墙（只撞停）",
                  !everGrab && core.OnGround,
                  $"state={core.State} onGround={core.OnGround} z={core.Position.Z:F3}");

            Check("① 撞墙后停在墙面外侧（没穿模、也没被推远）",
                  core.Position.Z > -2.0f && core.Position.Z < -1.50f,
                  $"z={core.Position.Z:F3}（墙面 -2.00，期望 ≈ -1.594 = 半径 0.4064）");
        }

        // 2. 地面贴墙 + W + Space —— 起跳后抓住
        {
            var world = WallWorld(wallTop: 6f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.0f, -1.0f));
            Settle(core, 40);
            float startFeet = core.FeetPosition.Y;

            bool grabbed = JumpAndGrab(core, 6f);

            Check("② 地面贴墙 + W + Space 起跳 → 抓住墙（Climb / WallHang）",
                  grabbed,
                  $"state={core.State} feet={core.FeetPosition.Y:F2}（起跳前 {startFeet:F2}）");

            Check("② 抓住后脚底已经离地上升",
                  grabbed && core.FeetPosition.Y > startFeet + 0.2f,
                  $"feet={core.FeetPosition.Y:F2}（起跳前 {startFeet:F2}）");
        }

        // 3. 空中面向墙、正在**上升**、不按 W —— 不许抓（避免"向上跳经过墙面被吸住"）
        {
            var world = WallWorld(wallTop: 6f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 3.0f, -1.0f));
            core.SetVelocity(new F3(0f, 4f, 0f));   // 正在上升

            bool everGrab = false;
            for (int i = 0; i < 8; i++)   // 起跳后到顶点约 11.5 tick，这里只覆盖**上升段**
            {
                core.Step(In(), core.Config.FixedDelta);   // 不给 W
                if (core.State is MovementState.Climb or MovementState.WallHang) everGrab = true;
            }

            Check("③ 空中上升 + 面向墙 + 不按 W → 不抓（上升不打断）",
                  !everGrab,
                  $"state={core.State} vy={core.VerticalSpeed:F2}（8 tick 内是否抓={everGrab}）");
        }

        // 4. 空中面向墙、正在**下落**、不按任何键 —— 自动抓住（需求「扒墙默认」）
        {
            var world = WallWorld(wallTop: 6f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 3.0f, -1.0f));
            core.SetVelocity(new F3(0f, -2f, 0f));   // 正在下落

            bool grabbed = false;
            for (int i = 0; i < 10; i++)
            {
                core.Step(In(), core.Config.FixedDelta);
                if (core.State is MovementState.Climb or MovementState.WallHang) { grabbed = true; break; }
            }

            Check("④ 空中下落 + 贴墙 + 不按任何键 → 自动抓住（扒墙默认化）",
                  grabbed && core.State == MovementState.WallHang,
                  $"state={core.State}（不按 W 时应停在 WallHang 而不是 Climb）");
        }

        // 5. 离墙太远 —— 挂不上
        {
            var world = WallWorld(wallTop: 6f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 3.0f, 0.5f));      // 离墙面 2.5m
            core.SetVelocity(new F3(0f, -2f, 0f));

            bool everGrab = false;
            for (int i = 0; i < 10; i++)
            {
                core.Step(In(), core.Config.FixedDelta);
                if (core.State is MovementState.Climb or MovementState.WallHang) everGrab = true;
            }

            Check("⑤ 离墙超过 reach → 挂不上",
                  !everGrab,
                  $"state={core.State} z={core.Position.Z:F2}");
        }

        // ═════════════ 需求 3：攀爬 / 扒墙输入映射 ═════════════

        // 6. 按住 W 抓住 → 状态是 Climb（在爬）
        // 7. 松 W → 回到 WallHang（扒住不动）
        {
            var world = WallWorld(wallTop: 6f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.0f, -1.0f));
            Settle(core, 40);

            bool grabbed = JumpAndGrab(core, 6f);
            Check("⑥ 按住 W 抓住 → 状态 = Climb（正在上爬）",
                  grabbed && core.State == MovementState.Climb,
                  $"state={core.State}");

            float yBefore = core.FeetPosition.Y;
            Run(core, 30, In(mz: 1f));   // 继续按 W 上爬 0.5s
            float climbed = core.FeetPosition.Y - yBefore;
            Check("⑦ 按住 W 上爬 ≈ ClimbSpeed（2 m/s）",
                  climbed > 0.85f && climbed < 1.15f,
                  $"0.5s 爬升 {climbed:F2}m（期望 ≈ 1.00）");

            // 松 W
            Run(core, 3, In());
            Check("⑧ 途中松开 W → 进入扒墙状态（WallHang）",
                  core.State == MovementState.WallHang,
                  $"state={core.State}");
        }

        // 9. 扒墙状态下的移动 / 掉落 / 松手
        {
            var world = WallWorld(wallTop: 6f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.0f, -1.0f));
            Settle(core, 40);
            core.Step(In(mz: 1f, jump: true), core.Config.FixedDelta);   // 起跳
            Run(core, 10, In(mz: 1f));                                    // 抓住
            Run(core, 2, In());                                           // 松 W → WallHang

            // 挂住时对外速度恒为 0
            Check("⑨ 挂住时对外速度恒为 0（HUD 上 speed=0 / vy=0）",
                  core.State == MovementState.WallHang &&
                  core.HorizontalSpeed < 1e-3f && MathF.Abs(core.VerticalSpeed) < 1e-3f,
                  $"state={core.State} speed={core.HorizontalSpeed:F4} vy={core.VerticalSpeed:F4}");

            // 贴墙距离 = 半径 + 皮肤
            Check("⑨ 挂住时贴到墙面（离墙 ≈ 半径 + 0.02）",
                  MathF.Abs(core.Position.Z - (-2f + cfg0.CapsuleRadius + 0.02f)) < 0.06f,
                  $"z={core.Position.Z:F3}（期望 ≈ {-2f + cfg0.CapsuleRadius + 0.02f:F3}）");

            // A / D 横移
            float x0 = core.Position.X;
            Run(core, 30, In(mx: 1f));
            float dxRight = core.Position.X - x0;
            Check("⑩ 按 D 沿墙横移 ≈ WallHangShimmySpeed（0.8 m/s）",
                  MathF.Abs(dxRight - 0.4f) < 0.12f,
                  $"0.5s 横移 {dxRight:F2}m（期望 ≈ +0.40）");

            float x1 = core.Position.X;
            Run(core, 30, In(mx: -1f));
            float dxLeft = core.Position.X - x1;
            Check("⑩ 按 A 沿墙反向横移",
                  dxLeft < -0.25f,
                  $"0.5s 横移 {dxLeft:F2}m（期望 ≈ -0.40）");
        }

        // 11. WA / WD 斜移（左上 / 右上）
        {
            var world = WallWorld(wallTop: 6f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.0f, -1.0f));
            Settle(core, 40);
            core.Step(In(mz: 1f, jump: true), core.Config.FixedDelta);
            Run(core, 10, In(mz: 1f));

            float x0 = core.Position.X, y0 = core.FeetPosition.Y;
            Run(core, 30, In(mz: 1f, mx: 1f));
            float dx = core.Position.X - x0, dy = core.FeetPosition.Y - y0;

            Check("⑪ 按 W+D → 向右上斜移（同时有 +X 与 +Y）",
                  dx > 0.25f && dy > 0.35f,
                  $"Δx={dx:F2} Δy={dy:F2}（期望两者都为正）");
        }

        // 12. 无输入一小段时间 → 掉落
        {
            var world = WallWorld(wallTop: 6f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.0f, -1.0f));
            Settle(core, 40);
            core.Step(In(mz: 1f, jump: true), core.Config.FixedDelta);
            Run(core, 10, In(mz: 1f));
            Run(core, 2, In());     // → WallHang

            bool wasHanging = core.State == MovementState.WallHang;
            int ticksToFall = 0;
            for (int i = 0; i < 120; i++)
            {
                core.Step(In(), core.Config.FixedDelta);
                ticksToFall++;
                if (core.State != MovementState.WallHang) break;
            }

            Check("⑫ 挂住后完全无输入 → 一小段时间后自动掉落",
                  wasHanging && ticksToFall > 5 && ticksToFall < 100,
                  $"掉落用时 {ticksToFall} tick（{ticksToFall / 60f:F2}s，"
                  + $"配置 WallHangIdleFallTime={cfg0.WallHangIdleFallTime:F2}s）");
        }

        // 13. 按 S → 立刻松手垂直下落
        {
            var world = WallWorld(wallTop: 6f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.0f, -1.0f));
            Settle(core, 40);
            core.Step(In(mz: 1f, jump: true), core.Config.FixedDelta);
            Run(core, 10, In(mz: 1f));
            Run(core, 2, In());

            float yHang = core.FeetPosition.Y;
            core.Step(In(mz: -1f), core.Config.FixedDelta);   // 按 S
            bool detached = core.State == MovementState.Air;

            Run(core, 12, In());
            float fell = yHang - core.FeetPosition.Y;

            Check("⑬ 按 S → 立刻取消扒墙，回 Air",
                  detached,
                  $"state={core.State}");
            Check("⑬ 松手后垂直下落（12 tick 掉 >0.3m，且有向下速度）",
                  fell > 0.3f && core.VerticalSpeed < -2f,
                  $"Δy=-{fell:F2}m vy={core.VerticalSpeed:F2}");
        }

        // 14. 下落过程中按 W → 重新扒住墙
        {
            var world = WallWorld(wallTop: 12f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 5.0f, -1.0f));
            core.SetVelocity(new F3(0f, -2f, 0f));
            Run(core, 3, In());                    // 下落自动抓住

            bool hung = core.State == MovementState.WallHang;
            float yHang = core.FeetPosition.Y;

            core.Step(In(mz: -1f), core.Config.FixedDelta);   // S 松手
            Run(core, 4, In());                              // 自由下落

            bool regained = false;
            for (int i = 0; i < 60; i++)
            {
                core.Step(In(mz: 1f), core.Config.FixedDelta);  // 下落中按 W
                if (core.State is MovementState.Climb or MovementState.WallHang) { regained = true; break; }
            }

            Check("⑭ 下落过程中按 W → 重新扒住墙",
                  hung && regained,
                  $"松手前 state={(hung ? "WallHang" : "未挂上")} feet={yHang:F2}，"
                  + $"现在 state={core.State}");
        }

        // 15. 按 Space → 蹬墙跳（墙上没有可站台面时）
        {
            var world = WallWorld(wallTop: 6f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.0f, -1.0f));
            Settle(core, 40);
            core.Step(In(mz: 1f, jump: true), core.Config.FixedDelta);
            Run(core, 10, In(mz: 1f));
            Run(core, 2, In());

            core.Step(In(jump: true), core.Config.FixedDelta);
            Check("⑮ 挂住时按 Space → 蹬墙跳（离墙 + 向上）",
                  core.State == MovementState.Air &&
                  core.Velocity.Z > 0.5f && core.VerticalSpeed > 3f,
                  $"state={core.State} vz={core.Velocity.Z:F2} vy={core.VerticalSpeed:F2}");
        }

        // 16. 视角甩开太大 → 松手
        {
            var world = WallWorld(wallTop: 6f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.0f, -1.0f));
            Settle(core, 40);
            core.Step(In(mz: 1f, jump: true), core.Config.FixedDelta);
            Run(core, 10, In(mz: 1f));
            Run(core, 2, In());

            float yaw0 = core.Yaw;
            core.Step(In(mouseYaw: MathF.PI * 0.5f), core.Config.FixedDelta);   // 甩 90°
            Check("⑯ 视角偏离墙面法线超过 WallHangMaxViewAngle → 松手",
                  core.State == MovementState.Air,
                  $"yaw {yaw0:F2}→{core.Yaw:F2}，state={core.State}");
        }

        // 17. 太矮的障碍不挂 —— 直接迈/翻过去更合理（v0.4：门槛降到小障碍档 1m 以下）
        {
            var world = WallWorld(wallTop: 0.5f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.0f, -1.0f));
            Settle(core, 40);
            core.Step(In(mz: 1f, jump: true), core.Config.FixedDelta);
            bool everGrab = false;
            for (int i = 0; i < 40; i++)
            {
                core.Step(In(mz: 1f), core.Config.FixedDelta);
                if (core.State is MovementState.Climb or MovementState.WallHang) everGrab = true;
            }
            Check("⑰ 半米高的坎不会被挂住（该直接迈过去）",
                  !everGrab,
                  $"state={core.State}（automantle_disable_hang：脚底 +0.6m 打不到墙）");
        }

        // 17b. 刚过小障碍档的 1.5m 墙**可以**挂 —— v0.3 曾把它也拒了（1.0…2.03m 是死区）
        {
            var world = WallWorld(wallTop: 2.5f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.0f, -1.0f));
            Settle(core, 40);
            core.Step(In(mz: 1f, jump: true), core.Config.FixedDelta);
            bool grabbed = false;
            for (int i = 0; i < 40 && !grabbed; i++)
            {
                core.Step(In(mz: 1f), core.Config.FixedDelta);
                if (core.State is MovementState.Climb or MovementState.WallHang) grabbed = true;
            }
            Check("⑰b 2.5m 墙（跳不上、必须攀爬）能挂住（v0.3 的 1.0…2.03m 死区已修）",
                  grabbed,
                  $"state={core.State}，抓到={grabbed}");
        }

        // 18. 挂墙期间墙消失（比如墙被打掉）→ 自动脱手，不卡在空中
        {
            var world = WallWorld(wallTop: 6f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.0f, -1.0f));
            Settle(core, 40);
            core.Step(In(mz: 1f, jump: true), core.Config.FixedDelta);
            Run(core, 10, In(mz: 1f));

            // 把墙"拆掉"：换成一张无限地面（射线打不到墙了）
            core.BindWorld(new AnalyticWorld(0f));
            bool detached = false;
            for (int i = 0; i < 20; i++)
            {
                core.Step(In(mz: 1f), core.Config.FixedDelta);
                if (core.State is not (MovementState.Climb or MovementState.WallHang)) { detached = true; break; }
            }
            Check("⑱ 墙消失后自动脱手（不卡在挂墙状态）",
                  detached,
                  $"state={core.State}");
        }

        // ═════════════ 需求 5：三档障碍分级 ═════════════

        // 19. ≤0.25m 台阶：直接走过去，无动作、速度大小不变、中途出现斜向上的速度
        {
            var world = new AnalyticWorld(0f);
            world.AddBox(new F3(-6f, 0f, -6f), new F3(6f, 0.20f, -4f));   // 0.20m 台阶
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 20);

            Run(core, 40, In(mz: 1f));       // 先跑到满速
            float speedBefore = core.HorizontalSpeed;

            bool sawAction = false;
            float maxUpVel = 0f;
            float minTotal = float.MaxValue;
            float maxTotal = 0f;
            float peakFeet = core.FeetPosition.Y;

            for (int i = 0; i < 90; i++)
            {
                core.Step(In(mz: 1f), core.Config.FixedDelta);
                if (core.State is MovementState.Vault or MovementState.Climb
                    or MovementState.WallHang or MovementState.Mantle) sawAction = true;
                maxUpVel = MathF.Max(maxUpVel, core.VerticalSpeed);
                float total = core.Velocity.Length();
                minTotal = MathF.Min(minTotal, total);
                maxTotal = MathF.Max(maxTotal, total);
                peakFeet = MathF.Max(peakFeet, core.FeetPosition.Y);
            }

            Check("⑲ 0.20m 台阶：不触发任何动作（无 Vault / Climb / WallHang / Mantle）",
                  !sawAction,
                  $"是否触发={sawAction}");
            Check("⑲ 0.20m 台阶：平滑踩上去（脚底到达 0.20m）",
                  peakFeet > 0.18f && peakFeet < 0.35f,
                  $"峰值脚底 {peakFeet:F3}m（台阶 0.20m）");
            Check("⑲ 0.20m 台阶：上台阶瞬间速度出现斜向上分量（只改方向）",
                  maxUpVel > 0.3f,
                  $"最大向上速度 {maxUpVel:F2} m/s（tan θ = b/a = 0.20/{cfg0.CapsuleRadius:F2} → θ≈26°）");
            Check("⑲ 0.20m 台阶：速度**大小**基本不变（无衰减）",
                  MathF.Abs(maxTotal - speedBefore) < 0.6f &&
                  minTotal > speedBefore * 0.75f,
                  $"上台阶前 {speedBefore:F2} m/s，途中 |v|∈[{minTotal:F2}, {maxTotal:F2}]");
        }

        // 20. ≤1m 小障碍：W 撞上去被挡住（不自动翻），W+Space 才翻越
        {
            var world = new AnalyticWorld(0f);
            world.AddBox(new F3(-6f, 0f, -6f), new F3(6f, 0.8f, -4f));   // 0.80m 小障碍
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 20);

            bool autoTraversed = false;
            for (int i = 0; i < 90; i++)
            {
                core.Step(In(mz: 1f), core.Config.FixedDelta);
                if (core.State is MovementState.Vault or MovementState.Mantle
                    or MovementState.Climb or MovementState.WallHang) autoTraversed = true;
            }
            Check("⑳ 0.80m 小障碍：只按 W 不会自动翻越（撞住）",
                  !autoTraversed && core.Position.Z > -4.0f,
                  $"state={core.State} z={core.Position.Z:F2}（障碍前脸 z=-4.0）");

            // 现在按 W + Space
            bool vaulted = false;
            float peakFeet = core.FeetPosition.Y;
            float speedAtVaultEnd = 0f;
            for (int i = 0; i < 90; i++)
            {
                core.Step(In(mz: 1f, jump: i == 0), core.Config.FixedDelta);
                if (core.State == MovementState.Vault) vaulted = true;
                peakFeet = MathF.Max(peakFeet, core.FeetPosition.Y);
                if (vaulted && core.State == MovementState.Ground)
                {
                    speedAtVaultEnd = core.HorizontalSpeed;
                    break;
                }
            }

            Check("⑳ 0.80m 小障碍：W + Space → 进入 Vault 状态",
                  vaulted,
                  $"state={core.State}");
            Check("⑳ 0.80m 小障碍：翻越后落到障碍上方（脚底 ≈ 0.80m）",
                  peakFeet > 0.70f && peakFeet < 1.10f,
                  $"峰值脚底 {peakFeet:F3}m（障碍 0.80m）");
            Check("⑳ 0.80m 小障碍：翻越结束保留大部分水平速度（VaultSpeedRetention）",
                  speedAtVaultEnd > cfg0.WalkSpeed * cfg0.VaultSpeedRetention * 0.8f,
                  $"翻完水平速度 {speedAtVaultEnd:F2} m/s（保留率 {cfg0.VaultSpeedRetention:F2}，"
                  + $"行走 {cfg0.WalkSpeed:F2}）");
        }

        // 21. >1m 墙：W 撞停，W+Space 不走 Vault（走抓墙）
        {
            var world = WallWorld(wallTop: 6.0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.0f, -1.0f));
            Settle(core, 40);

            bool everVault = false;
            for (int i = 0; i < 120; i++)
            {
                core.Step(In(mz: 1f), core.Config.FixedDelta);
                if (core.State == MovementState.Vault) everVault = true;
            }
            Check("㉑ 6m 高墙：只按 W 不会翻越（撞停）",
                  !everVault && core.Position.Z > -2.0f,
                  $"z={core.Position.Z:F2}（墙面 -2.0）");

            bool grabbed = JumpAndGrab(core, 6f);
            Check("㉑ 6m 高墙：W + Space → 走抓墙（Climb / WallHang），不是 Vault",
                  grabbed && core.State is MovementState.Climb or MovementState.WallHang,
                  $"state={core.State}");
        }

        // 22. 显式翻越 API（TryTraversal）仍保留：1.6m 台面 → Mantle/Level
        {
            var world = new AnalyticWorld(0f);
            world.AddBox(new F3(-6f, 0f, -6f), new F3(6f, 1.6f, -4f));
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 20);
            Run(core, 90, In(mz: 1f));      // 走到台面（4m 外，约需 60 tick）

            bool ok = core.TryTraversal(In(mz: 1f));
            Check("㉒ 显式 TryTraversal：1.6m 台面 → 进入 Mantle / Level 档",
                  ok && core.State == MovementState.Mantle && core.CurrentMantleClass == MantleClass.Level,
                  $"触发={ok} state={core.State} class={core.CurrentMantleClass} z={core.Position.Z:F2}");
        }

        // 23. 显式翻越超过上限（7.5m）→ 拒绝
        {
            var world = new AnalyticWorld(0f);
            world.AddBox(new F3(-6f, 0f, -6f), new F3(6f, 7.5f, -4f));
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1f, 0f));
            Settle(core, 20);
            Run(core, 90, In(mz: 1f));

            bool ok = core.TryTraversal(In(mz: 1f));
            Check("㉓ 显式 TryTraversal：7.5m > MantleMaxHeight(6.93) → 拒绝",
                  !ok && core.Position.Z < -3.4f,
                  $"触发={ok} z={core.Position.Z:F2}（已贴到台面 -3.59；上限 {cfg0.MantleMaxHeight:F2}m）");
        }

        // 24. 三档高度边界（纯配置断言，防止以后有人把档位改回去）
        {
            Check("㉔ 档位边界：OverwalkableHeight = 0.25（台阶档上限）",
                  MathF.Abs(cfg0.OverwalkableHeight - 0.25f) < 1e-4f,
                  $"OverwalkableHeight={cfg0.OverwalkableHeight:F4}");
            Check("㉔ 档位边界：VaultMaxHeight = 1.00（小障碍档上限）",
                  MathF.Abs(cfg0.VaultMaxHeight - 1.0f) < 1e-4f,
                  $"VaultMaxHeight={cfg0.VaultMaxHeight:F4}");
            Check("㉔ 档位顺序自洽：台阶 < 小障碍 < 墙",
                  cfg0.OverwalkableHeight < cfg0.VaultMaxHeight &&
                  cfg0.VaultMaxHeight < cfg0.AutoMantleMaxHeight,
                  $"{cfg0.OverwalkableHeight:F2} < {cfg0.VaultMaxHeight:F2} < {cfg0.AutoMantleMaxHeight:F2}");
        }

        // ═════════════ v0.3 遗留：tick 基准 / 探针开销 ═════════════

        {
            Check("㉕ tick 基准固定 60/s（配置）",
                  cfg0.TickRate == 60 && cfg0.MaxCatchUpTicks == 4,
                  $"TickRate={cfg0.TickRate} MaxCatchUpTicks={cfg0.MaxCatchUpTicks}");
            Check("㉕ FixedDelta = 1/60",
                  MathF.Abs(cfg0.FixedDelta - 1f / 60f) < 1e-7f,
                  $"FixedDelta={cfg0.FixedDelta:F6}");

            var clock = new ApexTickClock(1f / 60f, 4);
            clock.Accumulate(1f / 30f);
            int n = 0;
            while (clock.TryConsumeTick(out _)) n++;
            Check("㉕ 30fps 下每帧消费 2 个 tick（60 tick/s 不漂移）",
                  n == 2, $"1 帧 1/30s 消费 {n} tick（期望 2）");

            var clock2 = new ApexTickClock(1f / 60f, 4);
            int total = 0;
            for (int i = 0; i < 1440; i++)
            {
                clock2.Accumulate(1f / 144f);
                while (clock2.TryConsumeTick(out _)) total++;
            }
            Check("㉕ 144fps 下 10 秒整好 600 个 tick（无累积漂移）",
                  total is >= 599 and <= 600, $"1440 帧 × 1/144s → {total} tick（期望 599–600）");

            var clock3 = new ApexTickClock(1f / 60f, 4);
            clock3.Accumulate(1.0f);
            int consumed = 0;
            while (clock3.TryConsumeTick(out _)) consumed++;
            Check("㉕ 卡顿时最多补 4 tick（其余丢弃，有计数）",
                  consumed == 4 && clock3.DroppedTicks > 0,
                  $"消费 {consumed} tick，丢弃 {clock3.DroppedTicks}（上限 4）");
        }

        // 26. 探针抽样
        {
            // 挂墙期间"墙还在吗"探针：ClimbWallProbeInterval = 2 ⇒ 30 Hz
            var world = WallWorld(wallTop: 6f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.0f, -1.0f));
            Settle(core, 40);
            core.Step(In(mz: 1f, jump: true), core.Config.FixedDelta);
            Run(core, 10, In(mz: 1f));

            if (core.State == MovementState.Climb)
            {
                int before = core.ClimbWallProbeCount;
                Run(core, 60, In(mz: 1f));
                int probes = core.ClimbWallProbeCount - before;
                Check("㉖ 挂墙期间墙探针抽样间隔 2（60 tick ≈ 30 次）",
                      probes is >= 28 and <= 32,
                      $"实测 {probes} 次（期望 30 ± 2）");
            }
            else
            {
                Check("㉖ 挂墙期间墙探针抽样间隔 2（60 tick ≈ 30 次）", false,
                      $"前置失败：没进入 Climb（state={core.State}）");
            }

            // 附着探针：ClimbProbeInterval = 1 ⇒ 每 tick
            var core2 = MakeCore(WallWorld(6f));
            core2.Reset(new F3(3f, 10f, 3f));   // 离墙远，不会真挂上
            int before2 = core2.ClimbProbeCount;
            Run(core2, 40, In());
            int climbProbes = core2.ClimbProbeCount - before2;
            Check("㉖ 附着探针每 tick 都跑（间隔 1）",
                  climbProbes == 40, $"实测 {climbProbes} 次（期望 40）");
        }

        // 27. v0.4 移除自动翻越探测：地面上不再有每 tick 的翻越射线
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.0f, 0f));
            Settle(core, 40);
            int before = core.TraversalProbeCount;
            Run(core, 60, In(mz: 1f));
            int probes = core.TraversalProbeCount - before;
            Check("㉗ 自动翻越探测已移除（地面 60 tick 探针数 = 0）",
                  probes == 0,
                  $"实测 {probes} 次（v0.3 是 30 次；v0.4 改为只在 W+Space 时显式触发）");
        }

        sb.AppendLine();
        sb.AppendLine($"  合计 {checks} 项断言，失败 {failures} 项");
        return (failures == 0, sb.ToString());
    }
}
