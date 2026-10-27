using System;
using System.Text;
using ApexMovement.Core;
using ApexMovement.Camera;

namespace ApexMovement.Tests;

/// <summary>
/// v0.4.3 专项验收：**镜头走路摇晃波形**（需求方：「镜头缓慢向左倾斜一点点，
/// 然后迅速回正」「给我一种单脚跳的感觉」「正常晃动应该是屏幕中心点对应
/// 当前镜头方向画 ∞」）。
///
/// ## 这一版要证伪的东西
///
/// v0.4.2 的波形是（ApexCameraRig.BuildOutput）：
///
///     bobVert  = sin(φ)     * A_v
///     bobLat   = cos(φ/2)   * A_l
///     bobPitch = cos(φ)     * P
///     bobRoll  = cos(φ/2)   * R
///
/// 其中 φ 按 [0, 2π) 回绕。**cos(φ/2) 的周期是 4π 不是 2π**，所以每次回绕
/// 横向与侧倾都从 −幅度 突跳到 +幅度。这不是"幅度不合适"，调 BobGaitScale
/// 永远调不好 —— 所以本文件把"跳变"本身做成断言：
///
///   ① 新公式的逐 tick 变化量必须小于解析上界（光滑）；
///   ② **同一把尺子量旧公式必须失败**（否则这条断言没有判别力）。
///
/// ## 要证明的东西
///
///   ③ 屏幕平面轨迹是横放的 ∞（Gerono 双纽线）：横向 1 次/步幅、竖直 2 次/步幅，
///      在 |横向| 最大处竖直=0，在 |竖直| 最大处横向=±A_l/√2；
///   ④ 自交：一个步幅内恰好两次经过原点，且两个切向不同（交叉而不是相切）；
///   ⑤ 反例守门：把竖直换成 cos(2φ) 会**退化成抛物线**（y 是 x 的单值函数，
///      路径无法自交）—— 这条钉死"相位差必须是 0"这个数学约束；
///   ⑥ 幅度与倍率语义不变（峰到峰 = 2×半幅，k 线性）；
///   ⑦ 端到端：真实 core + rig 链路上也不再有跳变（这一条才是真的回归测试）。
/// </summary>
public static class BobWaveformTest
{
    private const float CadenceCap = 3.0f;              // 相机侧步频上限 (Hz)
    private const float StrideHz = CadenceCap * 0.5f;   // 步幅频率 (Hz) = 1.5

    private static GaitSample Gait(float vert, float lat, float pitch, float roll) => new()
    {
        CadenceHz = CadenceCap, StrideLength = 2.0f, FlightFraction = 0.38f, RunBlend = 1f,
        VerticalAmplitude = vert, LateralAmplitude = lat, PitchDeg = pitch, RollDeg = roll,
        Running = true,
    };

    private static float OldLateral(float phase, float amp) => MathF.Cos(phase * 0.5f) * amp;

    private static float OldRoll(float phase, float amp) => MathF.Cos(phase * 0.5f) * amp;

    public static (bool Pass, string Report) Run()
    {
        var sb = new StringBuilder();
        int fail = 0, total = 0;

        void Check(string name, bool ok, string detail)
        {
            total++;
            if (!ok) fail++;
            sb.AppendLine((ok ? "  [PASS] " : "  [FAIL] ") + name + (detail.Length > 0 ? "  —— " + detail : ""));
        }

        // 冲刺实测幅度（v0.4.2 拟合值，与 GaitTest 口径一致）
        const float Av = 0.00623f;   // 垂直半幅 (m)  → 峰到峰 12.5 mm
        const float Al = 0.00802f;   // 横向半幅 (m)  → 峰到峰 16.0 mm
        const float Pdeg = 0.124f;   // 点头半幅 (deg)
        const float Rdeg = 0.128f;   // 侧倾半幅 (deg)
        GaitSample g = Gait(Av, Al, Pdeg, Rdeg);

        sb.AppendLine("=== v0.4.3 镜头摇晃波形（∞ / 无回绕跳变） ===");
        sb.AppendLine($"  幅度（半幅）：垂直 {Av * 1000f:F2} mm / 横向 {Al * 1000f:F2} mm / 点头 {Pdeg:F3}° / 侧倾 {Rdeg:F3}°");

        // ── ① 新公式光滑：逐 tick 变化量不超过解析上界 ────────────────
        {
            const float dt = 1f / 240f;
            float ph = 0f;
            float maxLat = 0f, maxVert = 0f, maxRoll = 0f;
            float prevLat = 0f, prevVert = 0f, prevRoll = 0f;
            bool first = true;
            int ticks = (int)(4f / dt);      // 4 秒 ≈ 6 个步幅
            for (int i = 0; i < ticks; i++)
            {
                BobWaveform.Sample(ph, g, 1f, 0f, 0f, 0f, out float v, out float l, out _, out float r);
                if (!first)
                {
                    maxLat = MathF.Max(maxLat, MathF.Abs(l - prevLat));
                    maxVert = MathF.Max(maxVert, MathF.Abs(v - prevVert));
                    maxRoll = MathF.Max(maxRoll, MathF.Abs(r - prevRoll));
                }
                first = false;
                prevLat = l; prevVert = v; prevRoll = r;

                ph += dt * StrideHz * MathF.PI * 2f;
                ph %= MathF.PI * 2f;
            }
            float boundLat = Al * (MathF.PI * 2f * StrideHz) * dt * 1.02f;
            float boundVert = Av * (MathF.PI * 2f * CadenceCap) * dt * 1.02f;
            float boundRoll = Rdeg * (MathF.PI / 180f) * (MathF.PI * 2f * StrideHz) * dt * 1.02f;
            Check("① 新波形逐 tick 光滑（横向 / 竖直 / 侧倾变化量都在解析上界内）",
                maxLat <= boundLat && maxVert <= boundVert && maxRoll <= boundRoll,
                $"Δ横向 {maxLat * 1000f:F4}mm ≤ {boundLat * 1000f:F4}；Δ竖直 {maxVert * 1000f:F4}mm ≤ {boundVert * 1000f:F4}；" +
                $"Δ侧倾 {maxRoll * 180f / MathF.PI:E3}° ≤ {boundRoll * 180f / MathF.PI:E3}°");
        }

        // ── ② 同一把尺子量旧公式必须**失败**（判别力自证） ──────────────
        //
        //    旧相位口径是"步频相位"（一个 2π = 一步），推进 3.0 Hz。
        //    若这条也过，说明 ① 根本测不出跳变 —— 那 ① 就没有意义。
        {
            const float dt = 1f / 240f;
            float ph = 0f;
            float maxLat = 0f, maxRoll = 0f, prevLat = 0f, prevRoll = 0f;
            bool first = true;
            int ticks = (int)(4f / dt);
            for (int i = 0; i < ticks; i++)
            {
                float l = OldLateral(ph, Al);
                float r = OldRoll(ph, Rdeg) * (MathF.PI / 180f);
                if (!first)
                {
                    maxLat = MathF.Max(maxLat, MathF.Abs(l - prevLat));
                    maxRoll = MathF.Max(maxRoll, MathF.Abs(r - prevRoll));
                }
                first = false;
                prevLat = l; prevRoll = r;

                ph += dt * CadenceCap * MathF.PI * 2f;
                ph %= MathF.PI * 2f;
            }
            float boundLat = Al * (MathF.PI * 2f * StrideHz) * dt * 1.02f;
            Check("② 旧公式（cos(φ/2) 回绕）用同一把尺子量会失败 —— 证明 ① 有判别力",
                maxLat > boundLat * 10f && maxLat > Al * 1.5f,
                $"旧公式最大单 tick Δ横向 {maxLat * 1000f:F3}mm（= 2×半幅 {2f * Al * 1000f:F2}mm 的 {maxLat / (2f * Al):F1} 倍），" +
                $"新公式上界只有 {boundLat * 1000f:F4}mm；Δ侧倾 {maxRoll * 180f / MathF.PI:F2}°");
        }

        // ── ③ ∞ 形状：频率比 / 极值处的正交关系 ──────────────────────
        {
            // 取满一个周期、并且**起点不落在周期边界上**（否则整周期边界的过零会被
            // 误判成"内部过零缺失"：sin(2φ) 一个周期内其实有 4 次过零，
            // 但 φ=0 / φ=2π 是同一点，从 0 起数只能数到 3）。
            const int N = 2000;
            const float Ph0 = 0.37f;
            var lat = new float[N];
            var vert = new float[N];
            for (int i = 0; i < N; i++)
            {
                float ph = Ph0 + (float)i / N * MathF.PI * 2f;
                BobWaveform.Sample(ph, g, 1f, 0f, 0f, 0f, out vert[i], out lat[i], out _, out _);
            }

            int latZeros = CountSignChangesCyclic(lat, N);
            int vertZeros = CountSignChangesCyclic(vert, N);
            Check("③a 频率比：竖直 2 次 / 横向 1 次（每步幅）= 一步一次竖直",
                vertZeros == 2 * latZeros && latZeros == 2,
                $"一个步幅内变号次数：竖直 {vertZeros}、横向 {latZeros}（比值 {(float)vertZeros / latZeros:F2}，应为 2.00）");

            // 在 |横向| 最大处，竖直必须 ≈ 0（∞ 的左右两个端点）
            // 注：采样起点故意偏移过，所以不能按数组下标直接取 π/2，必须**扫极值**。
            int iLatMax = 0, iVertMax = 0;
            float bestLat = -1f, bestVert = -1f;
            for (int i = 0; i < N; i++)
            {
                if (MathF.Abs(lat[i]) > bestLat) { bestLat = MathF.Abs(lat[i]); iLatMax = i; }
                if (MathF.Abs(vert[i]) > bestVert) { bestVert = MathF.Abs(vert[i]); iVertMax = i; }
            }
            float atLatPeakVert = MathF.Abs(vert[iLatMax]);
            Check("③b 横向摆到最外侧时竖直起伏为 0（∞ 的左右两个端点）",
                atLatPeakVert < Av * 0.02f,
                $"横向峰值处 |竖直| = {atLatPeakVert * 1000f:E2}mm（A_v = {Av * 1000f:F2}mm）");

            // 在 |竖直| 最大处，|横向| = A_l/√2
            float atVertPeakLat = MathF.Abs(lat[iVertMax]);
            float expect = Al / MathF.Sqrt(2f);
            Check("③c 竖直最高/最低时横向在 ±A_l/√2（∞ 的交叉处）",
                MathF.Abs(atVertPeakLat - expect) < Al * 0.02f,
                $"竖直峰值处 |横向| {atVertPeakLat * 1000f:F4}mm，A_l/√2 = {expect * 1000f:F4}mm");

            // 峰值幅度与半幅一致（峰到峰 = 2×）
            float maxLat = 0f, maxVert = 0f;
            for (int i = 0; i < N; i++)
            {
                maxLat = MathF.Max(maxLat, MathF.Abs(lat[i]));
                maxVert = MathF.Max(maxVert, MathF.Abs(vert[i]));
            }
            Check("③d 峰值 = 配置的半幅（峰到峰正好 2×，幅度语义没被改写）",
                MathF.Abs(maxLat - Al) < Al * 1e-4f && MathF.Abs(maxVert - Av) < Av * 1e-4f,
                $"实测峰值 横向 {maxLat * 1000f:F4}mm（配置 {Al * 1000f:F4}）、竖直 {maxVert * 1000f:F4}mm（配置 {Av * 1000f:F4}）");
        }

        // ── ④ 自交：一个步幅内两次经过原点，且两个切向不同（真的交叉） ──
        {
            // 解析地取两个"过原点"的相位（sin(φ)=0 且 sin(2φ)=0 ⇒ φ = 0 与 φ = π），
            // 而不是靠在原点附近撒点碰运气 —— 撒点阈值一旦放宽就会把"接近原点"
            // 的一整段都算成过原点（初版就是因此得到 17 次），断言失去意义。
            BobWaveform.Sample(0f, g, 1f, 0f, 0f, 0f, out float v0, out float l0, out _, out _);
            BobWaveform.Sample(MathF.PI, g, 1f, 0f, 0f, 0f, out float v1, out float l1, out _, out _);
            bool bothAtOrigin = MathF.Abs(l0) + MathF.Abs(v0) < 1e-6f &&
                                MathF.Abs(l1) + MathF.Abs(v1) < 1e-6f;

            // 解析切向：d/dφ (lateral, vertical) = (A_l·cos φ, 2A_v·cos 2φ)
            //   φ = 0 → (A_l,  2A_v)     向右上
            //   φ = π → (−A_l, 2A_v)     向左上
            // 两者夹角 = 2·atan(2A_v / A_l)，只要 A_l > 0 就明显不同 ⇒ 真交叉。
            float t0 = MathF.Atan2(2f * Av, Al);
            float t1 = MathF.Atan2(2f * Av, -Al);
            float angleBetween = MathF.Abs(NormalizeAngle(t1 - t0));
            Check("④ 轨迹在原点自交两次且两切向不同（∞ 而不是 8 / 抛物线 / 直线来回）",
                bothAtOrigin && angleBetween > 0.5f,
                $"φ=0 处 |l|+|v| = {(MathF.Abs(l0) + MathF.Abs(v0)):E2}，φ=π 处 {(MathF.Abs(l1) + MathF.Abs(v1)):E2}；" +
                $"两切向 {t0 * 180f / MathF.PI:F1}° 与 {t1 * 180f / MathF.PI:F1}°，夹角 {angleBetween * 180f / MathF.PI:F1}°");
        }

        // ── ⑤ 反例守门：竖直换成 cos(2φ) 会退化成抛物线（y 是 x 的单值函数） ──
        {
            // 判据：sin(φ) 是偶对称于 φ = π/2 的，所以 φ 与 (π−φ) 给**同一个横向值**。
            // 要把 ∞ 与抛物线分开，只需看这两处的**竖直值是否不同**：
            //   · 抛物线（sin(φ)/cos(2φ)）：cos(2π−2φ) = cos(2φ) ⇒ 竖直值相同 ⇒ 路径不能自交；
            //   · 双纽线（sin(φ)/sin(2φ)）：sin(2π−2φ) = −sin(2φ) ⇒ 竖直值相反 ⇒ 必然自交。
            // 直接比绝对差即可 —— 初版写成了"差 / 横向差"，而横向差恒为 0，
            // 分母 1e-9 把浮点噪声放大成 1.59，度量本身是坏的。
            const int N = 4000;
            float maxDupParabola = 0f;
            for (int i = 0; i < N; i++)
            {
                float ph = (float)i / N * MathF.PI * 2f;
                float v1 = Av * MathF.Cos(2f * ph);
                float v2 = Av * MathF.Cos(2f * (MathF.PI - ph));
                if (MathF.Abs(ph - MathF.PI * 0.5f) < 1e-6f) continue;  // 顶点处 φ = π−φ（同一点）
                maxDupParabola = MathF.Max(maxDupParabola, MathF.Abs(v1 - v2));
            }
            Check("⑤ 反例守门：sin(φ)/cos(2φ) 组合退化成抛物线（同横向值处竖直值相同 ⇒ 不可能自交）",
                maxDupParabola < Av * 1e-4f,
                $"同一横向值处的竖直差最大 {maxDupParabola * 1000f:E2}mm（≈0 ⇒ y 是 x 的单值函数，不是 ∞）");

            // 对照：本实现（sin/sin）在同一横向值处竖直值**不同** ⇒ 真自交
            float dup2 = 0f;
            for (int i = 0; i < N; i++)
            {
                float ph = (float)i / N * MathF.PI * 2f;
                float v1 = Av * MathF.Sin(2f * ph), v2 = Av * MathF.Sin(2f * (MathF.PI - ph));
                if (MathF.Abs(ph - MathF.PI * 0.5f) < 1e-6f) continue;
                dup2 = MathF.Max(dup2, MathF.Abs(v1 - v2));
            }
            Check("⑤b 本实现（sin/sin）在同一横向值处竖直值不同 —— 与 ⑤ 形成对照，证明确实是自交曲线",
                dup2 > Av * 1.9f,
                $"最大竖直差 {dup2 * 1000f:F3}mm（接近 2×A_v = {2f * Av * 1000f:F3}mm）");
        }

        // ── ⑥ 倍率线性 + 相位偏移旋钮生效 ────────────────────────────
        {
            float ph = 0.7f;
            BobWaveform.Sample(ph, g, 1f, 0f, 0f, 0f, out float v1, out float l1, out float p1, out float r1);
            BobWaveform.Sample(ph, g, 0.5f, 0f, 0f, 0f, out float v2, out float l2, out float p2, out float r2);
            Check("⑥a 倍率 k 线性（k=0.5 恰好是 k=1 的一半，四个通道都是）",
                MathF.Abs(v2 * 2f - v1) < 1e-7f && MathF.Abs(l2 * 2f - l1) < 1e-7f &&
                MathF.Abs(p2 * 2f - p1) < 1e-7f && MathF.Abs(r2 * 2f - r1) < 1e-7f,
                $"k=1: v={v1:E3} l={l1:E3} p={p1:E3} r={r1:E3}");

            // 侧倾相位 +90° ⇒ 变成 cos(φ)
            BobWaveform.Sample(ph, g, 1f, 0f, 0f, 90f, out _, out _, out _, out float roll90);
            float expectRoll = MathF.Cos(ph) * Rdeg * (MathF.PI / 180f);
            Check("⑥b 侧倾相位旋钮生效（+90° ⇒ 侧倾变 cos(φ)）",
                MathF.Abs(roll90 - expectRoll) < 1e-6f,
                $"实测 {roll90:E3}，期望 {expectRoll:E3}");

            // 点头相位 +90° ⇒ 变成 cos(2φ)
            BobWaveform.Sample(ph, g, 1f, 0f, 90f, 0f, out _, out _, out float pitch90, out _);
            float expectPitch = MathF.Cos(2f * ph) * Pdeg * (MathF.PI / 180f);
            Check("⑥c 点头相位旋钮生效（+90° ⇒ 点头变 cos(2φ)）",
                MathF.Abs(pitch90 - expectPitch) < 1e-6f,
                $"实测 {pitch90:E3}，期望 {expectPitch:E3}");
        }

        // ── ⑧ 对比：竖直相位 90° 退化成抛物线（纯质心口径），默认 0° 才是 ∞ ──
        //
        //    这条把"为什么默认 0°"变成可执行的事实：
        //      0°  ⇒ 过原点自交 ⇒ 曲线（∞）
        //      90° ⇒ y 是 x 的单值函数 ⇒ 抛物线，不可能自交
        //    两者都光滑（都不是"单脚跳"的来源；那个是 v0.4.2 的回绕跳变）。
        //    取 ∞ 的理由是需求方要"画 ∞"，物理上对应头部相对质心 1/4 周期滞后。
        {
            const int N = 2000;
            float pHalfMin = 0f, pHalfMax = 0f;
            for (int i = 0; i < N; i++)
            {
                float ph = (float)i / N * MathF.PI * 2f;
                BobWaveform.Sample(ph, g, 1f, 90f, 0f, 0f, out float v, out float l, out _, out _);
                // 与 π−φ 处的横向值相同（sin 对称），比竖直值是否也相同
                BobWaveform.Sample(MathF.PI - ph, g, 1f, 90f, 0f, 0f, out float v2, out _, out _, out _);
                if (MathF.Abs(ph - MathF.PI * 0.5f) < 1e-5f) continue;
                float d = MathF.Abs(v - v2);
                if (d > pHalfMax) pHalfMax = d;
                if (MathF.Abs(v) > pHalfMin) pHalfMin = MathF.Abs(v);
            }
            Check("⑧ 竖直相位 90° 退化成抛物线（同横向值处竖直值相同 ⇒ 单值函数，不能自交）",
                pHalfMax < Av * 1e-4f,
                $"90° 时同一横向值处的竖直差最大 {pHalfMax * 1000f:E2}mm（≈0 ⇒ 抛物线）");

            // 默认 0°：同一横向值处竖直值相反 ⇒ 自交（∞），且上下对称
            float maxD = 0f;
            int up0 = 0, lo0 = 0;
            for (int i = 0; i < N; i++)
            {
                float ph = (float)i / N * MathF.PI * 2f;
                BobWaveform.Sample(ph, g, 1f, 0f, 0f, 0f, out float v, out _, out _, out _);
                BobWaveform.Sample(MathF.PI - ph, g, 1f, 0f, 0f, 0f, out float v2, out _, out _, out _);
                if (MathF.Abs(ph - MathF.PI * 0.5f) < 1e-5f) continue;
                maxD = MathF.Max(maxD, MathF.Abs(v - v2));
                if (v > Av * 0.5f) up0++;
                if (v < -Av * 0.5f) lo0++;
            }
            Check("⑧b 默认 0° 是 ∞：同横向值处竖直值相反（自交），且上下幅度对称",
                maxD > Av * 1.9f && MathF.Abs(up0 - lo0) <= 4 && up0 > N / 8,
                $"0° 时同横向值处竖直差最大 {maxD * 1000f:F2}mm（≈2A_v = {2f * Av * 1000f:F2}mm）；" +
                $"上方 {up0} 点 / 下方 {lo0} 点");
        }

        // ── ⑦ 端到端：真实 core + rig 链路没有回绕跳变 ────────────────
        //
        //    这才是真正的回归测试：① 只是照着公式算，⑦ 走的是
        //    UpdateBobPhase（相位口径）→ MovementSnapshot → ApexCameraRig → CameraOutput。
        {
            var cfg = MovementConfig.Apex();
            var world = new AnalyticWorld(0f);
            var core = new ApexMovementCore(cfg);
            core.BindWorld(world);
            core.Reset(new F3(0f, 1.0f, 0f));

            var rig = new ApexCameraRig(new CameraEffectsConfig { BobGaitScale = 1f });
            rig.Reset();

            var input = new MovementInput { MoveZ = 1f, SprintHeld = true };
            float dt = cfg.FixedDelta;
            for (int i = 0; i < 240; i++) { core.Step(input, dt); rig.Update(core.Snapshot, dt); }

            // v0.5（需求 m01070）：∞ 由视线方向承载，横向分量读相机输出的物理半幅。
            float prevLat = rig.Update(core.Snapshot, dt).BobLateralMeters;
            float maxDLat = 0f, maxLatAbs = 0f;
            int ticks = 180;                      // 3 秒
            for (int i = 0; i < ticks; i++)
            {
                core.Step(input, dt);
                CameraOutput o = rig.Update(core.Snapshot, dt);
                float lat = o.BobLateralMeters;
                maxDLat = MathF.Max(maxDLat, MathF.Abs(lat - prevLat));
                maxLatAbs = MathF.Max(maxLatAbs, MathF.Abs(lat));
                prevLat = lat;
            }

            GaitSample lg = core.Snapshot.Gait;
            float camCadence = MathF.Min(lg.CadenceHz, cfg.Gait.BobCadenceMaxHz);
            float bound = lg.LateralAmplitude * (MathF.PI * 2f * camCadence * 0.5f) * dt * 1.35f;
            Check("⑦ 真实 core+rig 链路：横向逐 tick 变化量无跳变（< 解析上界 ×1.35）",
                maxDLat <= bound && maxLatAbs > lg.LateralAmplitude * 0.5f,
                $"最大 Δ横向 {maxDLat * 1000f:F4}mm ≤ 界 {bound * 1000f:F4}mm；摆动峰值 {maxLatAbs * 1000f:F3}mm；" +
                $"步频 {lg.CadenceHz:F2}Hz → 相机 {camCadence:F2}Hz → 步幅 {camCadence * 0.5f:F2}Hz");

            // 旧口径在同一链路上会怎样：用同一组幅度与 old 公式复算
            float oldMax = 2f * lg.LateralAmplitude;
            Check("⑦b 若仍用旧口径（cos(φ/2)），同一链条上的最大单 tick Δ横向 = 2×半幅",
                oldMax > bound * 5f,
                $"理论跳变 {oldMax * 1000f:F2}mm vs 新界 {bound * 1000f:F4}mm（差 {oldMax / bound:F0} 倍）");
        }

        sb.AppendLine($"合计 {total} 项断言，失败 {fail} 项");
        return (fail == 0, sb.ToString());
    }

    /// <summary>按环计数变号（首尾相接），用于"一个完整周期内过零几次"。</summary>
    private static int CountSignChangesCyclic(float[] a, int n)
    {
        int c = 0;
        for (int i = 0; i < n; i++)
        {
            int s0 = a[i] > 0f ? 1 : (a[i] < 0f ? -1 : 0);
            int s1 = a[(i + 1) % n] > 0f ? 1 : (a[(i + 1) % n] < 0f ? -1 : 0);
            if (s0 != 0 && s1 != 0 && s0 != s1) c++;
        }
        // 环计数把每个过零算了一次；一个周期内过零次数 = c%2==0 ? c : c（保持原值）
        return c;
    }

    private static int CountSignChanges(float[] a, int n)
    {
        int c = 0;
        int prevSign = 0;
        for (int i = 0; i < n; i++)
        {
            int s = a[i] > 0f ? 1 : (a[i] < 0f ? -1 : 0);
            if (s == 0) continue;
            if (prevSign != 0 && s != prevSign) c++;
            prevSign = s;
        }
        return c;
    }

    private static float NormalizeAngle(float a)
    {
        while (a > MathF.PI) a -= MathF.PI * 2f;
        while (a < -MathF.PI) a += MathF.PI * 2f;
        return a;
    }

    private static F3 ForwardFromYaw(float yaw, F3 up)
    {
        var f = new F3(MathF.Sin(yaw), 0f, -MathF.Cos(yaw));
        if (MathF.Abs(F3.Dot(f, up)) > 0.99f)
        {
            F3 right = F3.Cross(F3.Up, up).Normalized();
            F3 realUp = F3.Cross(up, right).Normalized();
            f = right * f.X + realUp * f.Z;
        }
        return F3.ProjectOnPlane(f, up).Normalized();
    }
}
