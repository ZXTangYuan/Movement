namespace ApexMovement.Core;

/// <summary>移动状态机的状态。</summary>
public enum MovementState
{
    /// <summary>地面（站立或蹲下行走）。</summary>
    Ground = 0,
    /// <summary>空中（跳跃 / 下落）。</summary>
    Air = 1,
    /// <summary>滑铲。</summary>
    Slide = 2,
    /// <summary>攀爬（抓边 + 上拉，站到台面上）。</summary>
    Mantle = 3,
    /// <summary>翻越（跨过低障碍；<see cref="MantleClass.Below"/> 档的特例外观）。</summary>
    Vault = 4,
    /// <summary>窗口翻越（穿过窗口开口）。</summary>
    Window = 5,
    /// <summary>贴墙爬（Climb）。</summary>
    Climb = 6,
    /// <summary>挂边待机（Mantle hang）。</summary>
    Hang = 7,
    /// <summary>
    /// 打滑（Slip）。被 <c>C_TriggerSlipSphere</c> 类体积推动时进入：
    /// 摩擦失效，速度被体积的 speed / acceleration 拽向体积方向。
    /// </summary>
    Slip = 8,
    /// <summary>
    /// 扒墙 / 悬垂（Wall hang / dangle）。与 <see cref="Hang"/> 不同：
    /// Hang 是"攀爬到顶的过渡态"，WallHang 是**可停留**的独立状态，
    /// 支持沿墙上下 / 左右移动，久了会脱手。
    /// </summary>
    WallHang = 9,
}

/// <summary>
/// 翻越的高度分档。
///
/// **来源**：逆向在 `.rdata` 直接观测到 4 个时长字段
/// `mantleDurationHigh / mantleDurationAbove / mantleDurationLevel / mantleDurationBelow`
/// 与对应动画 `ACT_VM_MANTLE_HIGH / _ABOVE / _LEVEL / _BELOW`（见 docs/reverse-engineering-v0.2.md §5.5）。
/// 说明 Apex 按**台面相对角色的高度**分 4 档，每档一个时长。
/// </summary>
public enum MantleClass
{
    /// <summary>台面低于蹲姿高度 —— 跨过去就行（旧版叫 Vault）。</summary>
    Below = 0,
    /// <summary>台面在蹲姿与站姿高度之间。</summary>
    Level = 1,
    /// <summary>台面高于站姿高度（要拉上去）。</summary>
    Above = 2,
    /// <summary>台面远高于站姿高度（高位攀爬，接近 Climb 上限）。</summary>
    High = 3,
}

/// <summary>攀爬的两段式相位（wiki：Mantle grab 0.45s + Mantle pull 0.45s）。</summary>
public enum MantlePhase
{
    /// <summary>抓边（Mantle grab）。</summary>
    Grab = 0,
    /// <summary>上拉（Mantle pull）。</summary>
    Pull = 1,
    /// <summary>挂边待机（松手不按前进 → Mantle hang）。</summary>
    Hang = 2,
}

/// <summary>
/// 落地分档。
///
/// **来源**：逆向观测到 `landDistLow / landDistMedium / landDistHigh / hardFallDist`
/// 与动画 `ACT_MP_JUMP_LAND_LOW / _MEDIUM / _HIGH / _SLAM`
/// （见 docs/reverse-engineering-v0.2.md §4.2）。
/// </summary>
public enum LandingTier
{
    /// <summary>没有落地事件。</summary>
    None = 0,
    /// <summary>轻微落地（几乎无晃动）。</summary>
    Low = 1,
    /// <summary>中等落地。</summary>
    Medium = 2,
    /// <summary>高落地（明显晃动）。</summary>
    High = 3,
    /// <summary>硬着陆（最强晃动 + 落地硬直）。</summary>
    Hard = 4,
}

/// <summary>
/// 单个固定 tick 的输入快照。
///
/// **设计要点**：输入以"每 tick 一份"的形式喂进求解器。
/// 鼠标增量在 tick 边界被**全部消费**（不按渲染帧分摊）——
/// 因为 tick 频率固定，总增量守恒 ⇒ 任意帧率下累计转角一致。
/// </summary>
public struct MovementInput
{
    /// <summary>横移轴 [-1,1]，右为正。</summary>
    public float MoveX;

    /// <summary>前后轴 [-1,1]，前为正。</summary>
    public float MoveZ;

    /// <summary>本 tick 是否**按下**了跳跃（上升沿，非持续按住）。</summary>
    public bool JumpPressed;

    /// <summary>跳跃是否**按住**（Auto-mantle 需要按住跳跃）。</summary>
    public bool JumpHeld;

    /// <summary>蹲是否按住。</summary>
    public bool CrouchHeld;

    /// <summary>冲刺（Shift）是否按住。</summary>
    public bool SprintHeld;

    /// <summary>是否收枪（Holster）。收枪给 ×1.15 速度（wiki Holster）。</summary>
    public bool Holstered;

    /// <summary>视角偏航（弧度）。</summary>
    public float Yaw;

    /// <summary>视角俯仰（弧度，向上为正）。</summary>
    public float Pitch;

    /// <summary>本 tick 消费的鼠标偏航增量（弧度）。</summary>
    public float MouseDeltaYaw;

    /// <summary>本 tick 消费的鼠标俯仰增量（弧度）。</summary>
    public float MouseDeltaPitch;

    public static MovementInput Empty => default;
}

/// <summary>
/// 求解器对外暴露的只读状态快照（每 tick 结束后更新）。
///
/// 相机子系统、HUD、测试都读这个结构，而不是直接摸求解器私有字段 ——
/// 这样相机可以纯数学测试（不需要 Godot）。
/// </summary>
public struct MovementSnapshot
{
    // ── 位姿 ──
    public F3 Position;          // 胶囊中心
    public F3 Velocity;
    public F3 Up;
    public float Yaw;
    public float Pitch;

    // ── 体型 ──
    public float CurrentHeight;
    public float CrouchAmount;   // 0 = 站, 1 = 全蹲
    public float FeetOffsetFromCenter;   // 中心 → 脚底（沿 up）
    public float EyeOffsetFromFeet;      // 脚底 → 眼位（沿 up）= 当前身高 − EyeHeightOffset

    // ── 运动 ──
    public float HorizontalSpeed;
    public float VerticalSpeed;  // 沿 up 的分量
    public bool OnGround;
    public bool Sprinting;
    public bool Sliding;
    public bool Crouching;
    public bool Climbing;
    public MovementState State;

    // ── 翻越 ──
    public MantleClass MantleClass;
    public MantlePhase MantlePhase;
    public float TraversalProgress;   // 0..1；不在翻越时为 -1

    // ── 贴墙爬 / 扒墙（v0.3） ──
    /// <summary>松 W 后剩余宽限时间 (s)；按住 W 时为满值。</summary>
    public float ClimbNoInputRemaining;
    /// <summary>是否处于"即将扒墙，正在补爬最后一小段"。</summary>
    public bool ClimbApproaching;
    /// <summary>是否挂在沿上（Mantle hang / 扒墙）。</summary>
    public bool Hanging;

    // ── 打滑 / 扒墙（v0.3 完整版） ──
    /// <summary>是否处于打滑态（<c>IsSlipping</c>）。</summary>
    public bool Slipping;
    /// <summary>打滑体积给的推动方向（水平单位向量）；不在滑区且无可继承方向时为零。</summary>
    public F3 SlipDirection;
    /// <summary>离开滑区后的"空中方向限制"剩余时间 (s)（<c>m_slipAirRestrictTime</c>）。</summary>
    public float SlipAirRestrictRemaining;
    /// <summary>是否处于扒墙态（<c>IsWallHanging</c>）。</summary>
    public bool WallHanging;
    /// <summary>扒墙握力 0..1：1 = 刚挂上，0 = 脱手（<c>m_wallDangleClimbProgressFloor</c> 的手感映射）。</summary>
    public float WallHangGrip;
    /// <summary>本次扒墙已持续时间 (s)（<c>m_wallHangTime</c>）。</summary>
    public float WallHangTime;

    // ── 落地事件（供相机晃动） ──
    public LandingTier LandingTier;       // 本 tick 是否发生落地
    public float LandingFallDistance;     // 本次落地的下落高度（米）
    public float LandingImpactSpeed;      // 落地瞬间的向下速度（m/s）

    // ── 帧率无关的步频相位（供走路摇晃） ──
    public float BobPhase;
    public int TickCount;

    // ── 步态推导结果（v0.4：相机只读，不再自己拍数） ──
    /// <summary>
    /// 本 tick 的步态求值结果（步频 / 步幅 / 腾空比 / 各方向抖动半幅）。
    /// 由 <see cref="GaitModel"/> 按「身高 + 水平速度 + 冲刺参考速度 + 蹲伏量」推导，
    /// 相机只负责把它变成眼位偏移与角度，**不再自己拍频率/幅度**。
    /// </summary>
    public GaitSample Gait;

    // ── 台阶过渡（v0.4：≤0.25m 台阶"只改方向不改大小"） ──
    /// <summary>当前台阶倾斜角 (rad)：速度方向被抬起这么多，0 = 水平。</summary>
    public float StepTiltAngle;
}

/// <summary>
/// 求解器与碰撞世界的边界。
///
/// 求解器**不直接**调用 Godot 物理：这样它可以在无引擎环境下做单元测试，
/// 也保证"同一份输入 + 同一份世界 ⇒ 同一轨迹"。
/// 实现方（Godot 侧）必须保证查询发生在**固定 tick 边界**、世界为静态几何。
/// </summary>
public interface IMovementWorld
{
    /// <summary>
    /// 胶囊体扫描：从 <paramref name="pos"/> 沿 <paramref name="delta"/> 扫一个
    /// 半径 <paramref name="radius"/>、半高 <paramref name="halfHeight"/> 的胶囊。
    /// </summary>
    /// <returns>true 表示撞到东西。</returns>
    bool Sweep(F3 pos, float radius, float halfHeight, F3 delta,
               out F3 hitNormal, out float fraction);

    /// <summary>胶囊体是否与几何体重叠（用于站起检测、卡墙脱困）。</summary>
    bool Overlaps(F3 pos, float radius, float halfHeight);

    /// <summary>
    /// 从 <paramref name="origin"/> 沿 <paramref name="dir"/> 射线检测，
    /// 命中返回距离与法线。用于攀爬/翻越的前方探测。
    /// </summary>
    bool Raycast(F3 origin, F3 dir, float maxDist, out F3 hitPoint, out F3 hitNormal);
}

/// <summary>
/// 一次打滑采样：由体积（Apex 的 <c>C_TriggerSlipSphere</c>）提供。
///
/// **来源**：逆向在 `.rdata` 观测到
/// <c>DT_TriggerSlipSphere</c> / <c>C_TriggerSlipSphere</c> / <c>trigger_slip_sphere</c>
/// 与体积上的 <c>m_slipSpeed</c> / <c>m_slipAcceleration</c> / <c>m_defaultSlipDirection</c>；
/// 玩家身上有 <c>m_touchingSlipTriggersCount</c> 及
/// <c>m_touchingSlipTriggersSpeed[] / Acceleration[] / Direction[] / Origin[]</c>
/// 与聚合值（见 docs/reverse-engineering-v0.2.md §5.10）。
/// 说明它是**世界里的体积**，不是"地面材质"。
/// </summary>
public struct SlipSample
{
    /// <summary>推动方向（水平、单位向量）。体积未配置方向时用玩家当前水平速度方向。</summary>
    public F3 Direction;

    /// <summary>目标水平速度 (m/s)（<c>m_slipSpeed</c>）。</summary>
    public float Speed;

    /// <summary>逼近目标速度的加速度 (m/s²)（<c>m_slipAcceleration</c>）。</summary>
    public float Acceleration;

    /// <summary>
    /// 是否把玩家的**垂直**速度也一起抹掉（"滑道/斜槽"式体积）。
    /// 平地滑区用 false，让重力照常；斜槽用 true。
    /// </summary>
    public bool LockToPlane;
}

/// <summary>
/// 可选的世界能力：打滑体积采样。
///
/// <see cref="IMovementWorld"/> 不加这个方法，是为了**不破坏**已有实现
/// （测试替身 AnalyticWorld、Godot 桥接层都能只实现基础三件事）。
/// 求解器用 <c>world is ISlipWorld</c> 探测，没实现就当作"世界里没有滑区"。
/// </summary>
public interface ISlipWorld
{
    /// <summary>查询 <paramref name="pos"/> 是否落在某个打滑体积内，并给出合成后的推动参数。</summary>
    bool SampleSlip(F3 pos, out SlipSample sample);
}
