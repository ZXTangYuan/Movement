using System;
using System.Text;
using ApexMovement.Core;

namespace ApexMovement.Tests;

/// <summary>
/// v0.3 完整版的最后两块逆向机制验收：
///
/// ① **打滑（Slip）** —— 逆向里的 <c>C_TriggerSlipSphere</c> / <c>m_slipSpeed</c> /
///    <c>m_slipAcceleration</c> / <c>m_slipAirRestrictTime</c> /
///    <c>m_slipAirRestrictDirection</c>。
///    它是**世界体积推挤**，不是地面材质；离开滑区后会锁一小会儿空中方向。
/// ② **扒墙 / 悬垂（WallHang）** —— 逆向里的 <c>m_wallHanging</c> /
///    <c>m_wallHangTime</c> / <c>m_wallDangleClimbProgressFloor</c> /
///    <c>automantle_disable_hang</c> / <c>ACT_MP_WALLHANG_*</c>。
///    它与 <see cref="MovementState.Hang"/>（攀爬到顶的过渡态）不是一回事。
///
/// 跑在 <see cref="AnalyticWorld"/> 上：无头、无引擎、逐 tick 确定性。
/// </summary>
public static class SlipHangTest
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

    /// <summary>
    /// 竖直高墙：地面 y=0，墙 z ∈ [-4, -2]（朝玩家那面 z=-2，法线 +Z）。
    /// 玩家 yaw=0（前向 -Z）即"面向墙"。
    /// </summary>
    private static AnalyticWorld WallWorld(float wallTop, float groundY = 0f)
    {
        var w = new AnalyticWorld(groundY);
        w.AddBox(new F3(-5f, groundY, -4f), new F3(5f, groundY + wallTop, -2f));
        return w;
    }

    /// <summary>
    /// 空中贴墙（y=5、离墙面 1m）+ 按住蹲 → 挂上。
    /// 返回挂住后的 core，便于后续断言。
    /// </summary>
    private static ApexMovementCore AttachHang(AnalyticWorld world)
    {
        var core = MakeCore(world);
        core.Reset(new F3(0f, 5f, -1f));
        Run(core, 4, In(crouch: true));
        return core;
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

        // ═════════════════════════ 打滑（Slip） ═════════════════════════

        // ── 1. 进入滑区 → 状态变 Slip，速度被拉到 slipSpeed ──
        {
            // 半径取 40m：8 m/s 滑 0.67s 只走 5.3m，保证整段都在体积内，
            // 否则玩家会滑出去、被地面摩擦停下，测到的就不是"打滑加速"了。
            var world = new AnalyticWorld(0f);
            world.AddSlipSphere(new F3(0f, 0.5f, 0f), radius: 40f,
                                speed: 8f, accel: 40f, direction: new F3(1f, 0f, 0f));

            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.2f, 0f));
            Run(core, 40, In());          // 落地 + 加速

            Check("站进打滑球 → 状态变 Slip",
                  core.State == MovementState.Slip,
                  $"state={core.State}");

            Check("打滑把水平速度拉到 slipSpeed（8 m/s）",
                  MathF.Abs(core.HorizontalSpeed - 8f) < 0.6f,
                  $"speed={core.HorizontalSpeed:F2}（目标 8.00）");

            Check("打滑方向 = 体积方向（+X）",
                  core.Snapshot.Velocity.X > 7f && MathF.Abs(core.Snapshot.Velocity.Z) < 0.5f,
                  $"vel=({core.Snapshot.Velocity.X:F2}, {core.Snapshot.Velocity.Z:F2})");

            Check("HUD 快照标记 Slipping + 方向",
                  core.Snapshot.Slipping &&
                  core.Snapshot.SlipDirection.X > 0.9f,
                  $"Slipping={core.Snapshot.Slipping} dir=({core.Snapshot.SlipDirection.X:F2})");

            // ★ 回归：LockToPlane 曾把垂直速度整体归零，导致空中进滑区后永远落不下去
            //   （HUD 上 state=Slip 但 ground=False，角色悬在离地 0.22m 处）。
            Check("贴地滑道上仍然踩在地上（ground=True，不悬空）",
                  core.OnGround && core.FeetPosition.Y < 0.05f,
                  $"OnGround={core.OnGround} feet={core.FeetPosition.Y:F3}");
        }

        // ── 2. 体积没配方向 → 从球心往外推（m_defaultSlipDirection 语义） ──
        {
            var world = new AnalyticWorld(0f);
            // 球心在玩家左边 1.5m：往外推就是 +X
            world.AddSlipSphere(new F3(-1.5f, 0.5f, 0f), radius: 40f, speed: 6f, accel: 60f);

            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.2f, 0f));
            Run(core, 40, In());

            Check("没配方向的打滑球：从球心往外推（+X）",
                  core.State == MovementState.Slip && core.Snapshot.Velocity.X > 2f,
                  $"state={core.State} vx={core.Snapshot.Velocity.X:F2}");
        }

        // ── 3. 离开滑区 → 记录方向限制（m_slipAirRestrictTime） ──
        {
            var world = new AnalyticWorld(0f);
            // 小球：加速到 8 m/s 后很快滑出去
            world.AddSlipSphere(new F3(-1.0f, 0.5f, 0f), radius: 1.6f,
                                speed: 8f, accel: 60f, direction: new F3(1f, 0f, 0f));

            var core = MakeCore(world);
            core.Reset(new F3(-1.0f, 1.2f, 0f));

            // 先跑几 tick 确认确实进过 Slip（半径小，几十 tick 内就会滑出去）
            bool sawSlip = false;
            for (int i = 0; i < 120; i++)
            {
                core.Step(In(), core.Config.FixedDelta);
                if (core.State == MovementState.Slip) sawSlip = true;
                if (sawSlip && core.State != MovementState.Slip) break;
            }

            Check("站进小打滑球 → 进过 Slip 并滑出去",
                  sawSlip && core.State != MovementState.Slip,
                  $"曾 Slip={sawSlip}，现在 state={core.State}");

            // 滑出去的那一刻会置 0.6s 的方向限制；跑满 1s 后应已衰减完
            Run(core, 60, In());
            Check("方向限制计时器会自行衰减到 0",
                  !core.IsSlipAirRestricted,
                  $"IsSlipAirRestricted={core.IsSlipAirRestricted} " +
                  $"remaining={core.Snapshot.SlipAirRestrictRemaining:F3}");
        }

        // ── 4. 圆锥方向限制（ClampToCone 纯数学） ──
        {
            const float cone = 35f;
            float cosCone = MathF.Cos(cone * MathF.PI / 180f);

            // ① 已在圆锥内 → 原样返回
            F3 inside = ApexMovementCore.ClampToCone(new F3(1f, 0f, 0f), new F3(1f, 0f, 0f), cone);
            Check("圆锥：方向已在圆锥内 → 原样返回",
                  MathF.Abs(inside.X - 1f) < 1e-4f && MathF.Abs(inside.Z) < 1e-4f,
                  $"结果=({inside.X:F3}, {inside.Z:F3})");

            // ② 垂直轴的方向（90° 外）→ 被压到 35° 边缘
            F3 perp = ApexMovementCore.ClampToCone(new F3(0f, 0f, -1f), new F3(1f, 0f, 0f), cone);
            float dot = F3.Dot(perp, new F3(1f, 0f, 0f));
            if (dot > 1f) dot = 1f;
            if (dot < -1f) dot = -1f;
            float angle = MathF.Acos(dot);
            Check("圆锥：90° 外的方向被压到 35° 边缘",
                  MathF.Abs(angle - cone * MathF.PI / 180f) < 1e-3f &&
                  perp.Z < -0.5f,                       // 仍保留原方向的垂直分量
                  $"与轴夹角={angle * 180f / MathF.PI:F2}°（期望 {cone}°），" +
                  $"结果=({perp.X:F3}, {perp.Z:F3})");

            // ③ 完全反向 → 返回轴本身（无垂直分量可修正）
            F3 back = ApexMovementCore.ClampToCone(new F3(-1f, 0f, 0f), new F3(1f, 0f, 0f), cone);
            Check("圆锥：完全反向 → 退回轴本身",
                  MathF.Abs(back.X - 1f) < 1e-4f,
                  $"结果=({back.X:F3}, {back.Z:F3})");

            Check("圆锥半角配置在合理区间（SlipAirRestrictConeDeg）",
                  cfg0.SlipAirRestrictConeDeg > 0f && cfg0.SlipAirRestrictConeDeg < 90f,
                  $"SlipAirRestrictConeDeg={cfg0.SlipAirRestrictConeDeg}（cos={cosCone:F3}）");
        }

        // ── 5. 主动行为优先：滑区里蹲下 → 转成玩家可控的滑铲 ──
        {
            var world = new AnalyticWorld(0f);
            world.AddSlipSphere(new F3(0f, 0.5f, 0f), radius: 8f,
                                speed: 9f, accel: 80f, direction: new F3(1f, 0f, 0f));

            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.2f, 0f));
            Run(core, 40, In());          // 落地并被打滑加速
            Run(core, 10, In(crouch: true));

            Check("滑区里蹲下 → 优先转成 Slide（不被环境抢状态）",
                  core.State == MovementState.Slide,
                  $"state={core.State} speed={core.HorizontalSpeed:F2}");
        }

        // ── 6. 滑区里能跳出去 ──
        {
            var world = new AnalyticWorld(0f);
            world.AddSlipSphere(new F3(0f, 0.5f, 0f), radius: 8f,
                                speed: 9f, accel: 80f, direction: new F3(1f, 0f, 0f));

            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.2f, 0f));
            Run(core, 40, In());

            var jump = new MovementInput { JumpPressed = true, JumpHeld = true };
            core.Step(jump, core.Config.FixedDelta);

            Check("滑区里按跳 → 立刻离地（Air，且有向上速度）",
                  core.State == MovementState.Air && core.VerticalSpeed > 1f,
                  $"state={core.State} vy={core.VerticalSpeed:F2}");
        }

        // ── 7. 打滑探针降到 30/s ──
        {
            Check("打滑探针抽样间隔为 2（60Hz → 30Hz）",
                  cfg0.SlipProbeInterval == 2,
                  $"SlipProbeInterval={cfg0.SlipProbeInterval}");

            var world = new AnalyticWorld(0f);
            world.AddSlipSphere(new F3(0f, 0.5f, 0f), radius: 8f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.2f, 0f));
            Run(core, 40, In());

            int before = core.SlipProbeCount;
            Run(core, 60, In());
            int probes = core.SlipProbeCount - before;

            Check("60 个 tick 只采样约 30 次打滑体积",
                  probes >= 28 && probes <= 32,
                  $"实测 {probes} 次（期望 30 ± 2）");
        }

        // ═══════════════════════ 扒墙 / 悬垂 ═══════════════════════

        // ── 8. 非强制：空中贴墙但不按蹲 → 不挂 ──
        {
            var world = WallWorld(wallTop: 20f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 5f, -1f));
            Run(core, 40, In());          // 什么都不按

            Check("空中贴墙但不按蹲 → 不会自动挂住（非强制）",
                  core.State != MovementState.WallHang,
                  $"state={core.State}");
        }

        // ── 9. 按蹲 + 贴墙 → 挂住，速度归零 ──
        {
            var world = WallWorld(wallTop: 20f);
            var core = AttachHang(world);

            Check("空中贴墙 + 按住蹲 → 进入 WallHang",
                  core.State == MovementState.WallHang,
                  $"state={core.State}");

            Check("挂住时速度恒为 0（speed=0.00 / vy=0.00）",
                  core.HorizontalSpeed < 0.01f && MathF.Abs(core.VerticalSpeed) < 0.01f,
                  $"speed={core.HorizontalSpeed:F2} vy={core.VerticalSpeed:F2}");

            Check("挂住时贴到墙面上（离墙面 ≈ 半径 + 皮肤）",
                  MathF.Abs(core.Position.Z - (-2f + 0.4064f + 0.02f)) < 0.06f,
                  $"z={core.Position.Z:F3}（期望 ≈ -1.574）");

            Check("HUD 快照标记 WallHanging + 握力满",
                  core.Snapshot.WallHanging && core.Snapshot.WallHangGrip > 0.95f,
                  $"WallHanging={core.Snapshot.WallHanging} grip={core.Snapshot.WallHangGrip:F2}");
        }

        // ── 10. 松开蹲键 → 松手回空中 ──
        {
            var world = WallWorld(wallTop: 20f);
            var core = AttachHang(world);
            Run(core, 3, In());           // 松开蹲

            Check("松开蹲键 → 立刻松手（回 Air）",
                  core.State == MovementState.Air,
                  $"state={core.State}");
        }

        // ── 11. 按 W 沿墙上爬 ──
        {
            var world = WallWorld(wallTop: 20f);
            var core = AttachHang(world);
            float y0 = core.Position.Y;
            Run(core, 60, In(mz: 1f, crouch: true));
            float y1 = core.Position.Y;

            Check("挂住时按 W → 沿墙上爬（≈1 m/s）",
                  core.State == MovementState.WallHang && y1 - y0 > 0.8f && y1 - y0 < 1.3f,
                  $"Δy={y1 - y0:F2}（期望 ≈ 1.00，state={core.State}）");
        }

        // ── 12. 按 A/D 沿墙横移 ──
        {
            var world = WallWorld(wallTop: 20f);
            var core = AttachHang(world);
            float x0 = core.Position.X;
            Run(core, 60, In(mx: 1f, crouch: true));
            float x1 = core.Position.X;

            Check("挂住时按 D → 沿墙横移（ACT_MP_WALLHANG_RIGHT）",
                  core.State == MovementState.WallHang && x1 - x0 > 0.6f,
                  $"Δx={x1 - x0:F2}（期望 ≈ 0.80，state={core.State}）");
        }

        // ── 13. 不按 W 会缓慢下滑，滑过握力下限就脱手 ──
        {
            var world = WallWorld(wallTop: 20f);
            var core = AttachHang(world);
            float y0 = core.Position.Y;
            Run(core, 60, In(crouch: true));    // 1s 不动
            float y1 = core.Position.Y;

            Check("挂住不动 → 缓慢下滑（sound_wallHangSlip）",
                  core.State == MovementState.WallHang && y0 - y1 > 0.15f && y0 - y1 < 0.35f,
                  $"Δy={y0 - y1:F2}（期望 ≈ 0.22）");

            Check("下滑期间握力 < 1",
                  core.Snapshot.WallHangGrip < 0.9f,
                  $"grip={core.Snapshot.WallHangGrip:F2}");

            // 继续挂到握力耗尽（1.2m / 0.22 ≈ 5.45s）
            Run(core, 340, In(crouch: true));
            Check("握力耗尽 → 自动脱手",
                  core.State != MovementState.WallHang,
                  $"state={core.State}");
        }

        // ── 14. 按 W 能把手再抓高一点（回补握力） ──
        {
            var world = WallWorld(wallTop: 20f);
            var core = AttachHang(world);
            Run(core, 60, In(crouch: true));       // 先滑一会儿，掉握力
            float gripLow = core.Snapshot.WallHangGrip;
            Run(core, 30, In(mz: 1f, crouch: true));  // 再按 W 上爬

            Check("按 W 上爬会回补握力（m_wallDangleClimbProgressFloor）",
                  core.Snapshot.WallHangGrip > gripLow,
                  $"grip {gripLow:F2} → {core.Snapshot.WallHangGrip:F2}");
        }

        // ── 15. 挂太久 → 超时脱手（m_wallHangTime） ──
        {
            var world = WallWorld(wallTop: 40f);
            var core = AttachHang(world);

            // 一直按 W（握力不掉、也爬不到顶），挂到 6s 上限
            Run(core, 60 * 7, In(mz: 1f, crouch: true));

            Check("挂超过 WallHangMaxTime(6s) → 自动脱手",
                  core.State != MovementState.WallHang,
                  $"state={core.State}（上限 {cfg0.WallHangMaxTime:F1}s）");
        }

        // ── 16. Space → 蹬墙跳（离墙 + 向上） ──
        {
            var world = WallWorld(wallTop: 40f);   // 高墙：翻不上去，只能蹬开
            var core = AttachHang(world);

            var jump = new MovementInput { JumpPressed = true, JumpHeld = true, CrouchHeld = true };
            core.Step(jump, core.Config.FixedDelta);

            Check("挂住时按跳 → 蹬墙跳（离墙 + 向上）",
                  core.State == MovementState.Air &&
                  core.Snapshot.Velocity.Z > 0.5f &&
                  core.VerticalSpeed > 1f,
                  $"state={core.State} vz={core.Snapshot.Velocity.Z:F2} vy={core.VerticalSpeed:F2}");
        }

        // ── 17. 视角甩开太大 → 松手 ──
        {
            var world = WallWorld(wallTop: 20f);
            var core = AttachHang(world);

            // 转 90°（前向从 -Z 变成 +X）：facing 从 1 掉到 0 < cos(45.57°)
            var turn = new MovementInput { CrouchHeld = true, MouseDeltaYaw = MathF.PI / 2f };
            core.Step(turn, core.Config.FixedDelta);
            Run(core, 2, In(crouch: true));

            Check("挂住时把视角甩开 90° → 松手",
                  core.State != MovementState.WallHang,
                  $"state={core.State}");
        }

        // ── 18. automantle_disable_hang：能自动翻越的矮墙不挂 ──
        {
            // 墙顶 1.5m < AutoMantleMaxHeight(2.032)：上方探针打空 ⇒ 不挂
            var world = WallWorld(wallTop: 1.5f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 1.0f, -1f));
            Run(core, 30, In(crouch: true));

            Check("矮墙（能自动翻越）不会被挂住（automantle_disable_hang）",
                  core.State != MovementState.WallHang,
                  $"state={core.State}（墙顶 1.5 < AutoMantleMaxHeight {cfg0.AutoMantleMaxHeight:F2}）");
        }

        // ── 19. 离墙太远 → 挂不上 ──
        {
            var world = WallWorld(wallTop: 20f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 5f, 0.5f));   // 离墙面 2.5m > 半径 + reach
            Run(core, 20, In(crouch: true));

            Check("离墙超过 WallHangReach → 挂不上",
                  core.State != MovementState.WallHang,
                  $"state={core.State}");
        }

        // ── 20. 探针开销：扒墙探针挂在便宜的 ClimbProbeInterval 上 ──
        {
            var world = WallWorld(wallTop: 20f);
            var core = MakeCore(world);
            core.Reset(new F3(0f, 30f, -1f));   // 够高，40 tick 内不会落地
            int before = core.WallHangProbeCount;
            Run(core, 40, In());
            int probes = core.WallHangProbeCount - before;

            Check("扒墙探针每 tick 都跑（不按蹲时提前返回，无射线开销）",
                  probes == 40,
                  $"实测 {probes} 次（期望 40）");
        }

        sb.AppendLine();
        sb.AppendLine($"  合计 {checks} 项断言，失败 {failures} 项");
        return (failures == 0, sb.ToString());
    }
}
