using System;
using System.Text;
using ApexMovement.Camera;
using ApexMovement.Core;

namespace ApexMovement.Tests;

/// <summary>
/// 相机子系统的**纯数学**自检（不依赖 Godot、不需要渲染）。
///
/// 覆盖：FOV 调节与插值空间、弹簧视角踢（落地分档）、滑铲/冲刺侧倾、
/// 翻越相机脱开与高度上限、走路摇晃、以及"帧率无关的收敛性"。
/// </summary>
public static class CameraTest
{
    private static MovementSnapshot Snap(
        float speed = 0f, bool onGround = true, bool sprinting = false, bool sliding = false,
        float crouch = 0f, float height = 1.95f, float bobPhase = 0f, int tick = 0,
        MovementState state = MovementState.Ground, float traversalProgress = -1f,
        LandingTier landing = LandingTier.None, F3? pos = null, GaitSample gait = default)
    {
        return new MovementSnapshot
        {
            Position = pos ?? new F3(0f, 1f, 0f),
            Up = F3.Up,
            Yaw = 0f,
            Pitch = 0f,
            CurrentHeight = height,
            CrouchAmount = crouch,
            FeetOffsetFromCenter = height * 0.5f,
            EyeOffsetFromFeet = height - 0.10f,
            HorizontalSpeed = speed,
            OnGround = onGround,
            Sprinting = sprinting,
            Sliding = sliding,
            Crouching = crouch > 0.5f,
            State = state,
            TraversalProgress = traversalProgress,
            LandingTier = landing,
            BobPhase = bobPhase,
            TickCount = tick,
            Gait = gait,
        };
    }

    /// <summary>按真实步态模型造一个"以 speed 走路/奔跑"的快照（v0.4：相机只读步态）。</summary>
    private static MovementSnapshot GaitSnap(float speed, float bobPhase, float crouch = 0f,
                                             MovementState state = MovementState.Ground)
    {
        var gait = GaitModel.Evaluate(speed, 1.95f, 6.604f, crouch, GaitParams.Default);
        return Snap(speed: speed, bobPhase: bobPhase, crouch: crouch, state: state, gait: gait);
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

        const float dt = 1f / 60f;

        // ═════════ 1. 默认 FOV ═════════
        {
            var rig = new ApexCameraRig();
            var o = rig.Update(Snap(), dt);
            Near("默认 FOV", o.Fov, 90f, 0.5f);
        }

        // ═════════ 2. 冲刺抬 FOV ═════════
        {
            var rig = new ApexCameraRig();
            rig.Update(Snap(), dt);
            CameraOutput o = default;
            for (int i = 0; i < 90; i++)
                o = rig.Update(Snap(speed: 7.6f, sprinting: true), dt);
            Check("冲刺抬 FOV", o.Fov > 90.5f && o.Fov < 96f, $"fov={o.Fov:F2}（基础 90 + 冲刺 3）");
        }

        // ═════════ 3. 滑铲不抬 FOV（v0.4：滑铲镜头完全等同下蹲）═════════
        {
            var rig = new ApexCameraRig();
            rig.Update(Snap(), dt);
            CameraOutput o = default;
            for (int i = 0; i < 90; i++)
                o = rig.Update(Snap(speed: 10f, sliding: true, crouch: 1f, height: 0.975f,
                                    state: MovementState.Slide), dt);
            Check("滑铲不抬 FOV（与下蹲一致）", MathF.Abs(o.Fov - 90f) < 0.2f,
                  $"fov={o.Fov:F2}（基础 90；v0.4 已移除滑铲加宽）");
        }

        // ═════════ 4. FOV 被夹在 [FovMin, FovMax] ═════════
        {
            var cfg = new CameraEffectsConfig { FovBase = 118f, FovSprintAdd = 20f, FovSlideAdd = 20f };
            var rig = new ApexCameraRig(cfg);
            CameraOutput o = default;
            for (int i = 0; i < 200; i++)
                o = rig.Update(Snap(speed: 10f, sliding: true, sprinting: true), dt);
            Check("FOV 上限夹紧", o.Fov <= cfg.FovMax + 1e-3f, $"fov={o.Fov:F2}，上限 {cfg.FovMax}");
        }

        // ═════════ 5. 玩家可调 FOV ═════════
        {
            var rig = new ApexCameraRig();
            rig.Update(Snap(), dt);
            bool ok1 = rig.AdjustBaseFov(4f);
            CameraOutput o = default;
            for (int i = 0; i < 120; i++) o = rig.Update(Snap(), dt);
            Check("玩家可调 FOV（cl_fovScale 语义）", ok1 && MathF.Abs(o.Fov - 94f) < 0.5f,
                  $"调 +4° 后 fov={o.Fov:F2}");

            bool clamped = rig.AdjustBaseFov(200f);
            Check("FOV 调节夹到上限", !clamped || rig.Config.FovBase <= rig.Config.FovMax,
                  $"FovBase={rig.Config.FovBase:F1}，上限 {rig.Config.FovMax}");
        }

        // ═════════ 6. tan 空间插值 vs 线性插值（中间值不同，终值相同）═════════
        {
            // 用较大的 FOV 变化量（90 → 115）才看得出 tan 空间与线性空间的差别。
            // 先把 sprintBlend 预热到饱和（FovSmoothing=0 让 FOV 不动），再打开平滑做对比。
            var cfgT = new CameraEffectsConfig { FovSpace = FovInterpSpace.Tangent, FovSmoothing = 0f, FovSprintAdd = 25f };
            var cfgL = new CameraEffectsConfig { FovSpace = FovInterpSpace.Linear, FovSmoothing = 0f, FovSprintAdd = 25f };
            var rigT = new ApexCameraRig(cfgT);
            var rigL = new ApexCameraRig(cfgL);
            rigT.Update(Snap(), dt); rigL.Update(Snap(), dt);

            var sprint = Snap(speed: 10f, sprinting: true);
            for (int i = 0; i < 120; i++) { rigT.Update(sprint, dt); rigL.Update(sprint, dt); }

            cfgT.FovSmoothing = 6f;
            cfgL.FovSmoothing = 6f;

            CameraOutput oT = default, oL = default;
            for (int i = 0; i < 4; i++) { oT = rigT.Update(sprint, dt); oL = rigL.Update(sprint, dt); }
            float diffMid = MathF.Abs(oT.Fov - oL.Fov);

            for (int i = 0; i < 400; i++) { oT = rigT.Update(sprint, dt); oL = rigL.Update(sprint, dt); }
            float diffEnd = MathF.Abs(oT.Fov - oL.Fov);

            Check("tan 空间插值与线性不同但收敛一致",
                  diffMid > 0.3f && diffEnd < 0.05f,
                  $"中途差 {diffMid:F3}°，终值差 {diffEnd:F4}°（目标 115°）");
        }

        // ═════════ 7. 落地视角踢（弹簧）═════════
        {
            var rig = new ApexCameraRig();
            rig.Update(Snap(), dt);
            var land = Snap(landing: LandingTier.Hard, tick: 10);
            CameraOutput o = rig.Update(land, dt);
            float firstPitch = o.PitchOffset;
            // 落地瞬间镜头向下（负俯仰）
            Check("落地视角踢（瞬间低头）", firstPitch < -0.01f, $"pitchOffset={firstPitch:F4} rad");

            // 弹簧应把它拉回 0
            //
            // v0.3：这里的下界原来写死 0.02 rad（对应 v0.2 的 10° 级砸镜头），
            // 而 v0.3 按需求把幅度压到 2.5° 级，所以下界必须跟着改 —— 否则这条断言
            // 会变成"必须晃动得足够剧烈才算通过"，与需求本身冲突。
            // 真正防"踢量悄悄归零"的守卫放到下面第 7b 条（带上界，方向明确）。
            float peak = MathF.Abs(firstPitch);
            for (int i = 0; i < 90; i++) o = rig.Update(Snap(tick: 11 + i), dt);
            Check("视角踢自动回正", MathF.Abs(o.PitchOffset) < 0.005f && peak > 0.005f,
                  $"峰值 {peak:F4} → 1.5s 后 {o.PitchOffset:F4} rad");

            // ── 7b. v0.3 幅度守卫：落地砸镜头必须"轻"，而且仍确实存在 ──
            {
                float PeakAbs(LandingTier tier)
                {
                    var r = new ApexCameraRig();
                    r.Update(Snap(), dt);
                    float pk = 0f;
                    for (int i = 0; i < 60; i++)
                    {
                        var s = i == 0 ? Snap(landing: tier, tick: 1) : Snap(tick: 2 + i);
                        var oo = r.Update(s, dt);
                        pk = MathF.Max(pk, MathF.Abs(oo.PitchOffset));
                    }
                    return pk;
                }

                const float Upper = 0.0873f;   // 5°
                const float Lower = 0.0175f;   // 1°
                float hardPeak = PeakAbs(LandingTier.Hard);
                float lowPeak = PeakAbs(LandingTier.Low);

                Check("v0.3 落地砸镜头收敛到 5° 以内",
                      hardPeak <= Upper && lowPeak <= Upper,
                      $"Hard={hardPeak * 180f / MathF.PI:F2}° / Low={lowPeak * 180f / MathF.PI:F2}°（上限 5.00°）");

                Check("v0.3 落地砸镜头未被压没（Hard 仍在 1°–5°）",
                      hardPeak >= Lower && hardPeak <= Upper,
                      $"Hard={hardPeak * 180f / MathF.PI:F2}°（要求 1.00°–5.00°）");

                Check("v0.3 MaxKickAngle 只作保险丝（未成为工作点）",
                      hardPeak < rig.Config.MaxKickAngle * 0.9f,
                      $"Hard 峰值 {hardPeak:F4} < 上限 {rig.Config.MaxKickAngle:F4} × 0.9");
            }
        }

        // ═════════ 8. 落地踢幅度按档位单调 ═════════
        {
            float PeakFor(LandingTier tier)
            {
                var rig = new ApexCameraRig();
                rig.Update(Snap(), dt);
                float peak = 0f;
                for (int i = 0; i < 30; i++)
                {
                    var s = i == 0 ? Snap(landing: tier, tick: 1) : Snap(tick: 2 + i);
                    var o = rig.Update(s, dt);
                    peak = MathF.Max(peak, MathF.Abs(o.PitchOffset));
                }
                return peak;
            }
            float p1 = PeakFor(LandingTier.Low);
            float p2 = PeakFor(LandingTier.Medium);
            float p3 = PeakFor(LandingTier.High);
            float p4 = PeakFor(LandingTier.Hard);
            Check("落地踢按档位单调增强", p1 < p2 && p2 < p3 && p3 < p4,
                  $"Low={p1:F4} < Medium={p2:F4} < High={p3:F4} < Hard={p4:F4}");
        }

        // ═════════ 9. 弹簧不发散 ═════════
        {
            var rig = new ApexCameraRig();
            rig.Update(Snap(), dt);
            rig.AddKick(-50f, 50f, 50f);   // 极端冲量
            float maxAbs = 0f;
            for (int i = 0; i < 300; i++)
            {
                var o = rig.Update(Snap(tick: i), dt);
                maxAbs = MathF.Max(maxAbs, MathF.Abs(o.PitchOffset));
                maxAbs = MathF.Max(maxAbs, MathF.Abs(o.YawOffset));
            }
            Check("极端冲量下弹簧不发散", maxAbs <= rig.Config.MaxKickAngle + 1e-3f,
                  $"最大偏移 {maxAbs:F4} ≤ 上限 {rig.Config.MaxKickAngle:F4} rad");
        }

        // ═════════ 10. 滑铲侧倾随速度增大 ═════════
        {
            float RollAt(float speed)
            {
                var rig = new ApexCameraRig();
                rig.Update(Snap(), dt);
                CameraOutput o = default;
                for (int i = 0; i < 60; i++)
                    o = rig.Update(Snap(speed: speed, sliding: true, crouch: 1f, height: 0.975f,
                                        state: MovementState.Slide), dt);
                return o.Roll * 180f / MathF.PI;
            }
            float slow = RollAt(5f);
            float fast = RollAt(10f);
            Check("滑铲不侧倾（与下蹲一致）",
                  MathF.Abs(slow) < 0.05f && MathF.Abs(fast) < 0.05f,
                  $"5m/s→{slow:F2}°，10m/s→{fast:F2}°（v0.4 已移除滑铲侧倾）");
        }

        // ═════════ 11. 冲刺侧倾 ═════════
        //
        // v0.4.4 需求（m00818）：「奔跑状态下镜头会以当前面向的射线为轴，逆时针
        // 转一点点角度（1.5° 左右），我希望**不要转角度**」。滚转因此默认关闭，
        // 这里拆成两条：默认必须为 0（需求），打开开关后通道本身仍然可用（不回退能力）。
        {
            float SprintRollDeg(bool enabled)
            {
                var rig = new ApexCameraRig();
                rig.Config.SprintRollEnabled = enabled;
                rig.Update(Snap(), dt);
                CameraOutput o = default;
                for (int i = 0; i < 120; i++) o = rig.Update(Snap(speed: 7.6f, sprinting: true), dt);
                return o.Roll * 180f / MathF.PI;
            }
            float off = SprintRollDeg(false);
            float on = SprintRollDeg(true);
            Check("⑪a 默认无滚转：奔跑时画面绕视线轴不转（需求 m00818）",
                  MathF.Abs(off) < 1e-4f,
                  $"关闭时 roll={off:F4}°（要求 0）");
            Check("⑪b 打开 SprintRollEnabled 后冲刺侧倾仍可用（sprinttiltMaxRoll 通道未坏）",
                  on > 0.5f && on < 3f,
                  $"打开时 roll={on:F2}°（配置上限 {new CameraEffectsConfig().SprintMaxRollDeg:F2}°）");
        }

        // ═════════ 11b. ∞ 载体：只有「视线方向」一种（v0.5 定稿，需求 m01070）═════════
        //
        // 需求 m01070：「镜头方面就用新的 ∞ 载体开关的这个方式……原先的那种可以删了」。
        // 所以平移模式已删除：**EyeOffset 不承载 ∞，YawOffset 反过来承载**。
        {
            float LatRange(out float yawRange)
            {
                var rig = new ApexCameraRig();
                rig.Update(Snap(), dt);
                float minLat = float.MaxValue, maxLat = float.MinValue;
                float minYaw = float.MaxValue, maxYaw = float.MinValue;
                for (int i = 0; i < 400; i++)
                {
                    // 必须用 GaitSnap：bob 的幅度来自步态样本，普通 Snap() 的
                    // Gait 是 default（全 0）⇒ 波形恒为 0，测不出东西。
                    CameraOutput o = rig.Update(
                        GaitSnap(6.6f, bobPhase: i * 0.05f), dt);
                    float lat = o.EyeOffset.X;   // yaw=0 时 right = +X
                    minLat = MathF.Min(minLat, lat); maxLat = MathF.Max(maxLat, lat);
                    minYaw = MathF.Min(minYaw, o.YawOffset); maxYaw = MathF.Max(maxYaw, o.YawOffset);
                }
                yawRange = (maxYaw - minYaw) * 180f / MathF.PI;
                return maxLat - minLat;
            }
            float lat = LatRange(out float yawRange);
            Check("⑪c ∞ 只由视线方向承载：眼位不横移、yaw 摆幅 ≈ 2·atan(A/参考距离)",
                  MathF.Abs(lat) < 1e-4f && yawRange > 0.05f,
                  $"EyeOffset 横向摆幅 {lat * 1000f:F4} mm，YawOffset 摆幅 {yawRange:F3}°");
        }

        // ═════════ 12. 走路摇晃：静止无 bob，移动有 bob（幅度来自步态模型）═════════
        {
            var rig = new ApexCameraRig();
            rig.Update(Snap(), dt);
            CameraOutput idle = default;
            for (int i = 0; i < 60; i++) idle = rig.Update(Snap(speed: 0f), dt);

            var rig2 = new ApexCameraRig();
            rig2.Update(Snap(), dt);
            // v0.5：bob 不再进 EyeOffset，读相机输出的物理半幅（米）。
            float maxBob = 0f;
            for (int i = 0; i < 60; i++)
            {
                var o = rig2.Update(GaitSnap(5.4f, i * 0.4f), dt);
                maxBob = MathF.Max(maxBob, MathF.Abs(o.BobVerticalMeters));
            }
            Check("走路摇晃（静止无 / 移动有）",
                  MathF.Abs(idle.BobVerticalMeters) < 1e-4f && maxBob > 0.002f,
                  $"静止 bob={idle.BobVerticalMeters:E2}，移动最大 bob={maxBob:F4} m");

            // 舒适档上限：v0.4.2 把判据从「峰到峰 ≤ 2.5 cm」收紧到「峰到峰 ≤ 1.5 cm」。
            // 5.4 m/s 时步态模型给出半幅 3.79 mm（峰到峰 7.59 mm），远在新上限内。
            // 这里用 2×maxBob 作为峰到峰（maxBob 是相位扫过整周后的半幅峰值）。
            float bobP2P = 2f * maxBob;
            Check("摇晃幅度落在 v0.4.2 舒适档（垂直峰到峰 ≤1.5 cm）", bobP2P <= 0.015f,
                  $"峰到峰 = 2×{maxBob * 1000f:F2}mm = {bobP2P * 1000f:F2}mm（上限 15mm）");
        }

        // ═════════ 13. 空中无 bob（步态样本为零）═════════
        {
            var rig = new ApexCameraRig();
            rig.Update(Snap(), dt);
            float maxBob = 0f;
            for (int i = 0; i < 60; i++)
            {
                // 空中：核心不给步态（Gait=default）⇒ 相机不摇
                var o = rig.Update(Snap(speed: 5.4f, onGround: false, bobPhase: i * 0.4f), dt);
                maxBob = MathF.Max(maxBob, MathF.Abs(o.BobVerticalMeters));
            }
            Check("空中无走路摇晃", maxBob < 0.002f, $"空中最大 bob={maxBob:F5} m");
        }

        // ═════════ 14. 翻越相机脱开与回贴 ═════════
        {
            var rig = new ApexCameraRig();
            rig.Update(Snap(), dt);
            CameraOutput o = default;
            for (int i = 0; i < 40; i++)
                o = rig.Update(Snap(state: MovementState.Mantle, traversalProgress: 0.5f,
                                    onGround: false), dt);
            float blendDuring = o.TraversalBlend;

            for (int i = 0; i < 90; i++) o = rig.Update(Snap(), dt);
            float blendAfter = o.TraversalBlend;

            Check("翻越相机脱开→回贴",
                  blendDuring > 0.9f && blendAfter < 0.05f,
                  $"翻越中 blend={blendDuring:F3}，结束后 blend={blendAfter:F3}");
        }

        // ═════════ 15. 翻越相机高度上限 ═════════
        {
            var cfg = new CameraEffectsConfig { TraversalCapCameraHeight = 1.20f };
            var rig = new ApexCameraRig(cfg);
            rig.Update(Snap(), dt);
            CameraOutput o = default;
            // 攀爬 6m 高墙：眼位 1.85m 高于 cap 1.20m
            for (int i = 0; i < 60; i++)
                o = rig.Update(Snap(state: MovementState.Mantle, traversalProgress: 0.5f,
                                    onGround: false, pos: new F3(0f, 6f, 0f)), dt);
            float drop = -F3.Dot(o.EyeOffset, F3.Up);
            Check("翻越相机高度上限（traversal_mantle_cap_camera_height）",
                  drop > 0.3f,
                  $"相机被压低 {drop:F3} m（眼位 1.85 > 上限 {rig.Config.TraversalCapCameraHeight}）");
        }

        // ═════════ 16. 蹲下时相机前移 ═════════
        {
            var rig = new ApexCameraRig();
            rig.Update(Snap(), dt);
            CameraOutput o = default;
            for (int i = 0; i < 30; i++)
                o = rig.Update(Snap(crouch: 1f, height: 0.975f), dt);
            // 前向 = -Z（yaw=0）
            float forward = -o.EyeOffset.Z;
            Check("蹲下时相机前倾", forward > 0.01f, $"前移 {forward:F4} m");
        }

        // ═════════ 17. 帧率无关：60fps 与 144fps 收敛到同一 FOV ═════════
        {
            float ConvergeFov(float fps, float seconds)
            {
                var rig = new ApexCameraRig();
                float d = 1f / fps;
                int n = (int)MathF.Round(seconds * fps);
                CameraOutput o = default;
                var s = Snap(speed: 10f, sliding: true, sprinting: true);
                for (int i = 0; i < n; i++) o = rig.Update(s, d);
                return o.Fov;
            }
            float f60 = ConvergeFov(60f, 2f);
            float f144 = ConvergeFov(144f, 2f);
            float f240 = ConvergeFov(240f, 2f);
            Check("FOV 收敛与帧率无关（2s 后）",
                  MathF.Abs(f60 - f144) < 0.15f && MathF.Abs(f60 - f240) < 0.15f,
                  $"60fps={f60:F4}°，144fps={f144:F4}°，240fps={f240:F4}°");
        }

        // ═════════ 18. 冲刺眼位前移 ═════════
        {
            var rig = new ApexCameraRig();
            rig.Update(Snap(), dt);
            CameraOutput o = default;
            for (int i = 0; i < 60; i++) o = rig.Update(Snap(speed: 7.6f, sprinting: true), dt);
            float forward = -o.EyeOffset.Z;
            Check("冲刺眼位前移（sprintViewOffset）", forward > 0.03f, $"前移 {forward:F4} m");
        }

        sb.AppendLine();
        sb.AppendLine($"  合计 {checks} 项断言，失败 {failures} 项");
        return (failures == 0, sb.ToString());
    }
}
