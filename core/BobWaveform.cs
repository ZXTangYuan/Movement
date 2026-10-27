using System;

namespace ApexMovement.Core;

/// <summary>
/// 步态摇晃波形（v0.4.3 新增）：把「步幅相位 + 步态幅度」变成镜头位移与旋转量。
///
/// ## 为什么要有这个文件
///
/// v0.4.2 及之前，波形直接写在 <c>ApexCameraRig.BuildOutput</c> 里，是四行：
///
///     float sinP     = MathF.Sin(s.BobPhase);
///     float cosP     = MathF.Cos(s.BobPhase);
///     float cosHalfP = MathF.Cos(s.BobPhase * 0.5f);
///     bobVert  = sinP     * VerticalAmplitude * k;
///     bobLat   = cosHalfP * LateralAmplitude  * k;
///     bobPitch = cosP     * PitchDeg * k;
///     bobRoll  = cosHalfP * RollDeg  * k;
///
/// 这里藏着一个**相位回绕造成的跳变**（需求方原话：「镜头缓慢向左倾斜一点点，
/// 然后迅速回正」「单脚跳的感觉」）：
///
///   · 相位 _bobPhase 在 <c>ApexMovementCore.UpdateBobPhase</c> 里按 [0, 2π) 回绕；
///   · cos(φ/2) 的周期是 4π 而不是 2π —— 它**不是** φ 的周期函数；
///   · 于是在回绕点（φ: 2π⁻ → 0⁺）上，cos(φ/2) 从 cos(π) = −1 突变成 cos(0) = +1；
///   · 侧向位移与侧倾各自瞬间跳 2×幅度，频率正好是**每步一次**（冲刺时约 3 次/秒）。
///
/// 而 sin(φ) / cos(φ) 的周期是 2π，在回绕点连续 —— 所以只有横向与侧倾在跳，
/// 竖直方向是好的。玩家看到的就是「横向慢慢漂过去，然后瞬间弹回来」，
/// 主观上像单脚跳。**这不是幅度问题，调 VerticalScale / LateralMax 永远不会好。**
///
/// ## 正确做法：让它真的画一个 ∞
///
/// 步态的真实频率关系：
///
///   · **竖直起伏**：每**步**一次（过支撑腿时最高）⇒ 每个步幅 2 次；
///   · **左右摆动**：每**步幅**一次（左右各一步才摆满一个来回）⇒ 每个步幅 1 次。
///
/// 即竖直 : 横向 = 2 : 1。取相位 φ 的周期 = **一个步幅**，直接写：
///
///     lateral  = A_lat  · sin(φ)
///     vertical = A_vert · sin(2φ)
///
/// 这正是 Gerono 双纽线 ((x, y) = (A sin φ, B sin 2φ)) —— 屏幕平面上一个横放的
/// ∞：两个零点在中心交叉，处处光滑，没有任何回绕跳变。
///
/// ⚠ 竖直通道的相位必须是 0 而不是 90°：若写成 vertical = B·cos(2φ)，由
/// cos 2φ = 1 − 2sin²φ 可得 y = B·(1 − 2(x/A)²) —— 那是一条**抛物线**，
/// 不是 ∞（y 对 x 单值，路径不可能自交）。这条约束由
/// <c>tests/BobWaveformTest.cs</c> 的「自交 / 反例守门」断言守住。
///
/// ## 关于"哪个才是物理正确的"
///
/// 严格按**质心**轨迹推导：左右摆到最外侧时正好在支撑腿正上方、重心最高，
/// 也就是竖直与横向**同相**。取 lateral = A·sin φ，则
/// vertical = −B·cos 2φ = B·(2sin²φ − 1)，代回去得
/// <c>y = B·(2(x/A)² − 1)</c> —— 一条**开口向上的抛物线**（两侧高、中间低），
/// 不是 ∞。这条抛物线在帧间同样光滑，但它没有"中心交叉"，观感是"每步往下沉两次"。
///
/// ∞ 对应的物理图像是**头部相对质心有约 1/4 周期（90°）滞后**：颈部与躯干是
/// 有阻尼的弹簧-质量系统，在步行频率附近本来就明显滞后于质心。此时竖直在
/// 横向到达极值处正好过零，轨迹就从抛物线变成双纽线。这也是各家第一人称
/// 游戏做 head bob 的通用形状，且正是需求方要的「画 ∞」。
/// 因此默认取 ∞（<see cref="BobVerticalPhaseDeg"/> = 0）；
/// 想看纯质心口径（抛物线）可以调到 90° 对比。
///
/// ⚠ 注意：需求方抱怨的「单脚跳」**不是**这条抛物线造成的 —— 那是 v0.4.2 的
/// 回绕跳变（横向不连续）。本文件解决的是那个。
///
/// ## 幅度语义（与 GaitModel 一致，不要改）
///
/// <c>VerticalAmplitude</c> / <c>LateralAmplitude</c> / <c>PitchDeg</c> /
/// <c>RollDeg</c> 全部是**半幅**：峰到峰 = 2×。所以
/// sin 波乘上去之后峰到峰正好是 2×幅度，与文档里的「峰到峰 mm/度」口径一致。
/// </summary>
public static class BobWaveform
{
    private const float Deg2Rad = MathF.PI / 180f;

    /// <summary>
    /// 采样一个步幅相位处的摇晃量。
    /// </summary>
    /// <param name="phase">步幅相位 (rad)。2π = 一个完整步幅 = 左右各一步。</param>
    /// <param name="gait">步态样本（提供四个半幅）。</param>
    /// <param name="scale">总倍率（1 = 步态模型推导出的物理值，0 = 全关）。</param>
    /// <param name="verticalPhaseDeg">
    /// 竖直通道的相位偏移 (deg)。**0 = ∞（默认，需求方要的形状）**；
    /// 90 = 退化成抛物线（= 纯质心口径，见类注释）。
    /// </param>
    /// <param name="pitchPhaseDeg">点头相对竖直起伏的相位偏移 (deg)，0 = 同相。</param>
    /// <param name="rollPhaseDeg">侧倾相对左右摆动的相位偏移 (deg)，0 = 同相。</param>
    /// <param name="vertical">竖直位移 (m)，正 = 向上。</param>
    /// <param name="lateral">横向位移 (m)，正 = 右手方向。</param>
    /// <param name="pitchRad">点头角 (rad)。</param>
    /// <param name="rollRad">侧倾角 (rad)。</param>
    public static void Sample(
        float phase, in GaitSample gait, float scale,
        float verticalPhaseDeg, float pitchPhaseDeg, float rollPhaseDeg,
        out float vertical, out float lateral, out float pitchRad, out float rollRad)
    {
        // swing：1 周期 / 步幅（左右摆动）；bob：2 周期 / 步幅（= 一步一次）。
        float swing = MathF.Sin(phase);
        float bob = MathF.Sin(2f * phase + verticalPhaseDeg * Deg2Rad);

        vertical = bob * gait.VerticalAmplitude * scale;
        lateral = swing * gait.LateralAmplitude * scale;

        pitchRad = MathF.Sin(2f * phase + pitchPhaseDeg * Deg2Rad) * gait.PitchDeg * scale * Deg2Rad;
        rollRad = MathF.Sin(phase + rollPhaseDeg * Deg2Rad) * gait.RollDeg * scale * Deg2Rad;
    }
}
