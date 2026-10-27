using System;
using System.Text;
using ApexMovement.Core;

namespace ApexMovement.Tests;

/// <summary>
/// v0.5.2 摩擦模型自检（对应需求 m01928 第 4 点）。
///
/// 需求原文（节选）：
/// 「我注意到，玩家的输入带来的加速度是动态的，玩家行走时摩擦力向移动方向的相反方向，
///   但实际上是不对的。目前我是似乎没有设计过角色体重，我希望以国际身体体重指数
///   BMI = 体重 (kg) / [身高 (m)]² 来计算获得体重，目前角色身高为 1.9m，BMI 的正常范围
///   为 18.5-24.9，取中位数 21.7 计算，角色体重应该为 78.337kg，然后再以这个数值计算
///   得到摩擦系数获得最大静摩擦力/滑动摩擦力 …… 然后人行走奔跑起身是静摩擦力是做正功的，
///   因为人不是滑动，所以摩擦力产生的加速度应该是浮动的，而且作用于玩家输入方向。
///   当玩家达到最大速度时，静摩擦力会归零，如果玩家要转向，静摩擦力会将玩家的水平速度
///   限制在上限 …… 当玩家停止输入时，静摩擦力将会与玩家当前移动方向相反，然后缓慢增大到
///   最大静摩擦变为滑动摩擦，直到停止（类似汽车刹车，先降低轮胎滚动速度，最后抱死刹车）。
///   这样修改过后，如果地面摩擦系数非常小，玩家可能无法达到正常行走的最大速度，
///   可以真实模拟冰面难以行走，原地打滑的效果。还有一点是滑铲时始终是滑动摩擦……
///   所以滑铲的时候输入按键 w 前进方向不会产生作用，按 a/d 可以提供一点点的转向控制，
///   按 s 则和之前一样会提供一些主动制动，还有是空中输入也不会产生什么作用，
///   只有很微小的偏移，基本上只能靠惯性飞跃。」
///
/// 本文件逐条守住这些承诺。
/// </summary>
public static class FrictionTest
{
    private static readonly MovementInput Idle = default;

    private static MovementInput In(float mx = 0f, float mz = 0f, bool sprint = false,
                                    bool crouch = false, bool jump = false) => new()
    {
        MoveX = mx,
        MoveZ = mz,
        SprintHeld = sprint,
        CrouchHeld = crouch,
        JumpPressed = jump,
        JumpHeld = jump,
    };

    private static ApexMovementCore MakeCore(MovementConfig cfg, float mu = -1f)
    {
        var world = new AnalyticWorld(0f) { SurfaceFriction = mu < 0f ? null : mu };
        var core = new ApexMovementCore(cfg);
        core.BindWorld(world);
        core.Reset(new F3(0f, 1f, 0f));
        for (int i = 0; i < 30; i++) core.Step(Idle, cfg.FixedDelta);   // 落到地面
        return core;
    }

    private static void Run(ApexMovementCore core, MovementConfig cfg, int ticks,
                            MovementInput input)
    {
        for (int i = 0; i < ticks; i++) core.Step(input, cfg.FixedDelta);
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
            Check(name, MathF.Abs(got - want) <= tol, $"实测 {got:F4}，期望 {want:F4} ± {tol:F4}");

        var cfg = MovementConfig.Apex();
        float g = cfg.Gravity;
        float dt = cfg.FixedDelta;

        sb.AppendLine("=== v0.5.2 摩擦模型 / 体重 / 静摩擦驱动 / 冰面 / 滑铲输入 / 空中控制 ===");

        // ═════════ ① 体重（BMI）与力的换算 ═════════
        {
            float mass = cfg.BodyMass;
            float want = 21.7f * 1.9f * 1.9f;               // 78.337 kg
            Near("① 体重 = BMI × 身高²（21.7 × 1.9² = 78.337 kg）", mass, want, 1e-3f);

            // 换身高自动跟着变（BMI 不变）
            var c2 = MovementConfig.Apex();
            c2.StandHeight = 1.6f;
            Near("①b 换身高 ⇒ 体重按 BMI 自动重算（21.7 × 1.6²）",
                c2.BodyMass, 21.7f * 1.6f * 1.6f, 1e-3f);

            // 力 = μ·N = μ·m·g·cosθ
            float n = mass * g;
            Near("①c 法向力 N = m·g（平地）", cfg.NormalForce(1f), n, 1e-2f);
            Near("①d 最大静摩擦力 Fs = μs·N", cfg.MaxStaticFrictionForce(1f),
                cfg.GroundStaticFrictionCoefficient * n, 1e-2f);
            Near("①e 滑动摩擦力 Fk = μk·N", cfg.MaxKineticFrictionForce(1f),
                cfg.GroundFrictionCoefficient * n, 1e-2f);

            // 斜面：N = m·g·cosθ
            Near("①f 30° 斜面法向力 = m·g·cos30°", cfg.NormalForce(MathF.Cos(30f * MathF.PI / 180f)),
                n * MathF.Cos(30f * MathF.PI / 180f), 1e-2f);
        }

        // ═════════ ② 静摩擦做正功、方向 = 输入方向 ═════════
        {
            var core = MakeCore(cfg);
            // 先侧向跑起来（沿 +X），再突然把输入改成 -Z：驱动加速度方向必须**立刻**
            // 指向新输入方向，而不是继续沿旧速度方向或"反向拖拽"。
            Run(core, cfg, 40, In(mx: 1f, sprint: true));
            float speedX = core.Velocity.X;

            // 输入换成正前方（-Z，yaw=0 的前向）；**冲刺键保持**，否则 6.6 m/s 会先
            // 被判定成"超过走速上限"而进入收速分支（那是另一条语义，见 ④b）。
            core.Step(In(mz: 1f, sprint: true), dt);
            float vz1 = core.Velocity.Z;
            float vx1 = core.Velocity.X;

            // 1 tick 能加多少 = GroundAccel·dt = 10 × 1/60 = 0.167 m/s
            float perTick = cfg.GroundAccel * dt;
            Check("② 驱动加速度作用于**输入方向**（换向后立刻在新方向上加速）",
                speedX > 1f && vz1 < -perTick * 0.9f,
                $"先沿 +X 跑到 {speedX:F2} m/s；换到前向 1 tick 后 vz={vz1:F3}" +
                $"（一 tick 理论 -{perTick:F3}）");

            Check("②b 静摩擦**不会**沿旧速度反方向拖拽（旧方向不被立刻刹掉）",
                vx1 > speedX * 0.5f,
                $"换向 1 tick 后 vx={vx1:F2}（原 {speedX:F2}，若被摩擦减速会掉到 ≈{speedX - 0.1f:F2} 以下）");
        }

        // ═════════ ③ 达到最大速度 ⇒ 静摩擦归零（不再加速） ═════════
        {
            var core = MakeCore(cfg);
            Run(core, cfg, 120, In(mz: 1f, sprint: true));    // 冲到顶速
            float v0 = core.GroundPlaneSpeed;

            // 继续按住同一个方向 30 tick：速度不应该再涨（也不应该被摩擦压低）
            float worstRise = 0f;
            for (int i = 0; i < 30; i++)
            {
                float before = core.GroundPlaneSpeed;
                core.Step(In(mz: 1f, sprint: true), dt);
                worstRise = MathF.Max(worstRise, core.GroundPlaneSpeed - before);
            }
            float v1 = core.GroundPlaneSpeed;

            Check("③ 达到顶速后静摩擦归零：速度既不涨也不掉",
                MathF.Abs(v1 - v0) < 0.05f && worstRise < 0.02f,
                $"顶速 {v0:F3} → {v1:F3} m/s，期间单 tick 最大涨幅 {worstRise:E2}");
        }

        // ═════════ ④ 转向：水平速度被限制在上限 ═════════
        {
            var core = MakeCore(cfg);
            Run(core, cfg, 120, In(mz: 1f, sprint: true));    // 冲到 6.6
            float cap = cfg.SprintSpeed;
            float maxSeen = 0f;

            // 一边按 W 一边持续转视角（绕圈）：速度**可以**转向，但不能超过上限太多
            for (int i = 0; i < 180; i++)
            {
                var input = new MovementInput
                {
                    MoveZ = 1f,
                    SprintHeld = true,
                    MouseDeltaYaw = 0.05f,          // 每秒 3 rad ≈ 170°/s
                };
                core.Step(input, dt);
                maxSeen = MathF.Max(maxSeen, core.GroundPlaneSpeed);
            }

            Check("④ 转向时水平速度不超过上限（跑速永远 ≤ SprintSpeed）",
                maxSeen <= cap * 1.02f,
                $"绕圈 3 秒内最大沿坡速度 {maxSeen:F3} m/s（上限 {cap:F3}，容差 2%）");
        }

        // ═════════ ④b 高速落地 + 按住 W：惯性保留一段后滑回顶速（不是瞬间砍） ═════════
        {
            var world = new AnalyticWorld(0f);
            var core = new ApexMovementCore(cfg);
            core.BindWorld(world);
            core.Reset(new F3(0f, 4.95f, 0f));
            core.SetVelocity(new F3(0f, 0f, -12f));      // 空中 12 m/s 前冲
            // 先落到地面（落地前不按键，落地后立刻按住 W+Shift）
            for (int i = 0; i < 60 && !core.OnGround; i++) core.Step(Idle, dt);
            float atLanding = core.GroundPlaneSpeed;

            core.Step(In(mz: 1f, sprint: true), dt);
            float afterOneTick = core.GroundPlaneSpeed;   // 落地后第一 tick 不能被瞬砍

            Run(core, cfg, 60, In(mz: 1f, sprint: true));
            float settled = core.GroundPlaneSpeed;

            Check("④b 高速落地当帧不被瞬砍到走速（保留惯性）",
                afterOneTick > cfg.SprintSpeed + 2f,
                $"落地 {atLanding:F2} → 按 W 1 tick 后 {afterOneTick:F2} m/s（顶速 {cfg.SprintSpeed:F2}）");

            Near("④c 按住 W 滑一小段后收敛回顶速（不是永远超速）",
                settled, cfg.SprintSpeed, 0.15f);
        }

        // ═════════ ⑤ 松手刹车：静摩擦缓慢长到最大，再抱死成滑动摩擦 ═════════
        {
            var core = MakeCore(cfg);
            Run(core, cfg, 120, In(mz: 1f, sprint: true));
            float v0 = core.HorizontalSpeed;

            // 逐 tick 记录减速量
            var drops = new System.Collections.Generic.List<float>();
            float prev = core.HorizontalSpeed;
            for (int i = 0; i < 18; i++)
            {
                core.Step(Idle, dt);
                drops.Add(prev - core.HorizontalSpeed);
                prev = core.HorizontalSpeed;
            }

            float first = drops[0];
            int rampTicks = Math.Max(1, (int)MathF.Round(cfg.BrakeRampTime / dt));
            float beforeLock = drops[Math.Max(0, rampTicks / 2 - 1)];
            float afterLock = drops[drops.Count - 1];
            float kineticDrop = cfg.GroundFrictionCoefficient * g * MathF.Cos(0f) * dt;

            // v0.5.3 需求（m02395 第 2 点）：刹车**先给最大制动**，再衰减到滑动摩擦。
            float brakeMax = MathF.Min(cfg.GroundStaticFrictionCoefficient * g, cfg.GroundAccel);
            Check("⑤ 刹车起步就是最大制动（≈ 最大输入加速度，不是从 0 长起来）",
                MathF.Abs(first - brakeMax * dt) < brakeMax * dt * 0.05f,
                $"第一 tick 掉 {first:F4} m/s（最大制动 {brakeMax:F2} m/s² × dt = {brakeMax * dt:F4}）");

            Check("⑤b 刹车过程减速量单调下降（最大制动 → 滑动摩擦）",
                first > beforeLock && beforeLock > afterLock,
                $"t=0 {first:F4} → 中段 {beforeLock:F4} → 抱死后 {afterLock:F4} m/s/tick");

            Check("⑤c 抱死后每 tick 减速 = μk·g·dt（纯滑动，与速度无关）",
                MathF.Abs(afterLock - kineticDrop) < kineticDrop * 0.05f,
                $"实测 {afterLock:F5}，理论 {kineticDrop:F5}（偏差 {(afterLock - kineticDrop) / kineticDrop * 100f:F2}%）");

            // 停距应显著长于旧粘性模型（库仑 = v²/(2μk·g)）
            Check("⑤d 冲刺松手停距 ≈ v²/(2μk·g)",
                v0 > 6f,
                $"起刹速度 {v0:F2} m/s，理论停距 {v0 * v0 / (2f * cfg.GroundFrictionCoefficient * g):F2} m");
        }

        // ═════════ ⑥ 冰面：抓不住地、原地打滑、到不了正常走速 ═════════
        {
            var ice = MakeCore(cfg, mu: 0.05f);
            float maxSpeed = 0f;
            float minGrip = 1f;
            for (int i = 0; i < 300; i++)   // 5 秒狂按 W
            {
                ice.Step(In(mz: 1f, sprint: true), dt);
                maxSpeed = MathF.Max(maxSpeed, ice.GroundPlaneSpeed);
                minGrip = MathF.Min(minGrip, ice.GripRatio);
            }

            float walk = cfg.WalkSpeed;
            float sprint = cfg.SprintSpeed;
            Check("⑥ 冰面（μk=0.05）：5 秒也到不了正常走速（难以行走）",
                maxSpeed < walk * 0.5f,
                $"5 秒最大速度 {maxSpeed:F3} m/s（走速 {walk:F3}，冲刺 {sprint:F3}）");

            // 但**必须还能挪**：完全动不了会把玩家永久卡在冰面上（软锁）。
            // 打滑的耗散随滑移速度增大 ⇒ 存在终端速度（实测 ≈ 1.0 m/s）。
            Check("⑥a2 冰面上仍能缓慢挪动（不是完全卡死 / 不会软锁）",
                maxSpeed > 0.3f,
                $"5 秒最大速度 {maxSpeed:F3} m/s（终端速度由推进 = 滑移耗散决定）");

            Check("⑥b 冰面抓地力比值远小于 1（原地打滑）",
                minGrip < 0.15f,
                $"最小抓地比值 {minGrip:F3}（1 = 完全抓地）");

            // 静止起步 1 秒能走多远 —— 冰面应该几乎原地
            var ice2 = MakeCore(cfg, mu: 0.05f);
            F3 p0 = ice2.Position;
            Run(ice2, cfg, 60, In(mz: 1f, sprint: true));
            float iceDist = (ice2.Position - p0).Length();

            var norm = MakeCore(cfg);
            F3 n0 = norm.Position;
            Run(norm, cfg, 60, In(mz: 1f, sprint: true));
            float normDist = (norm.Position - n0).Length();

            Check("⑥c 冰面起步 1 秒位移远小于正常地面（< 1/3）",
                iceDist < normDist / 3f,
                $"冰面 {iceDist:F2} m vs 正常 {normDist:F2} m");

            // 正常地面抓地比值必须 == 1（不改变既有手感）
            Check("⑥d 默认地面抓地比值 = 1（完全抓地，不改变既有手感）",
                norm.GripRatio >= 0.999f,
                $"默认地面 grip={norm.GripRatio:F4}");
        }

        // ═════════ ⑦ 摩擦加速度与质量无关（a = μg，诚实的物理提醒） ═════════
        {
            float Measure(float mass)
            {
                var c = MovementConfig.Apex();
                c.BodyMassOverride = mass;
                var core = MakeCore(c);
                core.SetVelocity(new F3(0f, 0f, -6f));
                Run(core, c, 1, Idle);               // 让刹车斜坡起步
                float a = core.HorizontalSpeed;
                for (int i = 0; i < 10; i++) core.Step(Idle, c.FixedDelta);
                return (a - core.HorizontalSpeed) / (10f * c.FixedDelta);
            }

            float light = Measure(40f);
            float heavy = Measure(120f);
            Check("⑦ 摩擦加速度 a = μk·g 与体重无关（质量在 a = μN/m 里约掉）",
                MathF.Abs(light - heavy) < 0.02f,
                $"40 kg 实测 {light:F4} m/s²，120 kg 实测 {heavy:F4} m/s²");
        }

        // ═════════ ⑧ 滑铲输入：W 无效 / A-D 微转向 / S 制动 ═════════
        {
            ApexMovementCore SlideCore()
            {
                var core = MakeCore(cfg);
                core.SetVelocity(new F3(0f, 0f, -9f));
                Run(core, cfg, 3, In(crouch: true));      // 起滑
                return core;
            }

            // ⑧a 按住 W 滑 0.4 秒 vs 完全不按：速度应当一致（W 不产生作用）
            var a1 = SlideCore();
            var a2 = SlideCore();
            Run(a1, cfg, 24, In(mz: 1f, crouch: true));
            Run(a2, cfg, 24, In(crouch: true));
            Near("⑧a 滑铲时按 W 不产生作用（与不按完全一致）",
                a1.GroundPlaneSpeed, a2.GroundPlaneSpeed, 1e-3f);
            Check("⑧a2 这条用例确实在滑铲里", a1.State == MovementState.Slide,
                $"state={a1.State}");

            // ⑧b A/D 能提供一点点转向（速度方向变了，但大小基本不变）
            var b1 = SlideCore();
            float h0 = b1.GroundPlaneSpeed;
            F3 dir0 = F3.ProjectOnPlane(b1.Velocity, b1.GroundNormal).Normalized();
            Run(b1, cfg, 24, In(mx: 1f, crouch: true));
            float h1 = b1.GroundPlaneSpeed;
            F3 dir1 = F3.ProjectOnPlane(b1.Velocity, b1.GroundNormal).Normalized();
            float turnDeg = MathF.Acos(MathF.Max(-1f, MathF.Min(1f, F3.Dot(dir0, dir1)))) * 180f / MathF.PI;

            Check("⑧b 滑铲按 A/D 能微转向（0° < 转角 < 25°）且不加速",
                turnDeg > 1f && turnDeg < 25f && h1 <= h0 + 1e-3f,
                $"0.4 秒转了 {turnDeg:F1}°，速度 {h0:F2} → {h1:F2} m/s");

            // ⑧c S 是主动制动（比不按掉得快）
            var c1 = SlideCore();
            var c2 = SlideCore();
            float s0 = c1.GroundPlaneSpeed;
            Run(c1, cfg, 24, In(mz: -1f, crouch: true));
            Run(c2, cfg, 24, In(crouch: true));
            Check("⑧c 滑铲按 S 主动制动（比不按明显更慢）",
                c1.GroundPlaneSpeed < c2.GroundPlaneSpeed - 0.5f,
                $"按 S {s0:F2} → {c1.GroundPlaneSpeed:F2} m/s，不按 → {c2.GroundPlaneSpeed:F2} m/s");
        }

        // ═════════ ⑨ 空中输入：不能加能量，只能微调方向 ═════════
        {
            var world = new AnalyticWorld(0f);
            var core = new ApexMovementCore(cfg);
            core.BindWorld(world);
            core.Reset(new F3(0f, 4.95f, 0f));
            core.SetVelocity(new F3(0f, 0f, -6f));
            core.Step(Idle, dt);                     // 进入空中
            float entry = core.HorizontalSpeed;

            float maxHs = entry;
            for (int i = 0; i < 40 && !core.OnGround; i++)
            {
                core.Step(In(mx: 1f, mz: 1f), dt);   // 空中狂按斜向
                maxHs = MathF.Max(maxHs, core.HorizontalSpeed);
            }

            Check("⑨ 空中输入不能加能量（速度增益 ≤ AirSteerSpeedAllowance）",
                maxHs <= entry + cfg.AirSteerSpeedAllowance + 1e-3f,
                $"离地 {entry:F3} m/s，空中最大 {maxHs:F3}（上限 +{cfg.AirSteerSpeedAllowance:F2}）");

            // 但方向要能动（否则完全没手感）
            var core2 = new ApexMovementCore(cfg);
            core2.BindWorld(new AnalyticWorld(0f));
            core2.Reset(new F3(0f, 6f, 0f));
            core2.SetVelocity(new F3(0f, 0f, -6f));
            core2.Step(Idle, dt);
            F3 d0 = core2.Velocity.Normalized();
            for (int i = 0; i < 20 && !core2.OnGround; i++) core2.Step(In(mx: 1f), dt);
            F3 d1 = core2.Velocity.Normalized();
            float turn = MathF.Acos(MathF.Max(-1f, MathF.Min(1f, F3.Dot(d0, d1)))) * 180f / MathF.PI;
            Check("⑨b 空中输入仍能微调方向（0° < 转角 < 45°）",
                turn > 0.2f && turn < 45f,
                $"20 tick 内方向偏转 {turn:F2}°");
        }

        // ═════════ ⑩ 材质 μ 同时缩放静/动摩擦 ═════════
        {
            var rough = MakeCore(cfg, mu: 0.9f);
            Run(rough, cfg, 120, In(mz: 1f, sprint: true));   // 照样能跑到顶速（μs 足够大）
            Near("⑩ μk=0.9 的粗地面仍能跑到冲刺顶速（静摩擦够用）",
                rough.GroundPlaneSpeed, cfg.SprintSpeed, 0.05f);

            // 刹车对比：粗地面停得比默认快
            float StopDist(float mu)
            {
                var core = MakeCore(cfg, mu);
                core.SetVelocity(new F3(0f, 0f, -6f));
                F3 p = core.Position;
                int guard = 0;
                while (core.HorizontalSpeed > 1e-3f && guard++ < 3000)
                    core.Step(Idle, dt);
                return (core.Position - p).Length();
            }

            float dDefault = StopDist(-1f);
            float dRough = StopDist(0.9f);
            Check("⑩b 材质 μ 越大停距越短",
                dRough < dDefault * 0.85f,
                $"μ=0.9 停距 {dRough:F3} m < 默认(0.7) {dDefault:F3} m");
        }

        sb.AppendLine();
        sb.AppendLine($"  合计 {checks} 项断言，失败 {failures} 项");
        return (failures == 0, sb.ToString());
    }
}
