using Godot;

namespace ApexMovement;

/// <summary>
/// 玩家移动的**确定性状态**。所有字段都是纯数值，可序列化用于网络同步 / 回滚重放。
///
/// 设计要点：本类**不含任何引擎调用**（不做射线、不碰节点）。物理查询由 GDScript 玩法层
/// 完成后写入 <see cref="ApexMoveEnv"/>。因此同一 (state, input, env, config, dt) 必然产生
/// 同一结果 —— 这是客户端权威与服务器权威两种模式共用一套移动逻辑的前提。
/// </summary>
[GlobalClass]
public partial class ApexMoveState : RefCounted
{
    /// <summary>世界坐标位置。</summary>
    public Vector3 Position { get; set; } = Vector3.Zero;

    /// <summary>世界坐标速度（m/s）。</summary>
    public Vector3 Velocity { get; set; } = Vector3.Zero;

    /// <summary>当前局部“上”方向（球面重力下 = 背离质心）。</summary>
    public Vector3 Up { get; set; } = Vector3.Up;

    /// <summary>站立时脚底接触的地面法线。</summary>
    public Vector3 GroundNormal { get; set; } = Vector3.Up;

    /// <summary>是否站在地面上。</summary>
    public bool OnGround { get; set; }

    // ── 蹲伏 ──
    /// <summary>是否处于蹲伏。</summary>
    public bool Crouching { get; set; }
    /// <summary>蹲伏过渡插值（0=站, 1=蹲）。</summary>
    public float CrouchFraction { get; set; }

    // ── 跳跃 ──
    /// <summary>本次滞空已使用的空中跳次数。</summary>
    public int AirJumpsUsed { get; set; }
    /// <summary>跳跃输入缓冲剩余时间（s）。</summary>
    public float JumpBufferTimer { get; set; }
    /// <summary>离地后仍可起跳的宽限时间（coyote time，s）。</summary>
    public float CoyoteTimer { get; set; }
    /// <summary>起跳锁定计时（s）：起跳后短暂忽略接地，保证真正离地（防止探测距离内的地面把玩家粘住）。</summary>
    public float JumpLockTimer { get; set; }

    // ── 滑铲 ──
    public bool Sliding { get; set; }
    public float SlideTime { get; set; }
    public float SlideCooldownTimer { get; set; }
    /// <summary>滑铲是否已获得过起滑加速（避免重复叠加）。</summary>
    public bool SlideBoosted { get; set; }

    // ── 蹬墙跑 ──
    public bool Wallrunning { get; set; }
    public Vector3 WallNormal { get; set; } = Vector3.Zero;
    public float WallrunTime { get; set; }
    /// <summary>最近一次蹬墙结束后的计时（同墙冷却用）。</summary>
    public float TimeSinceWallrun { get; set; } = 999f;
    public Vector3 LastWallNormal { get; set; } = Vector3.Zero;

    /// <summary>深拷贝（回滚 / 预测用）。</summary>
    public ApexMoveState Clone()
    {
        return new ApexMoveState
        {
            Position = Position,
            Velocity = Velocity,
            Up = Up,
            GroundNormal = GroundNormal,
            OnGround = OnGround,
            Crouching = Crouching,
            CrouchFraction = CrouchFraction,
            AirJumpsUsed = AirJumpsUsed,
            JumpBufferTimer = JumpBufferTimer,
            CoyoteTimer = CoyoteTimer,
            JumpLockTimer = JumpLockTimer,
            Sliding = Sliding,
            SlideTime = SlideTime,
            SlideCooldownTimer = SlideCooldownTimer,
            SlideBoosted = SlideBoosted,
            Wallrunning = Wallrunning,
            WallNormal = WallNormal,
            WallrunTime = WallrunTime,
            TimeSinceWallrun = TimeSinceWallrun,
            LastWallNormal = LastWallNormal,
        };
    }

    /// <summary>把当前状态复制到另一个实例（回滚重放时避免分配）。</summary>
    public void CopyTo(ApexMoveState dst)
    {
        dst.Position = Position;
        dst.Velocity = Velocity;
        dst.Up = Up;
        dst.GroundNormal = GroundNormal;
        dst.OnGround = OnGround;
        dst.Crouching = Crouching;
        dst.CrouchFraction = CrouchFraction;
        dst.AirJumpsUsed = AirJumpsUsed;
        dst.JumpBufferTimer = JumpBufferTimer;
        dst.CoyoteTimer = CoyoteTimer;
        dst.JumpLockTimer = JumpLockTimer;
        dst.Sliding = Sliding;
        dst.SlideTime = SlideTime;
        dst.SlideCooldownTimer = SlideCooldownTimer;
        dst.SlideBoosted = SlideBoosted;
        dst.Wallrunning = Wallrunning;
        dst.WallNormal = WallNormal;
        dst.WallrunTime = WallrunTime;
        dst.TimeSinceWallrun = TimeSinceWallrun;
        dst.LastWallNormal = LastWallNormal;
    }

    /// <summary>紧凑序列化（18 个 float）。用于网络同步 / 存档。</summary>
    public float[] GetState()
    {
        return new[]
        {
            Position.X, Position.Y, Position.Z,
            Velocity.X, Velocity.Y, Velocity.Z,
            Up.X, Up.Y, Up.Z,
            GroundNormal.X, GroundNormal.Y, GroundNormal.Z,
            OnGround ? 1f : 0f,
            Crouching ? 1f : 0f,
            CrouchFraction,
            AirJumpsUsed,
            Sliding ? 1f : 0f,
            SlideTime,
            SlideCooldownTimer,
            Wallrunning ? 1f : 0f,
            WallNormal.X, WallNormal.Y, WallNormal.Z,
            WallrunTime,
            TimeSinceWallrun,
            JumpBufferTimer,
            CoyoteTimer,
            JumpLockTimer,
        };
    }

    public void SetState(float[] s)
    {
        Position = new Vector3(s[0], s[1], s[2]);
        Velocity = new Vector3(s[3], s[4], s[5]);
        Up = new Vector3(s[6], s[7], s[8]);
        GroundNormal = new Vector3(s[9], s[10], s[11]);
        OnGround = s[12] > 0.5f;
        Crouching = s[13] > 0.5f;
        CrouchFraction = s[14];
        AirJumpsUsed = (int)s[15];
        Sliding = s[16] > 0.5f;
        SlideTime = s[17];
        SlideCooldownTimer = s[18];
        Wallrunning = s[19] > 0.5f;
        WallNormal = new Vector3(s[20], s[21], s[22]);
        WallrunTime = s[23];
        TimeSinceWallrun = s[24];
        JumpBufferTimer = s[25];
        CoyoteTimer = s[26];
        JumpLockTimer = s[27];
    }
}
