using Godot;

namespace ApexMovement;

/// <summary>
/// Apex 风格移动的调参表（纯数据，无逻辑）。
///
/// **参数来源**：参数名逐条取自 Apex Legends <c>r5apex_dx12.exe</c>（v3.0.3.65,
/// SHA256 b78dc5b0…）保留的 Source 2 / Titanfall 移动符号表（<c>sv_*</c>、
/// <c>slide*</c>、<c>wallrun*</c>、<c>airStrafe*</c>、<c>player_doubleJump*</c> 等）。
/// 二进制保留名称但不保留数值，因此**数值为按符号语义 + Apex 手感重建**（非原版精确值）。
/// 单位统一为**公制**（米 / 秒 / 米·秒⁻²），与 ProjectA 的球面重力一致。
///
/// 速度类参数对照：Apex/Source 内部用「单位/秒」，此处按 1 单位 ≈ 0.0254 m 折算。
/// </summary>
[GlobalClass]
public partial class ApexMoveConfig : Resource
{
    // ───────────────────────── 地面移动 ─────────────────────────

    /// <summary>行走最大速度（m/s）。对应 Apex 基础 run speed。</summary>
    [Export] public float MaxSpeed { get; set; } = 7.6f;

    /// <summary>冲刺最大速度（m/s）。对应 sprintspeed / sprint 倍率。</summary>
    [Export] public float SprintSpeed { get; set; } = 9.5f;

    /// <summary>地面加速系数（无量纲，Source 语义 sv_accelerate）。</summary>
    [Export] public float GroundAccelerate { get; set; } = 10.0f;

    /// <summary>地面摩擦系数（无量纲，对应 sv_friction）。</summary>
    [Export] public float Friction { get; set; } = 4.8f;

    /// <summary>停止速度（m/s，对应 sv_stopspeed）：低于它时摩擦按此值封顶，避免抖动。</summary>
    [Export] public float StopSpeed { get; set; } = 2.54f;

    /// <summary>默认表面摩擦倍率（对应 m_surfaceFriction）。</summary>
    [Export] public float SurfaceFriction { get; set; } = 1.0f;

    // ───────────────────────── 空中移动 ─────────────────────────

    /// <summary>空中加速系数（无量纲，对应 sv_airaccelerate）。</summary>
    [Export] public float AirAccelerate { get; set; } = 12.0f;

    /// <summary>空中加速的速度上限（m/s）：限制 wishspeed，保留 Source「空中急转」手感。</summary>
    [Export] public float AirSpeedCap { get; set; } = 1.0f;

    /// <summary>空气阻力系数（对应 airDrag）。</summary>
    [Export] public float AirDrag { get; set; } = 0.0f;

    /// <summary>空中平移加速系数（对应 airStrafeAcceleration）。</summary>
    [Export] public float AirStrafeAcceleration { get; set; } = 12.0f;

    /// <summary>空中平移加速的衰减起点（0..1，对应 airStrafeTaperStart）。</summary>
    [Export] public float AirStrafeTaperStart { get; set; } = 0.5f;

    /// <summary>空中平移加速的衰减终点（0..1，对应 airStrafeTaperFinish）。</summary>
    [Export] public float AirStrafeTaperFinish { get; set; } = 0.9f;

    /// <summary>最大水平速度硬上限（m/s，对应 sv_maxspeed 的物理上限）。</summary>
    [Export] public float MaxVelocity { get; set; } = 60.0f;

    // ───────────────────────── 跳跃 ─────────────────────────

    /// <summary>起跳高度（m）。速度由 v = sqrt(2·g·h) 导出（对应 jumpHeight）。</summary>
    [Export] public float JumpHeight { get; set; } = 1.6f;

    /// <summary>双跳高度（m，对应 doubleJump 高度）。</summary>
    [Export] public float DoubleJumpHeight { get; set; } = 1.3f;

    /// <summary>双跳空中控制系数（对应 player_doubleJumpAirControl）。</summary>
    [Export] public float DoubleJumpAirControl { get; set; } = 0.5f;

    /// <summary>允许的空中跳跃次数（Apex 为 1 次双跳）。</summary>
    [Export] public int MaxAirJumps { get; set; } = 1;

    /// <summary>蹲伏时是否可跳（对应 canJumpWhileCrouched）。</summary>
    [Export] public bool CanJumpWhileCrouched { get; set; } = true;

    /// <summary>跳跃输入缓冲时间（s）：落地前按下仍生效，手感更跟手。</summary>
    [Export] public float JumpBufferTime { get; set; } = 0.12f;

    /// <summary>起跳锁定时间（s）：起跳后短暂忽略接地探测，保证真正离地（防止探测距离内的地面把玩家粘住）。</summary>
    [Export] public float JumpLockTime { get; set; } = 0.15f;

    // ───────────────────────── 蹲伏 ─────────────────────────

    /// <summary>蹲伏时碰撞高度比例（对应 crouchFraction）。</summary>
    [Export] public float CrouchHeightFraction { get; set; } = 0.5f;

    /// <summary>蹲伏时最大速度比例。</summary>
    [Export] public float CrouchSpeedScale { get; set; } = 0.5f;

    /// <summary>蹲伏过渡速度（1/s）。</summary>
    [Export] public float CrouchTransitionSpeed { get; set; } = 10.0f;

    // ───────────────────────── 滑铲（Apex 招牌） ─────────────────────────

    /// <summary>起滑所需最小速度（m/s，对应 slideRequiredStartSpeed）。</summary>
    [Export] public float SlideRequiredStartSpeed { get; set; } = 6.0f;

    /// <summary>空中起滑所需最小速度（m/s，对应 slideRequiredStartSpeedAir）。</summary>
    [Export] public float SlideRequiredStartSpeedAir { get; set; } = 8.0f;

    /// <summary>滑铲加速度（对应 slideAccel）。</summary>
    [Export] public float SlideAccel { get; set; } = 70.0f;

    /// <summary>滑铲加速中前向分量占比（对应 slideAccelForwardFrac）。</summary>
    [Export] public float SlideAccelForwardFrac { get; set; } = 1.0f;

    /// <summary>滑铲加速中侧向分量占比（对应 slideAccelSideFrac）。</summary>
    [Export] public float SlideAccelSideFrac { get; set; } = 0.3f;

    /// <summary>滑铲减速度（对应 slideDecel）。</summary>
    [Export] public float SlideDecel { get; set; } = 8.0f;

    /// <summary>滑铲结束速度阈值（对应 slideStopSpeed）。</summary>
    [Export] public float SlideStopSpeed { get; set; } = 2.0f;

    /// <summary>滑铲最大停止速度（对应 slideMaxStopSpeed）。</summary>
    [Export] public float SlideMaxStopSpeed { get; set; } = 3.0f;

    /// <summary>滑铲速度衰减（对应 slideVelocityDecay）。</summary>
    [Export] public float SlideVelocityDecay { get; set; } = 0.6f;

    /// <summary>滑铲重力缩放（对应 slideGravityScale）。</summary>
    [Export] public float SlideGravityScale { get; set; } = 0.35f;

    /// <summary>下坡滑铲重力倍率（对应 slideGravityDownhillMultiplier）。</summary>
    [Export] public float SlideGravityDownhillMultiplier { get; set; } = 2.2f;

    /// <summary>上坡滑铲重力倍率（对应 slideGravityUphillMultiplier）。</summary>
    [Export] public float SlideGravityUphillMultiplier { get; set; } = 0.4f;

    /// <summary>可滑铲的最大坡度（地面法线与 up 的 dot 下限，对应 slide_max_angle_dot）。</summary>
    [Export] public float SlideMaxAngleDot { get; set; } = 0.55f;

    /// <summary>起滑瞬间的速度加成（m/s，对应 slideSpeedBoost）。</summary>
    [Export] public float SlideSpeedBoost { get; set; } = 1.5f;

    /// <summary>速度加成上限（m/s，对应 slideSpeedBoostCap）。</summary>
    [Export] public float SlideSpeedBoostCap { get; set; } = 12.0f;

    /// <summary>滑铲起跳高度（m，对应 slideJumpHeight）。</summary>
    [Export] public float SlideJumpHeight { get; set; } = 2.1f;

    /// <summary>滑铲起跳最大速度（m/s，对应 slideMaxJumpSpeed）。</summary>
    [Export] public float SlideMaxJumpSpeed { get; set; } = 13.0f;

    /// <summary>滑铲最短持续时间（s），避免瞬起瞬停。</summary>
    [Export] public float SlideMinDuration { get; set; } = 0.25f;

    /// <summary>滑铲冷却（s，对应 player_slideBoostCooldown）。</summary>
    [Export] public float SlideCooldown { get; set; } = 0.5f;

    // ───────────────────────── 蹬墙跑（Titanfall/Apex） ─────────────────────────

    /// <summary>是否启用蹬墙跑（对应 wallrun_enable）。</summary>
    [Export] public bool WallrunEnabled { get; set; } = true;

    /// <summary>蹬墙跑所需最小速度（m/s，对应 wallrun_wallrunMinSpeed）。</summary>
    [Export] public float WallrunMinSpeed { get; set; } = 3.0f;

    /// <summary>蹬墙跑水平最大速度（m/s，对应 wallrunMaxSpeedHorizontal）。</summary>
    [Export] public float WallrunMaxSpeedHorizontal { get; set; } = 9.0f;

    /// <summary>蹬墙跑垂直最大速度（m/s，对应 wallrunMaxSpeedVertical）。</summary>
    [Export] public float WallrunMaxSpeedVertical { get; set; } = 4.0f;

    /// <summary>蹬墙跑水平加速度（对应 wallrunAccelerateHorizontal）。</summary>
    [Export] public float WallrunAccelerateHorizontal { get; set; } = 120.0f;

    /// <summary>蹬墙跑垂直加速度（对应 wallrunAccelerateVertical）。</summary>
    [Export] public float WallrunAccelerateVertical { get; set; } = 80.0f;

    /// <summary>蹬墙跑水平减速度（对应 wallrunDecelerateHorizontal）。</summary>
    [Export] public float WallrunDecelerateHorizontal { get; set; } = 60.0f;

    /// <summary>蹬墙跑垂直减速度（对应 wallrunDecelerateVertical）。</summary>
    [Export] public float WallrunDecelerateVertical { get; set; } = 40.0f;

    /// <summary>允许吸附的墙面距离（m，对应 wallrunAllowedWallDistance）。</summary>
    [Export] public float WallrunAllowedWallDistance { get; set; } = 1.2f;

    /// <summary>入墙最小夹角 cos 阈值（对应 wallrun_minAngle_air）。</summary>
    [Export] public float WallrunMinAngleCos { get; set; } = 0.2f;

    /// <summary>蹬墙跑重力爬升时间（s，对应 wallrunGravityRampUpTime）：期内重力被压制。</summary>
    [Export] public float WallrunGravityRampUpTime { get; set; } = 0.3f;

    /// <summary>单次蹬墙跑时间上限（s，对应 wallrun_timeLimit）。</summary>
    [Export] public float WallrunTimeLimit { get; set; } = 2.0f;

    /// <summary>蹬墙跑起跳的向上速度（m/s，对应 wallrunJumpUpSpeed）。</summary>
    [Export] public float WallrunJumpUpSpeed { get; set; } = 6.0f;

    /// <summary>蹬墙跑起跳的离墙速度（m/s，对应 wallrunJumpOutwardSpeed）。</summary>
    [Export] public float WallrunJumpOutwardSpeed { get; set; } = 6.5f;

    /// <summary>蹬墙跑起跳时沿输入方向的速度（m/s，对应 wallrunJumpInputDirSpeed）。</summary>
    [Export] public float WallrunJumpInputDirSpeed { get; set; } = 3.0f;

    /// <summary>同墙判定距离（m，对应 wallrun_sameWallDist）。</summary>
    [Export] public float WallrunSameWallDist { get; set; } = 1.5f;

    /// <summary>同墙判定 cos 阈值（对应 wallrun_sameWallDot）。</summary>
    [Export] public float WallrunSameWallDot { get; set; } = 0.9f;

    /// <summary>贴墙向上的额外加成（对应 wallrunUpWallBoost）。</summary>
    [Export] public float WallrunUpWallBoost { get; set; } = 1.0f;

    // ───────────────────────── 工厂 ─────────────────────────

    /// <summary>默认 Apex 手感配置。</summary>
    public static ApexMoveConfig CreateApexDefault() => new();
}
