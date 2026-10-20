using System;

namespace ApexMovement.Camera;

/// <summary>FOV 插值所在的数值空间。</summary>
public enum FovInterpSpace
{
    /// <summary>角度空间线性插值（直观）。</summary>
    Linear = 0,
    /// <summary>
    /// **tan(半角) 空间插值**。Apex 二进制里 FOV 以 `.fTanHalfFOV` 字段存储
    /// （见 docs/reverse-engineering-v0.2.md §3.1），说明投影运算在 tan 空间进行。
    /// 在这种模式下，FOV 变化的"视觉速度"才是均匀的。
    /// </summary>
    Tangent = 1,
}

/// <summary>
/// 相机效果的全部可调参数（单一事实来源）。
///
/// ## 证据等级
/// 本文件**没有 A 级数值证据** —— 逆向只证明了这些系统的**存在与字段名**，
/// 数值全部读不到（在 BSS 里）。因此这里每个字段都标 **C**（设计选择 / 手感校准），
/// 但字段命名与分组**严格对应逆向观测到的字段**，方便将来拿到真值后逐个替换。
///
/// 对应的逆向字段见 docs/reverse-engineering-v0.2.md §3–§4：
///   · `cl_fovScale` / `setting.cl_fovScale` / `.fTanHalfFOV` / `USE_FOV_SCALING`
///   · `viewkick_spring` / `_cooldown_holdtime` / `_cooldown_fadetime` / `_velocityScalePerShot`
///   · `viewkickFallDistMin` / `viewkickFallDistMax` / `viewkickFallDistMaxScale`
///   · `slide_viewTiltSide` / `_IncreaseSpeed` / `_DecreaseSpeed` / `_PlayerSpeed`
///   · `sprinttiltMaxRoll`
///   · `traversal_viewLerpOutPos` / `traversal_viewLerpOutAngle` / `traversal_viewLerpInDuration`
///   · `traversal_mantle_cap_camera_height`
///   · `sprintViewOffset` / `viewheight`
/// </summary>
public sealed class CameraEffectsConfig
{
    // ═══════════════════════════ FOV ═══════════════════════════

    /// <summary>[C] 基础水平 FOV (deg)。Apex 游戏内 FOV 设置范围是 70–110。</summary>
    public float FovBase = 90f;

    /// <summary>[C] FOV 允许范围（会被夹住）。</summary>
    public float FovMin = 60f;
    public float FovMax = 120f;

    /// <summary>[C] 冲刺时叠加的 FOV (deg)。</summary>
    public float FovSprintAdd = 3f;

    /// <summary>[C] 滑铲时叠加的 FOV (deg)。</summary>
    public float FovSlideAdd = 6f;

    /// <summary>[C] 攀爬/翻越时的 FOV 收缩 (deg，负值 = 变窄)。</summary>
    public float FovTraversalAdd = -4f;

    /// <summary>[C] FOV 平滑速度（1/s，越大越快跟上目标）。</summary>
    public float FovSmoothing = 8f;

    /// <summary>[C] FOV 插值空间（默认 tan 空间，对应 `.fTanHalfFOV`）。</summary>
    public FovInterpSpace FovSpace = FovInterpSpace.Tangent;

    /// <summary>[C] 玩家可用按键调节的 FOV 步长 (deg/次)。</summary>
    public float FovUserStep = 2f;

    // ═══════════════════════════ 视角踢（弹簧） ═══════════════════════════

    /// <summary>
    /// [C] 弹簧刚度 k（`viewkick_spring`）。运动方程 <c>a = −k·x − c·v</c>。
    /// ω = sqrt(k) = 13.4 rad/s ≈ 2.1 Hz，接近"落地一顿"的观感。
    /// </summary>
    public float SpringStiffness = 180f;

    /// <summary>[C] 弹簧阻尼 c（`viewkick_spring_cooldown_fadetime` 的对应物）。</summary>
    public float SpringDamping = 18f;

    /// <summary>
    /// [C] **视角踢总幅度倍率**（v0.3 新增）。
    ///
    /// 需求「镜头晃动过于剧烈」的**手感总闸**：所有冲量（落地 / 跳跃）在
    /// <see cref="ApexCameraRig.AddKick"/> 入口统一乘上它，所以调这一个数就能
    /// 整体放大/缩小镜头晃动，不必逐档改数组。
    ///
    /// 默认 1.0（各项已在下面的数组里按目标幅度标定好）；0 = 完全关闭视角踢；
    /// 想更"硬核"可调到 1.3 左右。
    /// </summary>
    public float KickScale = 1.0f;

    // ── v0.3：把落地方向的冲量整体下调（这才是"晃动过于剧烈"的真正修法） ──
    //
    // 推导（ζ = c/(2√k) = 0.67，ω_n = √180 = 13.42，ω_d = ω_n√(1−ζ²) = 9.96）：
    //   冲量 v₀ 下的峰值位移 ≈ (v₀/ω_d)·e^(−ζω_n·t_p)·sin(ω_d·t_p) ≈ 0.047·v₀
    // v0.2 的实际幅度：
    //   档1 v₀=0.9 → 0.042 rad ≈  2.4°
    //   档2 v₀=2.2 → 0.104 rad ≈  5.9°
    //   档3 v₀=4.0 → 0.189 rad ≈ 10.8°  ← 已被 MaxKickAngle 0.18 钳死
    //   档4 v₀=6.5 → 0.307 rad ≈ 17.6°  ← 同样钳死 ⇒ 高空落地固定 10.3° 俯仰砸脸
    // 这就是"剧烈"的量化根因：后两档**都撞上限**，等于所有大跳落地都是同一次 10° 甩头。
    // v0.3 把各档压到约 0.7° / 1.5° / 2.5° / 3.6°，并把上限放宽到 4.3° 留出余量。

    /// <summary>
    /// [C] 各落地档位的俯仰冲量 (rad/s)。索引 = <c>LandingTier</c>。
    /// 峰值位移 ≈ 0.047 × 本值 ⇒ {0, 0.25, 0.55, 0.90, 1.33} → 0° / 0.7° / 1.5° / 2.5° / 3.6°。
    /// </summary>
    public float[] LandingPitchImpulse = { 0f, 0.25f, 0.55f, 0.90f, 1.33f };

    /// <summary>
    /// [C] 各落地档位的偏航随机量 (rad/s)。落地不应有确定方向，这里用确定性的符号交替。
    /// 峰值 ≈ 0.047 × 本值 ⇒ 最大约 0.5°。
    /// </summary>
    public float[] LandingYawImpulse = { 0f, 0.03f, 0.07f, 0.12f, 0.18f };

    /// <summary>[C] 各落地档位的滚转冲量 (rad/s)。峰值 ⇒ 最大约 0.4°。</summary>
    public float[] LandingRollImpulse = { 0f, 0.02f, 0.05f, 0.09f, 0.14f };

    /// <summary>[C] 跳跃时给一点向上的视角踢（起跳"抬镜头"）。峰值 ≈ 0.28°。</summary>
    public float JumpPitchImpulse = 0.10f;

    /// <summary>
    /// [C] 视角踢的总幅度上限 (rad)，防止极端情况把镜头甩飞。
    /// v0.3 从 0.18 rad (10.3°) 收到 0.075 rad (4.3°)：让它只当**保险丝**，
    /// 而不是像 v0.2 那样成为"每次大落地都撞到"的实际工作点。
    /// </summary>
    public float MaxKickAngle = 0.075f;

    // ═══════════════════════════ 侧倾（roll） ═══════════════════════════

    /// <summary>[C] 滑铲最大侧倾角 (deg)（`slide_viewTiltSide` 的幅度）。</summary>
    public float SlideMaxRollDeg = 5.0f;

    /// <summary>[C] 滑铲侧倾增大速率 (deg/s)（`slide_viewTiltIncreaseSpeed`）。</summary>
    public float SlideRollIncreaseSpeed = 60f;

    /// <summary>[C] 滑铲侧倾减小速率 (deg/s)（`slide_viewTiltDecreaseSpeed`）。</summary>
    public float SlideRollDecreaseSpeed = 35f;

    /// <summary>[C] 侧倾角达到最大时的参考速度 (m/s)（`slide_viewTiltPlayerSpeed`）。</summary>
    public float SlideRollPlayerSpeedRef = 8.0f;

    /// <summary>[C] 侧倾方向（+1 右倾 / −1 左倾）。</summary>
    public float SlideRollSide = 1f;

    /// <summary>[C] 冲刺最大侧倾角 (deg)（`sprinttiltMaxRoll`）。</summary>
    public float SprintMaxRollDeg = 1.5f;

    /// <summary>[C] 冲刺侧倾的跟随速度 (deg/s)。</summary>
    public float SprintRollSpeed = 12f;

    // ═══════════════════════════ 攀爬相机插值 ═══════════════════════════

    /// <summary>
    /// [C] 翻越时相机"脱开"（lerp out）的时长 (s)（`traversal_viewLerpOutPos` / `OutAngle`）。
    /// 0 = 相机完全跟随（生硬但无延迟）。
    /// </summary>
    public float TraversalLerpOutDuration = 0.12f;

    /// <summary>[C] 翻越结束后相机"回贴"（lerp in）的时长 (s)（`traversal_viewLerpInDuration`）。</summary>
    public float TraversalLerpInDuration = 0.20f;

    /// <summary>
    /// [C] 翻越期间相机允许的最大离地高度 (m)（`traversal_mantle_cap_camera_height`）。
    /// 翻越高墙时相机不会跟着飞到很高，避免眩晕。
    /// </summary>
    public float TraversalCapCameraHeight = 2.20f;

    /// <summary>[C] 翻越期间相机位置滞后的比例（0 = 不滞后，1 = 完全不动）。</summary>
    public float TraversalPositionLag = 0.45f;

    /// <summary>[C] 翻越期间相机俯仰滞后的比例。</summary>
    public float TraversalAngleLag = 0.35f;

    // ═══════════════════════════ 走路摇晃（bob） ═══════════════════════════
    //
    // ★ v0.4 起，下面这一组 **不再被 ApexCameraRig 读取**。
    //
    // 需求原话：「通过步行和奔跑的速度，以及角色身高对应的步幅步频来获得对应的
    // 镜头抖动频率和幅度，奔跑状态的步幅和步频需要考虑现实人物奔跑的略微腾空。」
    //
    // 于是幅度与频率全部改由 `core/GaitModel.cs` 按生物力学推导
    // （身高 → 腿长 → Froude 速度 → 步频 → 步幅 → 腾空比 → 质心起伏），
    // 求解器把结果放进 `MovementSnapshot.Gait`，相机直接读。
    //
    // 保留这些字段是为了配置兼容（老配置文件、老测试仍能编译），
    // 改它们不会影响画面 —— 想调摇晃幅度请用 <see cref="BobGaitScale"/>。
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// [C] **步态摇晃倍率**（v0.4 新增，这是唯一仍然生效的摇晃幅度旋钮）。
    ///
    /// 1.0 = 步态模型推导出的物理值（舒适档：垂直 ~2 cm 半幅、点头 ≤0.8°）；
    /// 0.0 = 完全关掉走路摇晃；2.0 = 翻倍（用于对比/调试）。
    /// 运行时可通过调试菜单（`\` 键）实时拖动。
    /// </summary>
    public float BobGaitScale = 1.0f;

    /// <summary>
    /// [C] 走路摇晃的垂直幅度 (m)。**v0.4 起不再生效**（改由步态模型推导）。
    ///
    /// ⚠️ **Apex 没有 `cl_bob` 类 cvar**（逆向负结论），走路摇晃来自第一人称动画，
    /// 因此这里**没有任何证据可依**，纯手感参数。
    ///
    /// v0.3：0.035 → 0.026（配合下面几项一起收，需求是"镜头晃动过于剧烈"）。
    /// </summary>
    public float BobAmplitude = 0.026f;

    /// <summary>[C] 走路摇晃的横向幅度 (m)。**v0.4 起不再生效**。v0.3：0.022 → 0.016。</summary>
    public float BobLateralAmplitude = 0.016f;

    /// <summary>冲刺时摇晃幅度倍率。**v0.4 起不再生效**（腾空比已含在步态模型里）。</summary>
    public float BobSprintMultiplier = 1.6f;

    /// <summary>[C] 摇晃的俯仰幅度 (deg)。**v0.4 起不再生效**。v0.3：0.35 → 0.24。</summary>
    public float BobPitchDeg = 0.24f;

    /// <summary>[C] 摇晃的滚转幅度 (deg)。v0.3：0.30 → 0.20。</summary>
    public float BobRollDeg = 0.20f;

    /// <summary>[C] 摇晃开始生效的最低速度 (m/s)。</summary>
    public float BobMinSpeed = 0.6f;

    // ═══════════════════════════ 眼位偏移 ═══════════════════════════

    /// <summary>[C] 冲刺时的相机前移量 (m)（对应 `sprintViewOffset`）。</summary>
    public float SprintEyeForwardOffset = 0.06f;

    /// <summary>[C] 蹲下时的相机前移量 (m)（蹲下身体前倾）。</summary>
    public float CrouchEyeForwardOffset = 0.03f;

    /// <summary>[C] 滑铲时的相机下沉量 (m)（贴地感）。</summary>
    public float SlideEyeDrop = 0.10f;

    public void Validate()
    {
        if (FovMin <= 0f || FovMax <= FovMin)
            throw new ArgumentException("FovMin / FovMax 配置非法");
        if (FovBase < FovMin || FovBase > FovMax)
            throw new ArgumentOutOfRangeException(nameof(FovBase), "FovBase 必须在 [FovMin, FovMax] 内");
        if (SpringStiffness <= 0f) throw new ArgumentOutOfRangeException(nameof(SpringStiffness));
        if (SpringDamping < 0f) throw new ArgumentOutOfRangeException(nameof(SpringDamping));
        if (KickScale < 0f) throw new ArgumentOutOfRangeException(nameof(KickScale));
        if (LandingPitchImpulse.Length < 5 || LandingYawImpulse.Length < 5 || LandingRollImpulse.Length < 5)
            throw new ArgumentException("落地冲量数组必须至少 5 项（对应 LandingTier 0..4）");
        if (SlideRollPlayerSpeedRef <= 0f)
            throw new ArgumentOutOfRangeException(nameof(SlideRollPlayerSpeedRef));
        if (TraversalCapCameraHeight <= 0f)
            throw new ArgumentOutOfRangeException(nameof(TraversalCapCameraHeight));
    }

    public CameraEffectsConfig Clone() => (CameraEffectsConfig)MemberwiseClone();
}
