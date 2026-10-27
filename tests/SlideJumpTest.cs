using System;
using System.Collections.Generic;
using System.Text;
using ApexMovement.Core;

namespace ApexMovement.Tests;

/// <summary>
/// v0.4.2 专项验收：**滑铲底层逻辑** 与 **抓墙方向** 两组回归。
///
/// **A. 滑铲跳链 / 空中滑铲（需求方 m04709 第 2 条）**
///   「玩家现在可以无限滑铲连跳，然后越跳越高，在空中保持滑铲姿态连续跳跃，
///    这是不正常的。我原本只想玩家滑铲后按跳跃后，空中依旧保持蹲下的体积，
///    蹲键不松的话落地后会继续保持滑铲状态，并且可以继续跳起来。」
///
/// **B. 抓墙方向（需求方 m04709 第 3 条）**
///   「如果玩家按住 s 倒退坠下高台时，高台边缘会吸附住玩家一小段时间，这是不正常的，
///    应该只有玩家面向高台前进的时候才会触发攀爬，而不是任何方向都触发。」
///
/// ─────────────────────────────────────────────────────────────────────
/// ## 这一版断言相对初版的修正（都是"度量错了"而不是"产品错了"）
///
/// 初版 17 项里有 6 项红，逐条查过根因后确认：**产品侧只有 1 项是真缺陷**
/// （滑铲跳链第 4 跳就断，已在 ApexMovementCore.Step 的 9a 修掉），
/// 其余 5 项是**测试自己的度量写错了**：
///
///  · 1b —— 台面边缘离起点 30 m，而滑铲 1.1 s 就被摩擦刹停，之后是 2.03 m/s 的
///    蹲行；420 tick 根本走不到边缘。→ 改成"把玩家直接放到边缘外起滑"。
///  · 4b —— 起跳后跑了 90 tick 才断言"落地仍在滑铲"，那时滑铲早已自然结束、
///    速度掉到蹲行 2.03。→ 改成**在落地那一 tick** 断言。
///  · 5/5c —— 链条只完成 4 次是真缺陷（每跳被地面摩擦白吃 0.44 m/s）；已修。
///    但"峰值差 < 0.15 m"这个判据本身也不对：第 0 跳是满高、之后是跳跃疲劳
///    的 30% 高度（wiki antiMultiJumpHeightFrac = 0.30），**本来就不该相等**。
///    → 改成"非递增 + 每跳初速不超过 JumpVelocity"这个真正判据。
///  · 5d —— 量的是**脚底**。空中收腿（AirCrouchShrinksToMiddle）时胶囊中心不动
///    而 _currentHeight 由 1.68 收到 0.975，于是 feet = pos − curH/2 平白抬高
///    0.352 m —— 那不是高度增益，是姿态。→ 改成量**胶囊中心**。
///  · 9 —— PlaceInAir 里的 Settle(2) 会先让玩家自然下坠两 tick，
///    这两 tick 里 approach≈0（速度还没设），玩家**在断言之前就已经挂在墙上了**。
///    → 改成 Reset 后直接 SetVelocity，不做 Settle。
/// ─────────────────────────────────────────────────────────────────────
/// </summary>
public static class SlideJumpTest
{
    /// <summary>抓墙方向门的容差：approach 与 -WallGrabMaxRecedeSpeed 的比较容差 (m/s)。</summary>
    private const float ApproachEps = 1e-3f;

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

    /// <summary>一堵 6m 高的竖直墙：墙面在 z=-2（法线 +Z），玩家 yaw=0 ⇒ 前向 -Z 正对墙。</summary>
    private static AnalyticWorld WallWorld()
    {
        var w = new AnalyticWorld(0f);
        w.AddBox(new F3(-5f, 0f, -4f), new F3(5f, 6f, -2f));
        return w;
    }

    /// <summary>
    /// 把玩家放进墙前的空中并**立即**赋予速度。
    ///
    /// ★ 这里**不能**有 Settle：初版先 Settle(2) 再设速度，而这两 tick 里
    /// approach ≈ 0（速度还是 0）已经满足"扒墙默认化"，玩家在断言之前就挂上去了 ——
    /// 于是"背离墙不该抓"这条永远测不到真正的门，只测到了"已经挂上了"。
    /// Reset 会把 _coyoteTimer / _wallHangCooldown / _wallGrabNeedW 全部清零，
    /// 所以 Reset + 直接设速度就是干净的初始条件。
    /// </summary>
    private static void PlaceInAir(ApexMovementCore core, F3 velocity, float z = -1.5f)
    {
        core.Reset(new F3(0f, 4f + core.Config.StandHeight * 0.5f, z), 0f);
        core.SetVelocity(velocity);
    }

    private static bool Grabbed(ApexMovementCore core) =>
        core.State is MovementState.Climb or MovementState.WallHang;

    // ───────────────────────── 主体 ─────────────────────────

    public static (bool Pass, string Report) Run()
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail)
        {
            if (ok) { pass++; sb.AppendLine($"[PASS] {name}"); }
            else { fail++; sb.AppendLine($"[FAIL] {name} —— {detail}"); }
        }

        var cfg = MovementConfig.Apex();

        // ══════════════════ A1. 空中不可能是滑铲 ══════════════════

        // 1) 从台面边缘**滑**出去（不是走着掉出去）。
        //
        //    ★ 初版这条是假的：台面边缘离起点只有 0.5 m，而玩家要从静止加速到
        //    SlideMinStartSpeed(5.08) 才能起滑 —— 实测他会在起滑之前就以 3~4 m/s
        //    **走下去**（那一步 _state 由 Ground → Air，走的是另一条代码路径），
        //    整条用例根本没测到「滑铲中滑出台沿」。现在给 6 m 助跑距离，保证
        //    离地那一刻状态确实是 Slide。
        {
            var world = new AnalyticWorld(0f);
            // 台面顶面 y=1，z ∈ [-6, 0]；yaw=π ⇒ 前向 +Z，一路滑向 z=0 的台沿。
            world.AddBox(new F3(-6f, 0f, -6f), new F3(6f, 1.0f, 0f));
            var core = MakeCore(world, cfg);
            core.Reset(new F3(0f, 1.0f + cfg.StandHeight * 0.5f, -4.0f), MathF.PI);
            Settle(core, 20);

            // 助跑只跑到「刚出滑铲门槛速度」为止，不再往下跑 ——
            // 台沿在 z=0，跑过头就会在起滑之前先掉下去（那又变成测"走着掉出去"了）。
            int guard = 0;
            while (guard++ < 180 && core.HorizontalSpeed < cfg.SlideMinStartSpeed + 0.5f)
                core.Step(In(mz: 1f, sprint: true), cfg.FixedDelta);

            bool airSlideSeen = false;
            bool becameAir = false;
            bool slidOffEdge = false;         // 离地那一刻状态是 Slide（证明这条用例真的在测它）
            float minFeetInAir = float.MaxValue;
            float spdAtEdge = 0f;
            for (int i = 0; i < 400; i++)
            {
                bool wasOnGround = core.OnGround;
                // 注意要在 Step **之前**取状态：v0.4.2 起"离地当场就把 Slide 转成 Air"，
                // 所以 Step 之后再看状态永远看不到 Slide —— 那正是这条不变量成立的表现。
                bool wasSliding = core.State == MovementState.Slide;
                core.Step(In(mz: 1f, crouch: true, sprint: true), cfg.FixedDelta);
                if (wasOnGround && !core.OnGround)
                {
                    // 本 tick 从「有地面」变成「没地面」—— 离地瞬间
                    slidOffEdge = wasSliding;
                    spdAtEdge = core.HorizontalSpeed;
                }
                if (!core.OnGround)
                {
                    if (core.State == MovementState.Slide || core.Snapshot.Sliding) airSlideSeen = true;
                    if (core.State == MovementState.Air)
                    {
                        becameAir = true;
                        minFeetInAir = MathF.Min(minFeetInAir, core.FeetPosition.Y);
                    }
                }
                if (becameAir && core.OnGround) break;
            }

            Check("1. 滑铲中滑出台沿：空中任何一个 tick 都不是 Slide（含离地那一 tick）",
                !airSlideSeen, "出现过「空中 + Slide」");
            Check("1b. 这条用例确实测的是「滑铲中滑出台沿」（离地那一刻状态 = Slide）",
                slidOffEdge && spdAtEdge >= cfg.SlideMinStartSpeed,
                $"离地那一刻是否在滑铲={slidOffEdge} 水平速度={spdAtEdge:F2}（门槛 {cfg.SlideMinStartSpeed:F2}）");
            Check("1c. 滑出台沿后确实进入 Air 并落回地面",
                becameAir && core.OnGround,
                $"becameAir={becameAir} 最终 state={core.State} feet={core.FeetPosition.Y:F2} 空中最低脚底={minFeetInAir:F2}");
        }

        // 2) 空中按住蹲 + 高速下坠 → 不进入 Slide（Apex 的 Air Slide 入口已删）
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world, cfg);
            PlaceInAir(core, new F3(0f, -9f, 6f));
            bool everSlide = false;
            float minVy = 0f;
            int airTicks = 0;
            bool landed = false;
            for (int i = 0; i < 120; i++)
            {
                core.Step(In(mz: 1f, crouch: true, sprint: true), cfg.FixedDelta);

                // ★ 只判**空中**那些 tick。落地之后按住蹲 + 速度 ≥ 门槛本来就该
                //   进滑铲（v0.4.2 的 9a「落地即滑铲」，需求明确要求保留），
                //   所以落地后的 Slide 不算违例 —— 这段用例要证的是"空中不许转滑铲"。
                if (!core.OnGround)
                {
                    airTicks++;
                    if (core.State == MovementState.Slide) everSlide = true;
                }
                else if (airTicks > 2)
                {
                    landed = true;
                    break;
                }
                minVy = MathF.Min(minVy, core.VerticalSpeed);
            }

            Check("2. 空中按住蹲 + 高速下坠 → 不会转成滑铲（Air Slide 已删除）",
                !everSlide && landed,
                $"空中 {airTicks} tick，出现过「空中 + Slide」={everSlide}，已落地={landed}");
            Check("2b. 该场景确实构成「高速下坠」（下坠 >200hu/s = 5.08m/s），不是条件没满足而空过",
                minVy < -5.08f, $"最大下坠速度 {minVy:F2} m/s（Apex Air Slide 门槛 200hu/s）");
        }

        // 3) 空中按住蹲 → 胶囊保持蹲姿体积
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world, cfg);
            core.Reset(new F3(0f, 30f, 0f), 0f);
            core.SetVelocity(new F3(0f, 0f, 6f));
            Run(core, 40, In(mz: 1f, crouch: true, sprint: true));

            Check("3. 空中按住蹲 → 仍是蹲姿体积（需求「空中依旧保持蹲下的体积」）",
                MathF.Abs(core.CurrentHeight - cfg.CrouchHeight) < 0.01f && !core.OnGround,
                $"h={core.CurrentHeight:F3}（蹲姿 {cfg.CrouchHeight:F3}）onGround={core.OnGround}");
        }

        // ══════════════════ A2. 滑铲跳链 ══════════════════

        // 4) 滑铲 → 跳（离地即 Air）→ 落地那一 tick 就回到 Slide
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world, cfg);
            core.Reset(new F3(0f, cfg.StandHeight * 0.5f, 0f), 0f);
            Settle(core, 20);
            Run(core, 90, In(mz: 1f, sprint: true));                 // 助跑
            Run(core, 30, In(mz: 1f, sprint: true, crouch: true));   // 起滑
            bool startedSlide = core.State == MovementState.Slide;

            core.Step(In(mz: 1f, sprint: true, crouch: true, jump: true), cfg.FixedDelta);
            bool airborne = core.State == MovementState.Air;

            // 空中保持蹲姿体积 + 从不是 Slide
            bool airSlide = false;
            bool airCrouched = true;
            int landTick = -1;
            for (int i = 0; i < 240; i++)
            {
                core.Step(In(mz: 1f, sprint: true, crouch: true), cfg.FixedDelta);
                if (!core.OnGround)
                {
                    if (core.State == MovementState.Slide) airSlide = true;
                    if (MathF.Abs(core.CurrentHeight - cfg.CrouchHeight) > 0.01f) airCrouched = false;
                }
                else { landTick = i; break; }
            }
            // 落地那一 tick 之后**不再多跑**：初版多跑 90 tick，滑铲早已自然结束。
            bool resumed = core.State == MovementState.Slide;

            Check("4. 滑铲 → 跳 → 离地时是 Air（不是 Slide）",
                startedSlide && airborne, $"起滑={startedSlide} 起跳后 state={core.State}");
            Check("4b. 落地那一 tick 就已经回到滑铲（不是先站一帧再起滑）",
                resumed && landTick >= 0,
                $"落地 tick={landTick} state={core.State} speed={core.HorizontalSpeed:F2}（门槛 {cfg.SlideMinStartSpeed:F2}）");
            Check("4c. 整段滞空期间胶囊一直是蹲姿体积、且从不进入 Slide",
                airCrouched && !airSlide,
                $"airCrouched={airCrouched} airSlide={airSlide}");
        }

        // 5) 连续滑铲跳：8 连跳全部成立 + 不抬升 + 空中弹道不变量
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world, cfg);
            core.Reset(new F3(0f, cfg.StandHeight * 0.5f, 0f), 0f);
            Settle(core, 20);
            Run(core, 90, In(mz: 1f, sprint: true));

            const int Hops = 8;
            var peaksCenter = new List<float>();
            var peaksFeet = new List<float>();
            var jumpVys = new List<float>();
            var entrySpeeds = new List<float>();
            bool slideWhileAir = false;
            bool ballisticOk = true;
            string ballisticBroken = "";
            int chains = 0;

            for (int k = 0; k < Hops; k++)
            {
                int guard = 0;
                while (guard++ < 180 && core.State != MovementState.Slide)
                    core.Step(In(mz: 1f, sprint: true, crouch: true), cfg.FixedDelta);
                if (core.State != MovementState.Slide) break;
                entrySpeeds.Add(core.HorizontalSpeed);

                core.Step(In(mz: 1f, sprint: true, crouch: true, jump: true), cfg.FixedDelta);
                if (core.State != MovementState.Air) break;
                jumpVys.Add(core.VerticalSpeed);

                float peakCenter = core.Position.Y;
                float peakFeet = core.FeetPosition.Y;
                int t = 0;
                while (t++ < 240 && !core.OnGround)
                {
                    float vyBefore = core.VerticalSpeed;
                    core.Step(In(mz: 1f, sprint: true, crouch: true), cfg.FixedDelta);
                    if (!core.OnGround && core.State == MovementState.Slide) slideWhileAir = true;
                    peakCenter = MathF.Max(peakCenter, core.Position.Y);
                    peakFeet = MathF.Max(peakFeet, core.FeetPosition.Y);
                    // 弹道不变量：空中每 tick 竖直速度只允许减少 g·dt
                    // （起跳 tick 与落地 tick 不在这个窗口内）。
                    if (!core.OnGround && ballisticOk)
                    {
                        float expect = vyBefore - cfg.Gravity * cfg.FixedDelta;
                        if (MathF.Abs(core.VerticalSpeed - expect) > 1e-3f)
                        {
                            ballisticOk = false;
                            ballisticBroken = $"第{k}跳 t={t} vy {vyBefore:F4}→{core.VerticalSpeed:F4}（应为 {expect:F4}）";
                        }
                    }
                }
                peaksCenter.Add(peakCenter);
                peaksFeet.Add(peakFeet);
                chains++;
                if (!core.OnGround) break;   // 没落地就超时，链断
            }

            float maxVy = 0f, minVy = 0f;
            foreach (float v in jumpVys) { maxVy = MathF.Max(maxVy, v); minVy = MathF.Min(minVy, v); }
            float maxCenter = 0f, minCenter = float.MaxValue;
            foreach (float p in peaksCenter) { maxCenter = MathF.Max(maxCenter, p); minCenter = MathF.Min(minCenter, p); }

            var peakStr = new StringBuilder();
            foreach (float p in peaksCenter) peakStr.Append($"{p:F3} ");
            var speedStr = new StringBuilder();
            foreach (float s in entrySpeeds) speedStr.Append($"{s:F2} ");

            Check($"5. 连续滑铲跳 {Hops} 次全部成立（每一跳都「进滑铲 → 起跳 → 落地再进滑铲」）",
                chains >= Hops, $"实际完成 {chains} 次");
            Check("5b. 滑铲跳链中空中从不出现 Slide 状态",
                !slideWhileAir, "空中出现了 Slide");

            // ★ 真正的"不再越跳越高"判据 A：起跳初速有硬上限（能量不能凭空增长）
            Check("5c. 每一跳的起跳初速都不超过设计 JumpVelocity（没有凭空叠加竖直能量）",
                jumpVys.Count > 0 && maxVy <= cfg.JumpVelocity + 1e-3f,
                $"起跳初速区间 [{minVy:F4}, {maxVy:F4}]，JumpVelocity={cfg.JumpVelocity:F4}");

            // ★ 判据 B：胶囊中心峰值非递增（允许跳跃疲劳造成的下降，不允许上升）
            Check("5d. 每次跳跃的**胶囊中心**峰值不高于第 0 跳（不再越跳越高）",
                peaksCenter.Count > 0 && maxCenter <= peaksCenter[0] + 0.02f,
                $"中心峰值 {peakStr}（最高 {maxCenter:F3} > 第0跳 {peaksCenter[0]:F3}）");

            // 参考读数：脚底峰值（受"空中收腿"影响，只作展示不作判据）
            Check("5e. 空中弹道不变量成立：只要在空中，竖直速度每 tick 只减 g·dt",
                ballisticOk, ballisticBroken);

            Check("5f. 滑铲跳链不会掉速到滑不动（落地即滑铲，链条可持续）",
                chains >= Hops && entrySpeeds[entrySpeeds.Count - 1] >= cfg.SlideMinStartSpeed,
                $"每跳进滑铲时的水平速度 {speedStr}（门槛 {cfg.SlideMinStartSpeed:F2}）");

            sb.AppendLine($"      · 胶囊中心峰值：{string.Join(" ", peaksCenter.ConvertAll(p => p.ToString("F3")))}（判据已用这一列）");
            sb.AppendLine($"      · 脚底峰值（含空中收腿，仅展示）：{string.Join(" ", peaksFeet.ConvertAll(p => p.ToString("F3")))}");
            sb.AppendLine($"      · 起跳初速（vy）：{string.Join(" ", jumpVys.ConvertAll(v => v.ToString("F4")))}");
        }

        // 6) 空中狂按跳 + 按住蹲冲刺：不接受任何形态的空中滑铲 / 抬升
        {
            var world = new AnalyticWorld(0f);
            var core = MakeCore(world, cfg);
            core.Reset(new F3(0f, cfg.StandHeight * 0.5f, 0f), 0f);
            Settle(core, 20);
            Run(core, 120, In(mz: 1f, sprint: true));

            bool slideInAir = false;
            bool vyGainWhileAir = false;
            float maxCenter = 0f;
            for (int i = 0; i < 1200; i++)   // 20 秒
            {
                bool wasAir = !core.OnGround;
                float vyBefore = core.VerticalSpeed;
                core.Step(In(mz: 1f, sprint: true, crouch: true, jump: true), cfg.FixedDelta);
                if (!core.OnGround)
                {
                    if (core.State == MovementState.Slide) slideInAir = true;
                    maxCenter = MathF.Max(maxCenter, core.Position.Y);
                    if (wasAir && core.VerticalSpeed > vyBefore + 1e-3f) vyGainWhileAir = true;
                }
            }

            Check("6. 按住蹲+冲刺并每 tick 狂按跳 20 秒：空中从不出现 Slide",
                !slideInAir, "空中出现了 Slide");
            Check("6b. 同上：空中竖直速度从不**增加**（没有虚拟平台 / 空中二次起跳）",
                !vyGainWhileAir, "空中出现了向上的速度增量");
            float ceiling = cfg.StandHeight * 0.5f + cfg.JumpHeight + 0.30f;
            Check("6c. 同上：胶囊中心峰值始终不超过单次跳跃高度",
                maxCenter <= ceiling,
                $"最高中心 {maxCenter:F3}（站立中心 {cfg.StandHeight * 0.5f:F3} + JumpHeight {cfg.JumpHeight:F3} + 余量 0.30 = {ceiling:F3}）");
        }

        // 7) 空中穿过打滑体积 + 按住蹲 → 同样不能变成滑铲
        //    （这是 v0.4.2 补掉的**最后一个**空中起滑铲入口：StepSlip 里原来是
        //       BeginSlide(input, fromAir: !_onGround)，空中进滑区按蹲就会得到一个
        //       空中 Slide。现在 BeginSlide 的签名里已经没有任何 fromAir，
        //       且调用点全部要求 _onGround —— 这条断言把这个洞钉死。）
        {
            var world = new AnalyticWorld(0f);
            world.AddSlipSphere(new F3(0f, 12f, 0f), 6f, 6f, 30f, new F3(1f, 0f, 0f), false);
            var core = MakeCore(world, cfg);
            core.Reset(new F3(0f, 22f, 0f), 0f);
            core.SetVelocity(new F3(0f, -8f, 0f));

            bool slideInAir = false;
            bool everSlip = false;
            int airTicks = 0;
            for (int i = 0; i < 400 && !core.OnGround; i++)
            {
                core.Step(In(mz: 1f, crouch: true, sprint: true), cfg.FixedDelta);
                if (core.State == MovementState.Slip) everSlip = true;
                if (!core.OnGround)
                {
                    airTicks++;
                    if (core.State == MovementState.Slide) slideInAir = true;
                }
            }

            Check("7. 空中穿过打滑体积 + 按住蹲 → 也不会变成滑铲（Slip→Slide 的空中入口已封）",
                !slideInAir, $"空中 tick={airTicks} 空中出现过 Slide={slideInAir}");
            Check("7b. 该场景确实让玩家在空中进入了 Slip（条件满足，不是空过）",
                everSlip && airTicks > 20, $"进过 Slip={everSlip} 空中 tick={airTicks}");
        }

        // ══════════════════ B. 抓墙方向 ══════════════════

        // 8) 倒退坠下高台（需求原文场景）：状态从不进入 Climb / WallHang
        //
        //    ⚠ 判别力说明（独立验证 task-2 的 s4a/b6）：这条用的是**走路速度**
        //    4.4 m/s，而那个速度下**旧核也不抓** —— 几何先失效了，所以它
        //    **证不了方向门存在**，只能证"新核在需求场景下不抓"。
        //    真正有判别力的是 9a/9c 的低速背离带。
        {
            var world = new AnalyticWorld(0f);
            world.AddBox(new F3(-6f, 0f, -6f), new F3(6f, 3f, -2f));
            var core = MakeCore(world, cfg);
            core.Reset(new F3(0f, 3f + cfg.StandHeight * 0.5f, -4f), 0f);
            Settle(core, 20);
            bool onTop = core.OnGround && core.FeetPosition.Y > 2.9f;

            bool grabbed = false;
            for (int i = 0; i < 200; i++)
            {
                core.Step(In(mz: -1f), cfg.FixedDelta);
                if (Grabbed(core)) grabbed = true;
            }

            Check("8. 倒退坠下高台不会被台子侧面吸住（不进入 Climb / WallHang）",
                onTop && !grabbed, $"站上高台={onTop} 抓住过={grabbed}");
            Check("8b. 倒退坠台后确实掉到地面（不是被挂在半空）",
                core.OnGround && core.FeetPosition.Y < 0.1f,
                $"state={core.State} feet={core.FeetPosition.Y:F2}");
        }

        // 9) 方向门扫描：面朝墙、不按 W，approach 速度（沿墙面法线朝墙为正）扫一遍，
        //    抓 / 不抓的分界必须落在 0（配置容差 0.05 以内）。
        //
        //    几何约定：墙面法线 n = (0,0,+1)（玩家在墙的 +Z 侧）。所以
        //      approach = -dot(水平速度, n) = -vz   ⇒   vz = -approach
        //    vz 为正 = 玩家往 +Z 走 = **背离**墙；vz 为负 = 朝墙。
        {
            static (bool Ok, string Detail, string Boundary, List<(float approach, bool grabbed)> Rows) Sweep(
                float lo, float hi, float step, MovementConfig cfg)
            {
                var results = new List<(float approach, bool grabbed)>();
                for (float a = lo; a <= hi + 1e-4f; a += step)
                {
                    var world = WallWorld();
                    var core = MakeCore(world, cfg);
                    PlaceInAir(core, new F3(0f, -3f, -a));
                    // 只跑 3 tick：朝墙方向位移 ≤ 0.11m，保证玩家始终处于
                    // WallHangReach 之内 —— 于是"没抓住"只可能来自方向门，
                    // 不可能来自"离墙太远"或"已经穿进墙里"。
                    Run(core, 3, In());
                    results.Add((a, Grabbed(core)));
                }

                bool ok = true;
                var bad = new StringBuilder();
                foreach (var (a, g) in results)
                {
                    bool expect = a > -cfg.WallGrabMaxRecedeSpeed - ApproachEps;
                    if (g != expect) { ok = false; bad.Append($"approach={a:F3}→抓={g}(期望{expect}) "); }
                }

                // 分界点 = 从"不抓"翻成"抓"的那一步（approach 递增方向）
                string boundary = "(扫描区间内没有分界)";
                for (int i = 1; i < results.Count; i++)
                {
                    if (!results[i - 1].grabbed && results[i].grabbed)
                    {
                        boundary = $"approach {results[i - 1].approach:F3} 不抓 → {results[i].approach:F3} 抓";
                        break;
                    }
                }
                return (ok, bad.ToString(), boundary, results);
            }

            // 9a) 细网格：负侧一直扫到 −3.0，把「判别带」整段包进来，分界点必须落在 0
            var fine = Sweep(-3.0f, 1.5f, 0.125f, cfg);
            Check("9a. 面朝墙且不按 W：沿法线 approach > 0（朝墙/静止）就抓、approach < 0（背离墙）就不抓",
                fine.Ok, fine.Detail);
            sb.AppendLine($"      · 方向门分界：{fine.Boundary}（配置 WallGrabMaxRecedeSpeed={cfg.WallGrabMaxRecedeSpeed:F3}，理论分界 = 0）");

            // 9b) 大背离速度一律不抓
            var far = Sweep(-6f, -3.25f, 0.25f, cfg);
            Check("9b. 高速背离墙（3.25 … 6 m/s）一律不抓",
                far.Ok, far.Detail);

            // 9c) **判别力自证** —— 这条最关键。
            //
            //   为什么要单独断言：走台沿那种"倒退坠下高台"的真实场景（速度 ≈4.4 m/s）
            //   **并不具判别力** —— 独立验证（verifier task-2 的 b8/s4a/b6）测到：
            //   4.4 m/s 时**旧核（没有方向门）也不抓**，因为几何先失效了
            //   （玩家中心要 0.3064s 才降到台面顶以下，而 reach 在 0.204s 就已够不到）。
            //   真正能区分"有没有方向门"的是**低速背离带**：
            //     台沿后退速度 0…9 m/s 扫 37 点 ⇒ 旧核在 1.15…2.84 m/s 这 7 点上会误抓，
            //     新核 0/37。闭式解预测的分界 ≈ 0.9/0.3064 ≈ 2.94 m/s，实测区间吻合。
            //   所以本用例必须在 [0.25, 2.94] 这个带里取到点、且一个都不抓。
            var band = fine.Rows.FindAll(r => r.approach <= -0.25f && r.approach >= -2.94f);
            int bandGrabbed = band.FindAll(r => r.grabbed).Count;
            Check("9c. 扫描覆盖「旧核会误抓」的低速背离判别带（0.25–2.94 m/s）且全部不抓",
                band.Count >= 15 && bandGrabbed == 0,
                $"带内采样 {band.Count} 点，其中抓到 {bandGrabbed} 点（要求 ≥15 点且 0 抓）");
        }

        // 10) 面朝墙 + 纯垂直下落（不按任何键）→ 仍然自动扒住
        {
            var world = WallWorld();
            var core = MakeCore(world, cfg);
            PlaceInAir(core, new F3(0f, -3f, 0f));
            Run(core, 3, In());
            Check("10. 面朝墙 + 纯垂直下落（不按任何键）→ 仍然自动扒住（扒墙默认化不回退）",
                Grabbed(core), $"state={core.State}");
        }

        // 11) 面朝墙 + 水平速度朝向墙 → 抓
        {
            var world = WallWorld();
            var core = MakeCore(world, cfg);
            PlaceInAir(core, new F3(0f, -3f, -3f));
            Run(core, 3, In());
            Check("11. 面朝墙 + 水平速度朝向墙 → 抓住",
                Grabbed(core), $"state={core.State}");
        }

        // 12) 面朝墙 + 背离墙但按着 W → 抓（W 是明确的前进意图）
        {
            var world = WallWorld();
            var core = MakeCore(world, cfg);
            PlaceInAir(core, new F3(0f, -3f, 3f));
            Run(core, 3, In(mz: 1f));
            Check("12. 面朝墙 + 背离墙但按着 W（明确前进意图）→ 抓住",
                Grabbed(core), $"state={core.State}");
        }

        // 13) 面朝墙 + 从静止开始按 S 下坠 → 不抓（意图门，补 9 的"速度还没建立"盲区）
        {
            var world = WallWorld();
            var core = MakeCore(world, cfg);
            PlaceInAir(core, new F3(0f, -0.5f, 0f));
            Run(core, 5, In(mz: -1f));
            Check("13. 面朝墙 + 按 S 下坠（意图就是「往下掉」）→ 不抓",
                !Grabbed(core), $"state={core.State} hSpeed={core.HorizontalSpeed:F2}");
        }

        // 14) 配置自洽
        {
            Check("14. 抓墙方向门槛配置存在且非负",
                cfg.WallGrabMaxRecedeSpeed >= 0f && cfg.WallGrabMaxRecedeSpeed <= 1f,
                $"WallGrabMaxRecedeSpeed={cfg.WallGrabMaxRecedeSpeed}");
        }

        sb.Insert(0, "=== v0.4.2 滑铲跳链 / 空中滑铲 / 抓墙方向 ===\n");
        // ═════════ v0.5 滑铲入口重做（需求 m01202）═════════
        //
        //     需求：「滑铲入口是基于当前行走平面的移动速度（由基础移速 + 外置因素获得）
        //     判定是否超过滑铲阈值，再决定是否进入滑铲状态，当允许进入滑铲状态后
        //     再添加 boost 速度」「基于滑铲平面角度添加 boost」「取消滑铲速度上限」
        //     「基础蹲走移速是滑铲减速后的最慢移动速度（保底）」。
        {
            // 15) 低于门槛不能起滑（蹲着慢走）
            {
                var world = new AnalyticWorld(0f);
                var c = MakeCore(world, cfg);
                c.Reset(new F3(0f, 1f, 0f));
                Settle(c);
                Run(c, 40, In(mz: 1f, crouch: true));      // 蹲走 2.0 < 门槛 5.0
                Check("15. 平面速度低于滑铲门槛（蹲走 2.0 < 5.0）→ 不进滑铲",
                    c.State != MovementState.Slide &&
                    c.GroundPlaneSpeed < cfg.SlideMinStartSpeed,
                    $"state={c.State} 平面速度={c.GroundPlaneSpeed:F2}（门槛 {cfg.SlideMinStartSpeed:F2}）");
            }

            // 16) 平地冲刺跨越门槛 → 进滑铲，boost 按 1/cosθ（平地 = 1）加上
            {
                var world = new AnalyticWorld(0f);
                var c = MakeCore(world, cfg);
                c.Reset(new F3(0f, 1f, 0f));
                Settle(c);
                Run(c, 60, In(mz: 1f, sprint: true));      // 冲刺 6.604
                float before = c.GroundPlaneSpeed;
                c.Step(In(mz: 1f, sprint: true, crouch: true), cfg.FixedDelta);
                float after = c.GroundPlaneSpeed;
                Check("16. 平地进滑铲：入口速度 = 起滑前平面速度 + boost（无上限）",
                    c.State == MovementState.Slide &&
                    MathF.Abs(after - (before + cfg.SlideBoostSpeed)) < 0.05f,
                    $"起滑前 {before:F3} + boost {cfg.SlideBoostSpeed:F2} = {before + cfg.SlideBoostSpeed:F3}" +
                    $"；实测 {after:F3}（旧上限 10.16 已取消）");
            }

            // 17) 取消上限：起滑前已经 9.9 m/s，boost 后不夹紧
            {
                var world = new AnalyticWorld(0f);
                var c = MakeCore(world, cfg);
                c.Reset(new F3(0f, 1f, 0f));
                Settle(c);
                c.SetVelocity(new F3(0f, 0f, -9.9f));
                c.Step(In(mz: 1f, crouch: true), cfg.FixedDelta);
                Check("17. 取消滑铲速度上限：9.9 → 9.9+boost（不再被 10.16 夹住）",
                    c.State == MovementState.Slide && c.GroundPlaneSpeed > 12f,
                    $"state={c.State} 速度={c.GroundPlaneSpeed:F3}（9.9 + {cfg.SlideBoostSpeed:F2}）");
            }

            // 18) 保底：减速到蹲走基础移速就交还地面状态机（Ctrl 仍按着 → 蹲姿）
            {
                var world = new AnalyticWorld(0f);
                var c = MakeCore(world, cfg);
                c.Reset(new F3(0f, 1f, 0f));
                Settle(c);
                c.SetVelocity(new F3(0f, 0f, -5.5f));
                c.Step(In(mz: 1f, crouch: true), cfg.FixedDelta);
                bool entered = c.State == MovementState.Slide;
                float minSpeed = float.MaxValue;
                int ticks = 0;
                while (c.State == MovementState.Slide && ticks < 600)
                {
                    c.Step(In(mz: 1f, crouch: true), cfg.FixedDelta);
                    minSpeed = MathF.Min(minSpeed, c.GroundPlaneSpeed);
                    ticks++;
                }
                Check("18. 滑铲减速到蹲走基础速度即结束（保底 2.0 m/s），之后仍是蹲着",
                    entered && c.State != MovementState.Slide &&
                    minSpeed >= cfg.CrouchWalkSpeed - 0.05f &&
                    c.CrouchAmount > 0.5f,
                    $"结束速度 {minSpeed:F3}（保底 {cfg.CrouchWalkSpeed:F2}），" +
                    $"状态 {c.State}，蹲伏 {c.CrouchAmount:F2}，历时 {ticks} tick");
            }

            // 18b) 滑铲结束后：只按 Ctrl → 蹲伏（原地不动）；W+Ctrl → 蹲走 2.0 m/s
            //
            //      需求 m01202：「变成最慢移动速度后如果玩家还按着 ctrl，则会变成蹲伏状态，
            //      如果玩家还按着 w+ctrl，则会变成蹲走状态」。
            {
                // (a) 结束后松开 W（只按 Ctrl）→ 蹲伏，速度归零
                {
                    var world = new AnalyticWorld(0f);
                    var c = MakeCore(world, cfg);
                    c.Reset(new F3(0f, 1f, 0f));
                    Settle(c);
                    c.SetVelocity(new F3(0f, 0f, -5.5f));
                    c.Step(In(mz: 1f, crouch: true), cfg.FixedDelta);
                    int ticks = 0;
                    while (c.State == MovementState.Slide && ticks++ < 600)
                        c.Step(In(mz: 1f, crouch: true), cfg.FixedDelta);
                    Run(c, 60, In(crouch: true));          // 松 W，只按蹲
                    Check("18b. 滑铲结束 + 只按 Ctrl → 蹲伏不动（速度归零、仍是蹲姿）",
                        c.State == MovementState.Ground && c.CrouchAmount > 0.9f &&
                        c.HorizontalSpeed < 0.05f,
                        $"state={c.State} 蹲伏={c.CrouchAmount:F2} 速度={c.HorizontalSpeed:F3}");
                }

                // (b) 结束后继续按 W+Ctrl → 蹲走（速度稳定在 CrouchWalkSpeed）
                {
                    var world = new AnalyticWorld(0f);
                    var c = MakeCore(world, cfg);
                    c.Reset(new F3(0f, 1f, 0f));
                    Settle(c);
                    c.SetVelocity(new F3(0f, 0f, -5.5f));
                    c.Step(In(mz: 1f, crouch: true), cfg.FixedDelta);
                    int ticks = 0;
                    while (c.State == MovementState.Slide && ticks++ < 600)
                        c.Step(In(mz: 1f, crouch: true), cfg.FixedDelta);
                    Run(c, 60, In(mz: 1f, crouch: true));   // 继续 W+Ctrl
                    Check("18b. 滑铲结束 + W+Ctrl → 蹲走（速度回到 2.0 m/s 档）",
                        c.State == MovementState.Ground &&
                        MathF.Abs(c.GroundPlaneSpeed - cfg.CrouchWalkSpeed) < 0.1f &&
                        c.CrouchAmount > 0.9f,
                        $"state={c.State} 速度={c.GroundPlaneSpeed:F3}（蹲走 {cfg.CrouchWalkSpeed:F2}）");
                }
            }

            // 19) 门槛是硬边界：4.9 不起滑
            {
                var world = new AnalyticWorld(0f);
                var c = MakeCore(world, cfg);
                c.Reset(new F3(0f, 1f, 0f));
                Settle(c);
                c.SetVelocity(new F3(0f, 0f, -4.9f));    // 略低于门槛
                c.Step(In(mz: 1f, crouch: true), cfg.FixedDelta);
                Check("19. 4.9 m/s（低于门槛 5.0）→ 不起滑",
                    c.State != MovementState.Slide,
                    $"state={c.State} 速度={c.GroundPlaneSpeed:F2}");
            }
        }
        // 20) boost 的坡度投影：需求方给的算例（30°、√3 ⇒ 2.0）
        {
            float b30 = ApexMovementCore.SlideBoostForSlope(MathF.Sqrt(3f), MathF.PI / 6f);
            float b0 = ApexMovementCore.SlideBoostForSlope(MathF.Sqrt(3f), 0f);
            float b60 = ApexMovementCore.SlideBoostForSlope(1f, MathF.PI / 2f);   // 夹紧到 60°
            Check("20. boost 按坡度投影：30° 坡 √3 → 2.0（1/cosθ）；平地不变；≥60° 夹紧",
                MathF.Abs(b30 - 2f) < 1e-4f &&
                MathF.Abs(b0 - MathF.Sqrt(3f)) < 1e-4f &&
                MathF.Abs(b60 - 2f) < 1e-4f,
                $"30°→{b30:F4}（需求例子 2.0）；0°→{b0:F4}；90°按60°夹紧→{b60:F4}");
        }
        sb.AppendLine($"合计 {pass + fail} 项断言，失败 {fail} 项");
        return (fail == 0, sb.ToString());
    }
}
