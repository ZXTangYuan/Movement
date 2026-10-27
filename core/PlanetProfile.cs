using System;

namespace ApexMovement.Core;

/// <summary>
/// 星球档案（v0.5 新增）—— 需求方 m00818：
///
/// 「我希望保留各种效果的情况下将常规系数定为真实地球量…… 不只是重力，
///  其他物理规则同理，设为真实地球的量，同时可调，然后以保留效果的情况下
///  反推修改特殊量…… 我要设计不同的星球。」
///
/// 分工原则（这是整个 v0.5 的核心约定）：
///
///   · **常规系数**（本文件）＝ 物理世界的客观属性：表面重力、空气密度……
///     它们是**可切换的**，切星球只改这里。
///   · **设计目标**（<see cref="MovementConfig"/> 里带 [Target] 记号的量）＝
///     玩家能感知的效果：跳跃高度、奔跑速度、滑铲加成……
///     它们**不随星球变**。
///   · **特殊量**（<see cref="MovementConfig"/> 里的派生属性）＝ 由上面两者
///     用物理公式算出来的：JumpVelocity = √(2·g·h)、滑铲坡度加速度
///     = g·sinθ − μ·g·cosθ…… 它们**永远不许手调**。
///
/// 于是「地球重力 + 跳 1 米」自动得到 v = √(2×9.80665×1) = 4.429 m/s；
/// 换成月球只需要把星球档案换成 <see cref="Moon"/>，跳跃高度仍然是 1 米，
/// 但初速变成 1.800 m/s、滞空变成 2.47 秒 —— 这正是「低重力星球」该有的样子。
///
/// 注意：**重力加速度是矢量**。本类只给**大小**；方向由
/// <c>ApexMovementCore</c> 的 up 向量承载（<c>SetUpDirection</c>），
/// 这样将来接入球形行星时，每 tick 传一次行星局部 up 即可，
/// 移动/跳跃/滑铲的方向全部自动跟着转。
/// </summary>
public readonly struct PlanetProfile
{
    /// <summary>星球名（只用于日志/HUD）。</summary>
    public readonly string Name;

    /// <summary>表面重力加速度大小 (m/s²) —— 常规系数。</summary>
    public readonly float SurfaceGravity;

    /// <summary>海平面空气密度 (kg/m³) —— 预留给未来的空气阻力/落地风噪。</summary>
    public readonly float AirDensity;

    public PlanetProfile(string name, float surfaceGravity, float airDensity)
    {
        if (!float.IsFinite(surfaceGravity) || surfaceGravity <= 0f)
            throw new ArgumentOutOfRangeException(nameof(surfaceGravity), "表面重力必须 > 0");
        if (!float.IsFinite(airDensity) || airDensity < 0f)
            throw new ArgumentOutOfRangeException(nameof(airDensity), "空气密度必须 ≥ 0");
        Name = name;
        SurfaceGravity = surfaceGravity;
        AirDensity = airDensity;
    }

    // ── 预置星球 ────────────────────────────────────────────────

    /// <summary>地球：g = 9.80665 m/s²，空气密度 1.225 kg/m³。**v0.5 起的默认**。</summary>
    public static PlanetProfile Earth => new("Earth", 9.80665f, 1.225f);

    /// <summary>月球：g = 1.62 m/s²，无大气。</summary>
    public static PlanetProfile Moon => new("Moon", 1.62f, 0f);

    /// <summary>火星：g = 3.71 m/s²，稀薄大气 0.02 kg/m³。</summary>
    public static PlanetProfile Mars => new("Mars", 3.711f, 0.02f);

    /// <summary>
    /// Apex 原版等效值 g = 20.7802 m/s²（由 wiki 的 h=56hu、t=0.74s 反解）。
    /// 保留它只为**兼容/对比**：v0.4.x 的全部手感数字都是在这颗"重星球"上标定的。
    /// </summary>
    public static PlanetProfile ApexDefault => new("Apex-like", 20.7802f, 1.225f);

    public override string ToString() =>
        $"{Name}(g={SurfaceGravity:F4} m/s², rho={AirDensity:F3} kg/m³)";
}
