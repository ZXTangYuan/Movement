using Godot;

namespace ApexMovement;

/// <summary>
/// 一帧的**物理环境查询结果**。由 GDScript 玩法层完成射线/球体检测后写入，
/// 供 C# 确定性核心使用。核心本身不做任何引擎查询，保证可回滚重放。
/// </summary>
[GlobalClass]
public partial class ApexMoveEnv : RefCounted
{
    /// <summary>该点的局部“上”方向（球面重力下 = 背离质心）。</summary>
    public Vector3 Up { get; set; } = Vector3.Up;

    /// <summary>重力加速度大小（m/s²）。方向为 -Up。</summary>
    public float GravityMagnitude { get; set; } = 9.8f;

    /// <summary>是否接触地面（向下探测命中）。</summary>
    public bool Grounded { get; set; }

    /// <summary>地面法线（世界空间，已归一化）。</summary>
    public Vector3 GroundNormal { get; set; } = Vector3.Up;

    /// <summary>地面摩擦倍率（不同材质）。</summary>
    public float SurfaceFriction { get; set; } = 1.0f;

    /// <summary>
    /// 地面吸附偏移（世界空间）。探测层算出「把脚底放回地表」所需的位置增量。
    /// 对应 Source 的 ground snap：球面行星上纯切向运动会沿切线飘离曲面，
    /// 每帧用此偏移把角色压回地表。接地且未起跳时由核心应用。
    /// </summary>
    public Vector3 GroundSnap { get; set; } = Vector3.Zero;

    /// <summary>是否探测到可蹬的墙（侧向）。</summary>
    public bool HasWall { get; set; }

    /// <summary>墙法线（世界空间，指向玩家一侧）。</summary>
    public Vector3 WallNormal { get; set; } = Vector3.Zero;

    /// <summary>玩家到墙面的距离（m）。</summary>
    public float WallDistance { get; set; } = 999f;

    /// <summary>墙面是否足够“竖直”可蹬（法线与 up 近似垂直）。</summary>
    public bool WallIsRunable { get; set; }

    // ── 攀爬 / 翻越探测（mantle / climb） ──

    /// <summary>前方是否探测到可翻越/攀爬的障碍（挡路、且有可站立的顶面）。</summary>
    public bool ObstacleAhead { get; set; }

    /// <summary>障碍顶面相对脚底的高度（m）。</summary>
    public float ObstacleHeight { get; set; }

    /// <summary>障碍（可站立顶面）的法线（世界空间）。</summary>
    public Vector3 ObstacleTopNormal { get; set; } = Vector3.Up;

    /// <summary>翻越目标点（世界坐标）：障碍顶面内侧、可站立处。</summary>
    public Vector3 MantleTargetPoint { get; set; } = Vector3.Zero;
}
