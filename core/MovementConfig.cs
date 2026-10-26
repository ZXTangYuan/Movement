using System;

namespace ApexMovement.Core;

/// <summary>
/// 移动系统的全部可调参数（单一事实来源）。
///
/// ## 数值口径（v0.2）
///
/// | 类别 | 来源 | 说明 |
/// |---|---|---|
/// | **角色尺寸** | **需求方口径** | 站立 1.95 m / 蹲下 0.975 m / 眼位 = 头顶 −0.10 m。**优先级最高，不随 Apex 改动** |
/// | 速度 / 高度 / 角度 | **Apex 实机实测（wiki）** | 全部按 1 hu = 2.54 cm 换算，见 docs/wiki-reference.md |
/// | 加速度 / 摩擦 / 时长 | **引擎谱系（B）** | Source 2 / Titanfall 谱系默认值 |
/// | 相机 / 手感曲线 | **设计选择（C）** | 无可靠证据，全部可调 |
///
/// **证据等级标注**：
///   A = 二进制直接观测（字段/枚举存在）或 wiki 实测数值
///   B = 由引擎谱系推导，未直接读到该值
///   C = 设计选择 / 需实机手感校准
///
/// **单位约定**：米 / 秒（Godot 世界单位 = 1m）。注释里给出 Source 单位（hu）原始值。
/// </summary>
public sealed class MovementConfig
{
    // ═════════════════════════════ 单位换算 ═════════════════════════════

    /// <summary>[A] hammer unit → 米。1 hu = 2.54 cm（wiki 引擎篇 + 游戏文件）。</summary>
    public const float HuToMeter = 0.0254f;

    /// <summary>hu/s → m/s 的便捷换算。</summary>
    public static float Hu(float huPerSecond) => huPerSecond * HuToMeter;

    // ═════════════════════════════ 重力与下落 ═════════════════════════════

    /// <summary>
    /// [A] 重力加速度 (m/s²)。
    ///
    /// **不是拍的**：由 wiki 的两个独立实测值反解 ——
    /// 跳跃高度 h = 56 hu = 1.4224 m，滞空时间 t = 0.74 s。
    /// 由 <c>h = g·t²/8</c> 得 <c>g = 8h/t² = 8×1.4224/0.5476 = 20.7802</c>。
    ///
    /// 顺带交叉验证：Source 默认 sv_gravity = 800 u/s² × 0.0254 = 20.32 m/s²，两者仅差 2.3%，
    /// 说明 Apex 确实沿用了 Source 量级但略有调高。
    /// </summary>
    public float Gravity = 9.80665f;   // v0.5：地球真实值（原先 20.7802 = Apex 等效）

    /// <summary>
    /// 当前**星球档案**（常规系数的来源）。默认保持 <see cref="PlanetProfile.ApexDefault"/>
    /// = v0.4.x 的 20.7802，保证所有既有手感数字不变；切星球一律走
    /// <see cref="ApplyPlanet"/>，它会同步 <see cref="Gravity"/>。
    ///
    /// v0.5 的既定方向：默认星球会换成 <see cref="PlanetProfile.Earth"/>（9.80665），
    /// 并把跳跃高度等**设计目标**固定住、由 √(2gh) 反解初速。
    /// </summary>
    public PlanetProfile Planet = PlanetProfile.Earth;

    /// <summary>
    /// 切换星球：只改**常规系数**（表面重力等），<see cref="Gravity"/> 立即同步。
    ///
    /// 设计目标（<see cref="JumpHeight"/>、<see cref="SprintSpeed"/>、滑铲加成……）
    /// **不动**；所有派生量（<see cref="JumpVelocity"/>、滑铲坡度加速度、
    /// <see cref="SlideCriticalAngleDeg"/>……）都是 get-only 计算属性，会自动跟着变。
    /// 这就是需求方要的「常规系数真实化 + 特殊量反推」。
    /// </summary>
    public void ApplyPlanet(PlanetProfile planet)
    {
        planet = planet.Name is null ? PlanetProfile.ApexDefault : planet;
        Planet = planet;
        Gravity = planet.SurfaceGravity;
    }

    /// <summary>[B] 终端下落速度上限 (m/s)。Source sv_maxvelocity = 3500 u/s ≈ 88.9。</summary>
    public float MaxFallSpeed = 88.9f;

    // ═════════════════════════════ 地面速度 ═════════════════════════════

    /// <summary>[A] 行走速度 (m/s) = 173.5 hu/s。wiki Ground：「a walk peaks at a velocity of 173.5 hu/s」。</summary>
    public float WalkSpeed = 4.407f;

    /// <summary>[A] 冲刺速度 (m/s) = 260 hu/s（持枪）。wiki Sprint：「it peaks at a velocity of 260 hu/s」。</summary>
    public float SprintSpeed = 6.60400f;

    /// <summary>[A] 蹲行速度 (m/s) = 80 hu/s（持枪）。wiki Crouch：「Crouchwalk peaks at 92hu/s」（收枪值）。</summary>
    public float CrouchWalkSpeed = 2.0f;

    /// <summary>
    /// [A] 收枪加速倍率 = 1.15。wiki Holster：「Holster Speedboost is 15% i.e a factor of 1.15」。
    /// 收枪冲刺 = 299 hu/s = 7.5946 m/s（这是玩家实际感知的"Apex 冲刺速度"）。
    /// </summary>
    public float HolsterMultiplier = 1.15f;

    /// <summary>
    /// **[Target] 地面驱动加速度 (m/s²，默认 14)**。
    ///
    /// wiki 明确：**蹲行 / 行走 / 冲刺的加速度完全相同，只有顶速不同**。
    /// 因此三个状态共用一个加速度值（拆成三个字段只是为了将来单独调）。
    ///
    /// ## v0.5.3：10 → 14（需求 m02395 第 2 点）
    ///
    /// 需求原文：「我感觉输入的最大加速度只有 10 m/s² 有点小，虽然比较真实但是
    /// 游戏性低了一点，这个能改吗？改了会影响整体物理化的系统吗？」
    ///
    /// **能改，而且不会破坏物理自洽** —— 因为它是**设计目标**（肌肉能给出的推力），
    /// 不是物理常数。真正与物理耦合的只有一条：抓地条件
    /// <code>μs · g · cosθ ≥ 驱动加速度</code>
    /// 也就是说"加速度提上去以后，鞋底必须抓得住"。本配置里
    /// <see cref="GroundStaticFrictionCoefficient"/> 就是按这条约束反推的
    /// （见它的注释），改这个值时要一起改。
    ///
    /// 影响面（都是**设计量**的连锁，不是物理错误）：
    ///   · 到顶速的时间：冲刺 6.604/14 = **0.47 s**（原 0.66 s）；
    ///   · 松手刹车第一段也用它（见 <see cref="BrakeRampTime"/>）；
    ///   · 上坡时它要先对抗 g·sinθ，坡越陡净加速度越小（30° 时 14 − 4.9 = 9.1）。
    /// </summary>
    public float GroundAccel = 14f;

    /// <summary>[Target] 冲刺加速度（wiki：与行走相同，保留独立字段便于调参）。</summary>
    public float SprintAccel = 14f;

    /// <summary>[Target] 蹲行加速度（wiki：与行走相同）。</summary>
    public float CrouchAccel = 14f;

    /// <summary>
    /// [B] 空中加速度系数。
    ///
    /// ⚠️ **这里做了一个必须说明的工程决定**：wiki 给出的游戏内变量是
    /// `Air acceleration = 500`、`Air speed = 60 (hu/s)`。
    /// 若直接套 Source 的 <c>accelspeed = accel × dt × wishSpeed</c>，
    /// 得到 500 × (1/60) × 1.524 = **12.7 m/s 每 tick** —— 荒谬（一 tick 加满）。
    /// 说明 Apex 内部对 `airAcceleration` 另做了一层单位缩放，而这个缩放系数**没有读到**。
    ///
    /// 因此本插件把 `airAcceleration` 归一化为本字段（默认取 Source 谱系默认 10，B 级），
    /// 而把**唯一有 A 级证据的量** `AirWishSpeed = 60 hu/s` 作为硬约束。
    /// 真正决定 air strafe 手感的是 `AirWishSpeed`（它限制了每 tick 最多能加多少投影速度）。
    /// </summary>
    public float AirAccel = 10f;

    /// <summary>
    /// [A] 空中 wishspeed (m/s) = 60 hu/s = 1.524。wiki Air strafe：「Air speed=60」。
    ///
    /// **这是 air strafe 的物理根因**：加速公式里有
    /// <c>addSpeed = wishSpeed − dot(velocity, wishDir)</c>，
    /// 而 wishSpeed 只有 1.524 m/s，所以只有当你的速度在 wishDir 上的投影小于 1.524 时才加速。
    /// 必须同步转视角让 wishDir 一直"贴"在速度矢量侧前方，才能持续吃到那点加速度。
    /// </summary>
    public float AirWishSpeed = 1.52400f;

    /// <summary>
    /// **[Target] 地面库仑摩擦系数 μ（默认 0.7）**。
    ///
    /// v0.5 需求（m01070 第 3 点）：「地面摩擦改真库仑 μ≈0.7（停距 3.18m，且随坡度/星球变）」。
    ///
    /// 物理模型：<c>a_brake = μ · g · cosθ</c>（θ = 地面倾角，cosθ 来自法向力）。
    ///   · 平地：a = 0.7 × 9.80665 = 6.865 m/s²
    ///   · 冲刺 6.604 m/s 松键停距 = v²/(2a) = **3.18 m**（原 Source 粘性模型的 ≈1.24 m）
    ///   · 坡度越陡摩擦越小（法向力小），星球重力越大刹车越狠 —— 两者都自动跟随
    ///
    /// 参考值：橡胶鞋底在混凝土上 0.6–0.9；湿滑地面 0.3；冰面 0.05。
    /// 这个值是默认值，世界里实现 <see cref="IFrictionWorld"/> 时脚下材质可以覆盖它。
    /// </summary>
    public float GroundFrictionCoefficient = 0.7f;

    // ═════════ v0.5.2 摩擦重做（需求 m01928 第 4 点）═════════════
    //
    // 需求原话：「人行走奔跑起身是静摩擦力是做正功的，因为人不是滑动，所以摩擦力产生的
    // 加速度应该是浮动的，而且作用于玩家输入方向。当玩家达到最大速度时，静摩擦力会归零，
    // 如果玩家要转向，静摩擦力会将玩家的水平速度限制在上限……当玩家停止输入时，静摩擦力
    // 将会与玩家当前移动方向相反，然后缓慢增大到最大静摩擦变为滑动摩擦，直到停止
    // （类似汽车刹车，先降低轮胎滚动速度，最后抱死刹车）。」

    /// <summary>
    /// **[Target] 最大静摩擦系数 μs（默认 1.9）**。
    ///
    /// 走路时脚是**钉在地上**的（不打滑），所以推动身体的是**静摩擦**（反作用力），
    /// 它做正功；只有超过 μs·N 才会打滑转成滑动摩擦。
    ///
    /// 为什么默认 1.9 &gt; 1：这个值不是"看起来像"，而是被**设计加速度**倒逼出来的 ——
    /// 要让正常地面"抓得住"（不打滑），必须满足
    /// <code>μs · g · cosθ ≥ GroundAccel</code>
    /// 取最陡可跑坡 30°（cos30°=0.866）⇒ μs ≥ 14 / (9.80665×0.866) = **1.65**。
    /// 取 1.9 留 15% 余量（45° 坡上 μs·g·cosθ = 13.2 &lt; 14 ⇒ 开始轻微打滑，符合
    /// "陡坡跑不快"的直觉）。
    ///
    /// ⚠️ 这个值是**跟着 <see cref="GroundAccel"/> 走的**：改驱动加速度必须同步改它，
    /// 否则正常地面会"抓不住地"（grip &lt; 1），走路变成轻微打滑。
    ///
    /// 参考值：橡胶底运动鞋在干燥粗糙混凝土上 μs ≈ 1.2–1.4（鞋纹咬合会让系数 &gt; 1）；
    /// 湿滑地面 0.3–0.5；冰面 0.05–0.15。
    /// </summary>
    public float GroundStaticFrictionCoefficient = 1.9f;

    /// <summary>
    /// 刹车时"从最大制动衰减到滑动摩擦"所需时间 (s，默认 0.25)。
    ///
    /// v0.5.3 需求（m02395 第 2 点）：「我希望玩家停止移动时先给一个速度反向的最大制动
    /// （比如刚刚的最大输入 10 m/s²）然后逐渐降低至当前滑动摩擦（6.86 m/s²），
    /// 这样直到人物停下」。
    ///
    /// 实现（<c>BrakeDecel</c>）：松开输入后制动加速度**起点 = 最大驱动加速度**
    /// （被 <c>μs·g·cosθ</c> 夹住，物理上不会超过静摩擦极限），
    /// 在 <see cref="BrakeRampTime"/> 内**线性衰减到** <c>μk·g·cosθ</c>，
    /// 之后保持滑动摩擦直到停住：
    /// <code>
    ///   a(t) = μk·g·cosθ + (a₀ − μk·g·cosθ) · (1 − min(1, t/T))
    ///   a₀   = min(μs·g·cosθ, GroundAccel)      // 平地 14 m/s²
    /// </code>
    ///
    /// 为什么是这个方向（先强后弱）：人主动停下时**先用力蹬地**（静摩擦能做功），
    /// 速度降下来以后脚才逐渐锁死成滑动；反过来（先弱后强）会让"刚松手那一下"
    /// 几乎不掉速，感觉像刹不住。
    ///
    /// 平地实测（v=6.6 m/s 松手）：第 1 tick 掉 0.233 m/s（=14·dt），
    /// 0.25 s 后衰减到 0.114 m/s/tick（=6.86·dt），总停距 ≈ 2.5 m。
    /// </summary>
    public float BrakeRampTime = 0.25f;

    /// <summary>
    /// 空中输入加速度 (m/s²，默认 1.2)。
    ///
    /// 需求：「空中输入也不会产生什么作用，只有很微小的偏移，基本上只能靠惯性飞跃」。
    /// 旧的 <see cref="AirAccel"/>(10) + <see cref="AirWishSpeed"/>(1.524) 组合是
    /// Source 谱系的 air-strafe：实测能把 6.6 m/s 加到 7.95 m/s（+20%），
    /// 明显违反"靠惯性"。
    ///
    /// 现在空中输入只负责**微调方向**，并且额外有
    /// <see cref="AirSteerSpeedAllowance"/> 的**速度增益上限**（见 StepAir）。
    /// </summary>
    public float AirControlAccel = 1.2f;

    /// <summary>
    /// 打滑耗散的**滑移速度参考值** (m/s，默认 1.0)。
    ///
    /// 脚相对地面滑移得越快，滑动摩擦耗散越强：
    /// <code>drag = μk·g·cosθ·(1 − grip)·(1 + v / SlipDragRefSpeed)</code>
    ///
    /// 这个 <c>(1 + v/vRef)</c> 项的存在让"打滑地面"有一个**稳定的终端速度**
    /// （推进 = 耗散），而不是慢慢爬到顶速、也不是完全动不了。
    /// 冰面（μk=0.05）实测终端速度 ≈ 1.1 m/s：能挪，但挪不快。
    /// </summary>
    public float SlipDragRefSpeed = 1.0f;

    /// <summary>
    /// 空中允许比离地时多出的速度 (m/s，默认 0.5)。
    ///
    /// 无论怎么转视角/按方向，空中水平速度都**不会超过**
    /// <c>离地速度 + 这个值</c> —— 于是"空中只能靠惯性，最多微调"成为一条硬不变量，
    /// 而不是"加速度调小所以看起来还行"。
    /// </summary>
    public float AirSteerSpeedAllowance = 0.5f;

    // ═════════ v0.5.2 体重（需求 m01928 第 4 点）═════════════

    /// <summary>
    /// **BMI 身体质量指数**（默认 21.7 = 正常区间 18.5–24.9 的中位数）。
    ///
    /// 需求：「我希望以国际身体体重指数 BMI = 体重(kg)/[身高(m)]² 来计算获得体重，
    /// 目前角色身高为 1.9m，BMI 的正常范围为 18.5-24.9，取中位数 21.7 计算，
    /// 角色体重应该为 78.337kg」。
    /// </summary>
    public float BodyBmi = 21.7f;

    /// <summary>体重覆盖值 (kg)。&gt;0 时忽略 BMI 计算（用于特殊角色/装备）。</summary>
    public float BodyMassOverride = 0f;

    /// <summary>
    /// 体重 (kg)：<c>BMI × 身高²</c>（默认 21.7 × 1.9² = **78.337 kg**）。
    ///
    /// ⚠ **重要（诚实的物理提醒）**：在纯移动系统里，质量会**在摩擦加速度里约掉**
    /// —— <c>a = μ·N/m = μ·g·cosθ</c>，与体重无关。所以体重**不改变**走跑滑的加减速曲线。
    /// 它真正起作用的地方是**力**：
    ///   · 调试渲染器里显示的法向力/摩擦力（N）；
    ///   · 未来木筏/刚体的冲量传递（<c>Δv = J/m</c>，轻的人被弹得更远）；
    ///   · 推箱子、被风吹、被爆炸推动这类"外力 ÷ 质量"的场景。
    /// 这些接口现在就按 N 记账，等接入刚体时直接可用。
    /// </summary>
    public float BodyMass => BodyMassOverride > 0f
        ? BodyMassOverride
        : BodyBmi * StandHeight * StandHeight;

    /// <summary>脚下法向力 (N) = m·g·cosθ。</summary>
    public float NormalForce(float cosT) => BodyMass * Gravity * Clamp01(cosT);

    /// <summary>最大静摩擦力 (N) = μs·N。走跑时身体能获得的推进力上限。</summary>
    public float MaxStaticFrictionForce(float cosT)
        => GroundStaticFrictionCoefficient * NormalForce(cosT);

    /// <summary>滑动摩擦力 (N) = μk·N。滑铲/抱死刹车/打滑时的大小。</summary>
    public float MaxKineticFrictionForce(float cosT)
        => GroundFrictionCoefficient * NormalForce(cosT);

    /// <summary>最大静摩擦能提供的加速度 (m/s²) = μs·g·cosθ（质量约掉了）。</summary>
    public float StaticFrictionAccel(float cosT)
        => GroundStaticFrictionCoefficient * Gravity * Clamp01(cosT);

    /// <summary>滑动摩擦加速度 (m/s²) = μk·g·cosθ。</summary>
    public float KineticFrictionAccel(float cosT)
        => GroundFrictionCoefficient * Gravity * Clamp01(cosT);

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

    /// <summary>
    /// [C] **主动急停**加成倍率（默认 2.0）。
    ///
    /// v0.5 需求（m01070 第 3 点）：「改真库仑 μ≈0.7 …+『主动急停』补手感」。
    /// 真库仑把松散松键的停距从 1.24 m 拉长到 3.18 m，手感会"刹不住"；
    /// 人类想停时本来就会主动蹬地制动，所以**输入方向与速度方向相反**时把 μ 乘这个倍率：
    /// <c>a_brake = 2.0 × 0.7 × g = 13.7 m/s²</c> ⇒ 6.6 m/s 停距 ≈ 1.59 m。
    ///
    /// 注意：滑铲**不吃**这个加成（需求 m00914 ②：「滑铲的时候不要有制动加成，
    /// 不然会影响持续滑行手感」）—— 滑铲有自己的库仑模型（见 StepSlide）。
    /// </summary>
    public float ActiveBrakeMultiplier = 2.0f;

    /// <summary>
    /// [C] **坡面合速度**的速度调节时间常数 τ（s，默认 0.25）。
    ///
    /// v0.5 需求（m00914 ⑥）：「我希望跑动和滑铲的时候用我的那套（基础移速随坡度变化
    /// + 重力加成），蹲走和正常行走时用制动防止摔倒那套，因为玩家可以操控人物是奔跑
    /// 还是行走……如果是慢慢走那就是玩家不希望摔倒，如果是奔跑和滑铲就是希望体验
    /// 斜坡带来的加速，不顾一切冲下斜坡」。
    ///
    /// 模型（**跑动** = 按着冲刺键且未蹲下时）：
    /// <code>
    ///   v_target = v_base + τ · g · sinθ · (下坡方向 · 期望方向)
    /// </code>
    ///   · 下坡（沿坡向下跑）：v_target &gt; v_base ⇒ 越跑越快；
    ///   · 上坡：v_target &lt; v_base ⇒ 明显掉速；
    ///   · 横向跑（等高线）：sinθ 项被点积吃掉 ⇒ 速度不变。
    ///
    /// 为什么是 τ·g·sinθ 而不是"直接加 g·sinθ 加速度"：
    ///   人是**速度调节器**，不是自由落体 —— 跑者会主动控制速度。
    ///   设肌肉调节增益 = 1/τ（速度偏差越大、纠正越猛），则稳态时
    ///   <c>g·sinθ = (v − v_base)/τ</c> ⇒ <c>v = v_base + τ·g·sinθ</c>。
    ///   这也是"坡度加成自动跟着星球重力走"的来源（换月球 g 小 ⇒ 加成小）。
    ///
    /// 数值（地球 g=9.80665、冲刺 6.604）：30° 下坡 7.83 / 上坡 5.38；
    ///   45° 下坡 8.34 / 上坡 4.87。**走（4.407）与蹲走（2.0）不吃这个加成** ——
    ///   那是"玩家不希望摔倒"的那一档，只保留库仑摩擦（见
    ///   <see cref="GroundFrictionCoefficient"/>）。
    /// </summary>
    public float SlopeSpeedTimeConstant = 0.25f;

    /// <summary>[A] 空中**没有**摩擦。wiki 引擎篇：「Apex does not feature any Air friction」。</summary>
    public float AirFriction = 0f;

    /// <summary>[A] 停止速度基准 (m/s) = 100 hu/s = 2.54。Source sv_stopspeed 同值。</summary>
    public float StopSpeed = 2.54000f;

    /// <summary>
    /// [C] 贴地吸附长度 (m)，等价 Godot 的 <c>floor_snap_length</c>。
    ///
    /// 两个作用：① 接地探测距离；② 命中后把胶囊**压到接触面**（留 1 mm 皮肤）。
    /// 只靠"向下速度"贴地时，探测距离远大于单 tick 下降量，角色会悬停在离地几厘米处落不下去。
    /// </summary>
    public float GroundSnapLength = 0.20f;

    /// <summary>[A] 可行走坡面法线点积阈值 = 0.7（= arccos(0.7) = 45.572°）。</summary>
    public float WalkableNormalDot = 0.7f;

    // ═════════════════════════════ 跳跃 ═════════════════════════════

    /// <summary>
    /// **[Target] 跳跃高度 (m) = 0.75**（v0.5 需求 m00952：「人物跳跃高度改成 0.75m，
    /// 滞空时间无所谓，高度达到 0.75m 就行」）。
    ///
    /// v0.4.x 是 56 hu = 1.4224（Apex 原版）。改到 0.75 之后滞空自然变短：
    /// 地球重力下 v = √(2·9.80665·0.75) = 3.835 m/s，滞空 ≈ 0.78 s。
    /// 空中按蹲可以收腿（<see cref="CrouchHeight"/>），所以能跳上比 0.75 m 矮的台子。
    /// </summary>
    public float JumpHeight = 0.75f;

    /// <summary>[C] 跳跃缓冲窗口 (s)：落地前按跳，落地瞬间自动触发。</summary>
    public float JumpBufferTime = 0.12f;

    /// <summary>[A] 土狼时间 (s) = 0.2。wiki Coyote time：「Coyote time lasts for 0.2sec」。</summary>
    public float CoyoteTime = 0.20f;

    // ── 跳跃疲劳（wiki 直接给出的游戏内变量） ──

    /// <summary>[A] 疲劳最短窗口 (s) = `antiMultiJumpTimeMin` = 0.15000006。</summary>
    public float JumpFatigueMinTime = 0.15f;

    /// <summary>[A] 疲劳衰减完成时间 (s) = `antiMultiJumpTimeMax` = 0.75。</summary>
    public float JumpFatigueMaxTime = 0.75f;

    /// <summary>[A] 疲劳最低跳跃高度比例 = `antiMultiJumpHeightFrac` = 0.30。</summary>
    public float JumpFatigueHeightFrac = 0.30f;

    /// <summary>[A] Bunnyhop 判定窗口 (s)：落地后此时长内离地算 bhop（游戏内 `skip_time` = 1s）。</summary>
    public float BunnyHopWindow = 1.0f;

    /// <summary>[A] Bunnyhop 减速上限 (m/s) = 100 hu/s = 2.54（wiki：超过冲刺速度时每次 bhop 扣这么多）。</summary>
    public float BunnyHopSpeedPenalty = 2.54f;

    /// <summary>[A] 跳跃 grace 的 cutoff 速度 (m/s)：超过它就不再施加 bhop 减速（`jump_grace_cutoff_speed` 的语义）。</summary>
    public float JumpGraceCutoffSpeed = 12.0f;

    /// <summary>[C] 落地后多久内"按住蹲再跳"算作 slidehop (s)。</summary>
    public float SlideHopWindowAfterLanding = 0.20f;

    // ═════════════════════════════ 体型（需求方口径，优先） ═════════════════════════════

    /// <summary>**需求方口径**：站立胶囊高度 1.90 m（v0.5 需求 m00952 从 1.95 改到 1.90）。</summary>
    public float StandHeight = 1.90f;

    /// <summary>**需求方口径**：蹲下胶囊高度 = 站立一半 = 0.95 m。</summary>
    public float CrouchHeight = 0.95f;

    /// <summary>**需求方口径**：眼位 = 胶囊顶端 − 0.10 m（站立时 1.85 m，蹲下时 0.875 m）。</summary>
    public float EyeHeightOffset = 0.10f;

    /// <summary>[A] 胶囊半径 (m) = 16 hu = 0.4064。wiki 引擎篇。</summary>
    public float CapsuleRadius = 0.406f;

    /// <summary>[C] 蹲下/站起过渡时间 (s)。</summary>
    public float CrouchTransitionTime = 0.12f;

    /// <summary>
    /// [A] 空中蹲下时胶囊**向中间收缩**（脚底上升）；地面上蹲下**向底部收缩**（脚底不动）。
    /// wiki 引擎篇：「Crouching on the ground shrinks it towards the bottom... mid air ... towards the middle」。
    /// 这是 slidehop 与 bhop 手感不同的根因。关掉它 = 一律脚底锚定（旧版行为）。
    /// </summary>
    public bool AirCrouchShrinksToMiddle = true;

    // ═════════════════════════════ 滑铲 ═════════════════════════════

    /// <summary>
    /// **滑铲触发门槛** (m/s)：判定用的是「**当前行走平面内**的移动速度」
    /// （速度在 [地面法线垂直平面] 上的投影大小），**不含** slideboost。
    ///
    /// v0.5 需求（m01202）：「滑铲入口是基于当前行走平面的移动速度
    /// （由基础移速 + 外置因素获得）判定是否超过滑铲阈值，再决定是否进入滑铲状态，
    /// 当允许进入滑铲状态后再添加 boost 速度」。
    ///
    /// 取值 5.0（原 200 hu/s = 5.08，按"取整"要求收敛到 5.0）。
    /// </summary>
    public float SlideMinStartSpeed = 5.0f;

    // ★ v0.4.2 删除了两个字段：SlideMinStartSpeedAirHorizontal(2.286) 与
    //   SlideMinStartSpeedAirVertical(5.08) —— 它们对应 Apex 的 Air Slide 门槛
    //   （`slideRequiredStartSpeedAir`：水平 > 90hu/s 且下坠 > 200hu/s），
    //   而空中滑铲整个机制已经在 v0.4.2 删除（见 ApexMovementCore.StepAir 的说明）。
    //
    //   为什么删而不是留：删掉入口之后这两个值**没有任何代码路径读取**，
    //   但它们在配置里仍然是一个显眼的公开旋钮 —— 后人改它会得到"改了没反应"，
    //   然后去怀疑是不是别的地方有 bug。逆向量出来的知识（90hu/s、200hu/s）
    //   放在这里当备注就够，不需要留一个假接口。

    /// <summary>
    /// Slideboost 增量 (m/s) = 150 hu/s = 3.81（水平面口径）。
    ///
    /// v0.5 需求（m01202）：「基于滑铲平面角度添加 boost 速度……这是基于投影的」。
    /// 也就是说 3.81 是**水平面**上的增量；真正加到"沿坡速度"上时要按坡度换算：
    /// <code>
    ///   boost_沿坡 = SlideBoostSpeed / cosθ      (θ = 坡面倾角)
    /// </code>
    /// 需求方给的例子：30° 坡、boost = √3 ⇒ 沿坡 boost = √3/cos30° = 2.0 m/s。
    /// 直觉：同样的"水平推进量"，在斜面上要走更长的坡面距离才能实现。
    /// </summary>
    public float SlideBoostSpeed = 3.81f;

    // ★ v0.5（需求 m01202）：「取消滑铲速度上限」。
    //
    //   原字段 SlideMaxSpeed = 10.16（400 hu/s）已**删除**：它只在 BeginSlide
    //   那一刻夹紧 boost 后的速度，现在入口速度 = 平面速度 + boost 且不再夹紧，
    //   滑铲期间的速度完全由坡面物理 a = g·sinθ − μ·g·cosθ 决定。
    //   保留一个"有旋钮但没人读"的死字段只会误导后来人（历史教训见本文件别处的说明）。

    /// <summary>[A] Slideboost 冷却 (s) = 2.0。wiki Slide：「the Slideboost has a 2sec cooldown」。</summary>
    public float SlideBoostCooldown = 2.0f;

    /// <summary>[C] 滑铲摩擦（远低于地面摩擦 —— 这是能滑远的原因）。</summary>
    /// <summary>
    /// 滑铲时地面材质的**库仑摩擦系数 μ**（默认 0.45）。
    ///
    /// v0.4.1 起滑铲按真实物理走：沿坡合加速度 <c>a = g·sinθ − μ·g·cosθ</c>。
    ///  - 平地 θ=0：匀减速 <c>μg ≈ 9.35 m/s²</c>（重力 20.78 × 0.45）
    ///  - 临界角 <c>tanθ = μ</c>：θ &lt; 24.2° 会停下来，θ &gt; 24.2° 越滑越快
    ///  - μ=0.05 是"冰面"，μ=0.9 是"粗糙水泥"
    ///
    /// 这个值是**默认值**：世界里实现 <see cref="IFrictionWorld"/> 时，
    /// 脚下那块地面可以覆盖它（不同材质不同 μ）。
    /// </summary>
    public float SlideFrictionCoefficient = 0.45f;

    /// <summary>
    /// **滑铲结束速度** = 蹲走基础移速（保底）。
    ///
    /// v0.5 需求（m01202）：「基础蹲走移速是保证滑铲被减速后的最慢移动速度（用来保底），
    /// 变成最慢移动速度后如果玩家还按着 ctrl，则会变成蹲伏状态，
    /// 如果玩家还按着 w+ctrl，则会变成蹲走状态」。
    ///
    /// 于是滑铲不会"突然降到 0"，而是滑到蹲走速度就无缝交还给地面状态机：
    /// 松 W 就蹲着（速度被地面摩擦继续吃掉），按 W 就接着蹲走。
    /// 原 SlideMinSpeed = 2.5 已删除（口径统一到 CrouchWalkSpeed）。
    /// </summary>
    public float SlideExitSpeed => CrouchWalkSpeed;

    /// <summary>[C] 滑铲最长持续时间 (s)。wiki 说滑铲本身**没有**计时器，这里只是防卡死兜底。</summary>
    public float SlideMaxDuration = 6.0f;

    /// <summary>[C] 滑铲期间的转向能力（相对正常加速的比例）。wiki：短暂方向输入几乎被忽略。</summary>
    public float SlideSteerFactor = 0.15f;

    /// <summary>[C] 滑铲期间反向输入的额外摩擦倍率。wiki：反向输入减速"非常剧烈"。</summary>
    public float SlideReverseInputPenalty = 3.0f;

    // ── 滑铲跳（slide jump / slidehop） ──

    /// <summary>
    /// [A] Slidejump 速度阈值 (m/s) = 350 hu/s = 8.89。
    /// wiki：跳跃时**只有**速度低于此值 **或** 进入滑铲已超过 <see cref="SlideJumpTimeWindow"/> 才叠加跳跃初速。
    /// </summary>
    public float SlideJumpSpeedThreshold = 8.89000f;

    /// <summary>[A] Slidejump 时间窗 (s) = 0.24。wiki：0.24sec after initiating the Slide。</summary>
    public float SlideJumpTimeWindow = 0.24f;

    /// <summary>[C] 滑铲跳速度保留系数（跳跃时水平速度乘以此值）。</summary>
    public float SlideJumpSpeedRetention = 1.0f;

    // ═════════════════════════════ 翻越 / 攀爬 ═════════════════════════════

    /// <summary>
    /// [C] **台阶**高度上限 (m) = 0.25（v0.4 需求方口径）。
    ///
    /// 需求原话：「≤0.25 m 视为台阶 → 直接平滑经过、无速度衰减；上台阶时水平速度
    /// 变为斜向上的速度再变回水平，**只有方向变化、大小不变**，不触发任何动作」。
    ///
    /// 三档障碍分级（v0.4 起，全部由需求方定义）：
    ///   · <c>≤ StepMaxHeight (0.25)</c> → 台阶：自动平滑跨越，速度大小不变（只倾斜方向）
    ///   · <c>(0.25, VaultMaxHeight (1.0)]</c> → 小障碍：按 W+Space 翻越，速度小幅削减后快速恢复
    ///   · <c>&gt; VaultMaxHeight (1.0)</c> → 墙：撞上去停住；W+Space 起跳后由 Climb 接管
    ///
    /// v0.3 及之前这里是 0.762（Apex 的 30 hu overwalkable），一个 0.7 m 的台子
    /// 会被"直接跨过去"，既不符合需求的三档分级，也让 ≤1 m 的小障碍失去意义。
    /// </summary>
    public float OverwalkableHeight = 0.25000f;

    /// <summary>
    /// [C] **小障碍**高度上限 (m) = 1.0（v0.4 需求方口径）。
    ///
    /// 高于这个值就是"墙"：贴墙按 W 只会撞停，必须 W+Space 起跳后走攀爬。
    /// 低于等于这个值：W+Space 直接翻越（Vault），**不触发攀爬**。
    /// </summary>
    public float VaultMaxHeight = 1.0f;

    /// <summary>
    /// [C] 翻越小障碍时的水平速度保留系数（0.85 = 只损失 15%）。
    ///
    /// 需求：「水平速度削减较小，翻越结束快速恢复满速」。
    /// 与"攀爬"形成对比：攀爬时水平速度直接掉到 0（需求原话）。
    /// </summary>
    public float VaultSpeedRetention = 0.85f;

    /// <summary>
    /// [C] 翻越后恢复到满速的时间 (s)。
    ///
    /// 实现方式：翻越结束时给一个 <see cref="VaultRecoverTime"/> 长的"加速宽限"，
    /// 这期间地面加速度放大，玩家 0.25 s 内就重新回到满速（需求：「快速恢复满速」）。
    /// </summary>
    public float VaultRecoverTime = 0.25f;

    /// <summary>[C] 翻越后加速宽限期的加速度倍率。</summary>
    public float VaultRecoverAccelBoost = 3.0f;

    /// <summary>
    /// [C] 台阶过渡的倾斜时长 (s)。
    ///
    /// 上台阶时速度方向从水平抬到 <c>θ = atan(b/a)</c> 再落回水平，
    /// 这个时长就是"抬起来 → 落回去"的总时间。0.12 s 约等于脚掌踩上台阶再蹬直的时间。
    /// </summary>
    public float StepTiltTime = 0.12f;

    /// <summary>
    /// [C] 台阶倾角的横向基准 a (m)：玩家脚底中心到脚底边缘的距离。
    ///
    /// 需求给的公式是 <c>tan(θ) = b / a</c>，其中 a = 玩家脚底中心到脚底边缘的横向距离。
    /// 默认取胶囊半径（= 脚底半径），因为胶囊的底面就是一个半径 R 的半球。
    /// 传 &lt;= 0 时运行时用 <see cref="CapsuleRadius"/>。
    /// </summary>
    public float StepTiltFootRadius = 0f;

    /// <summary>[A] Auto-mantle 高度上限 (m) = 80 hu = 2.032。wiki Auto mantle。</summary>
    public float AutoMantleMaxHeight = 2.03f;

    /// <summary>[A] 攀爬总高度上限 (m) = 273 hu = 6.9342。wiki 引擎篇 + Climb（climb space cutoff）。</summary>
    public float MantleMaxHeight = 6.93420f;

    /// <summary>[A] 攀爬附着偏移 (m) = 100 hu = 2.54（Attach offset，"个人体力上限"）。</summary>
    public float ClimbAttachOffset = 2.54f;

    /// <summary>[A] 攀爬结束额外上抛 (m) = 28 hu = 0.7112（End boost）。</summary>
    public float ClimbEndBoost = 0.71120f;

    /// <summary>[A] 判定"向上爬"的垂直速度阈值 (m/s) = 10 hu/s = 0.254。</summary>
    public float ClimbUpwardSpeedThreshold = 0.25400f;

    /// <summary>
    /// [A] 非向上爬的脱离计时 (s) = 1.0。wiki Climb。
    ///
    /// **v0.3 起不再是主脱离计时**：v0.2 拿它做「没按 W 就 1 秒后掉」，
    /// 手感变成"贴墙挂满一秒才掉"。v0.3 按需求改成
    /// <see cref="ClimbNoInputGrace"/>（短宽限），本字段只保留给
    /// 「按着输入但实际在向下/侧向爬」的情形。
    /// </summary>
    public float ClimbNonUpwardTimer = 1.0f;

    /// <summary>[C] 向上攀爬速度 (m/s)。</summary>
    public float ClimbSpeed = 2.0f;

    // ── v0.3：非强制攀爬（面向墙 + 空中 + 朝墙速度） ──

    /// <summary>
    /// [A] 允许**附着**攀爬的最大视角偏角 (deg) = 45.57。
    ///
    /// wiki Climb L40：「You can initiate a climb while looking up to 45.57° away
    /// from a wall」；L208 给出 45.57° = <c>arccos(0.7)</c>。
    ///
    /// 注意与 <see cref="MantleMaxViewAngle"/>（50°，Mantle grab 用）区分：
    /// v0.2 把两者混用，还在判定里乘了 0.5 的魔法系数，结果"只是路过墙边"
    /// 也会被吸住 —— v0.3 拆开并去掉魔法系数。
    /// </summary>
    public float ClimbAttachMaxViewAngle = 45.57f;

    /// <summary>
    /// [C] 附着攀爬所需的**最小朝墙速度** (m/s)。
    ///
    /// 需求：「必须角色在空中 + 有朝向墙壁的速度才触发」。
    /// wiki Climb L37-38 也印证：真正决定粘不粘墙的是**朝墙的动量**，而不是
    /// "有没有按前进键"（「You technically don't need a Forward input to Climb.
    /// Just moving towards a wall mid air will have you at least stick to it」）。
    ///
    /// 0.5 m/s ≈ 20 hu/s：轻擦墙不触发，正常跳向墙面的速度（>3 m/s）必然满足。
    /// </summary>
    public float ClimbMinApproachSpeed = 0.5f;

    /// <summary>
    /// [C] 松 W 后仍留在墙上的宽限时长 (s)。
    ///
    /// 需求：「按住 W 才向上爬；短时间没按 W 则取消攀爬开始下落」。
    /// 宽限让"点一下 W 再松"仍能完成一次抓附（wiki L37 的 brief stick），
    /// 超过这个时长才真正脱手下落。
    /// </summary>
    public float ClimbNoInputGrace = 0.22f;

    /// <summary>
    /// [C] "即将扒墙"时额外补爬的距离 (m)。
    ///
    /// 需求：「即将扒墙，则会继续上爬一小部分进入扒墙状态」。
    /// 检测到墙顶（前向探针打不到墙 / 到达高度上限）后，先补爬这段距离，
    /// 让手正好够到台沿，再切 <see cref="MovementState.Hang"/>。
    /// </summary>
    public float ClimbHangApproachDistance = 0.35f;

    /// <summary>[C] 上面那段"补爬"的时长 (s)。距离 / 时长 = 该阶段的爬升速度。</summary>
    public float ClimbHangApproachTime = 0.12f;

    /// <summary>
    /// [A] 重附着豁免角 (deg) = 25.842（wiki Climb L172-175）。
    ///
    /// 默认规则：脱离攀爬后必须**掉回上次 attach point 以下**才能再次附着；
    /// 但若新表面与旧表面的法线夹角 ≥ 此值，视为"换了面"，豁免该限制。
    /// </summary>
    public float ClimbReattachNormalAngle = 25.842f;

    /// <summary>[C] 是否允许从攀爬过渡到扒墙（Hang）。关掉就退回"到顶直接弹上去"。</summary>
    public bool ClimbToHangEnabled = true;

    /// <summary>[A] 允许触发翻越的最大视角偏角 (deg) = 50。wiki Mantle grab：「within 50° to each side」。</summary>
    public float MantleMaxViewAngle = 50f;

    /// <summary>[A] Mantle grab 时长 (s) = 0.45。wiki Mantle。</summary>
    public float MantleGrabTime = 0.45f;

    /// <summary>[A] Mantle pull 时长 (s) = 0.45。wiki Mantle。</summary>
    public float MantlePullTime = 0.45f;

    /// <summary>[C] grab 占整段翻越时长的比例（两段式轨迹的切分点）。</summary>
    public float MantleGrabFraction = 0.45f;

    /// <summary>[C] <see cref="MantleClass.Below"/> 档总时长 (s)（对应 `mantleDurationBelow`）。</summary>
    public float MantleDurationBelow = 0.55f;

    /// <summary>[C] <see cref="MantleClass.Level"/> 档总时长 (s)（对应 `mantleDurationLevel`）。</summary>
    public float MantleDurationLevel = 0.75f;

    /// <summary>[C] <see cref="MantleClass.Above"/> 档总时长 (s)（对应 `mantleDurationAbove`）。</summary>
    public float MantleDurationAbove = 0.90f;

    /// <summary>[C] <see cref="MantleClass.High"/> 档总时长 (s)（对应 `mantleDurationHigh`）。</summary>
    public float MantleDurationHigh = 1.20f;

    /// <summary>[C] 分档阈值①：台面高度 ≤ 此值 → <see cref="MantleClass.Below"/>。</summary>
    public float MantleLevelHeight = 0.975f;

    /// <summary>[C] 分档阈值②：台面高度 ≤ 此值 → <see cref="MantleClass.Level"/>。</summary>
    public float MantleAboveHeight = 1.95f;

    /// <summary>[C] 分档阈值③：台面高度 ≤ 此值 → <see cref="MantleClass.Above"/>，否则 <see cref="MantleClass.High"/>。</summary>
    public float MantleHighHeight = 3.45f;

    /// <summary>[C] 翻越结束时的前推距离 (m)。</summary>
    public float MantleForwardOffset = 0.55f;

    /// <summary>[C] 触发翻越/攀爬的最大前方探测距离 (m)。</summary>
    public float TraversalReach = 0.9f;

    /// <summary>[C] 翻越/攀爬冷却 (s)。</summary>
    public float TraversalCooldown = 0.2f;

    /// <summary>[C] 松手后是否进入挂边（Mantle hang）而不是自动完成。</summary>
    public bool EnableMantleHang = true;

    // ── 窗口翻越（TRAVERSAL_WINDOW） ──

    /// <summary>[C] 是否启用窗口翻越。</summary>
    public bool WindowTraversalEnabled = true;

    /// <summary>[C] 窗口翻越时长 (s)（对应 `traversal_window_duration`）。</summary>
    public float WindowDuration = 0.70f;

    /// <summary>[C] 穿过窗口后的前推距离 (m)（对应 `traversal_window_forward_offset`）。</summary>
    public float WindowForwardOffset = 1.20f;

    /// <summary>[C] 穿过窗口时的侧向偏移 (m)（对应 `traversal_window_sideways_offset`）。</summary>
    public float WindowSidewaysOffset = 0.0f;

    /// <summary>[C] 手部垂直偏移 (m)（对应 `traversal_window_hand_vertical_offset`）。</summary>
    public float WindowHandVerticalOffset = 0.0f;

    /// <summary>[C] 窗口翻越的收尾角度 (deg)（对应 `traversal_window_finish_angle`）。</summary>
    public float WindowFinishAngle = 0.0f;

    /// <summary>[C] 窗口开口的最小宽度 (m)（对应 `traversal_hand_required_width`）。</summary>
    public float WindowRequiredWidth = 1.0f;

    // ═════════════════════════════ 落地与 Fallstun ═════════════════════════════

    /// <summary>[A] 落地分档：低 (m)。对应 `landDistLow` 的量级。</summary>
    public float LandDistLow = 2.0f;

    /// <summary>[A] 落地分档：中 (m)。对应 `landDistMedium`。</summary>
    public float LandDistMedium = 5.0f;

    /// <summary>[A] 落地分档：高 (m)。对应 `landDistHigh`。</summary>
    public float LandDistHigh = 10.0f;

    /// <summary>[A] 硬着陆阈值 (m)。对应 `hardFallDist`。</summary>
    public float HardFallDist = 20.32f;

    /// <summary>[A] Fallstun 起始下落高度 (m) = 300 hu = 7.62。wiki Fallstun。</summary>
    public float FallStunMinHeight = 7.62f;

    /// <summary>[A] Fallstun 满值下落高度 (m) = 800 hu = 20.32。wiki Fallstun。</summary>
    public float FallStunMaxHeight = 20.32f;

    /// <summary>[A] Fallstun 最长持续时间 (s) = 1.0。wiki Fallstun。</summary>
    public float FallStunMaxDuration = 1.0f;

    /// <summary>[C] Fallstun 期间的顶速比例。wiki：从 0 到"完全减速"。</summary>
    public float FallStunSpeedScale = 0.35f;

    // ═════════════════════════════ 固定 tick ═════════════════════════════

    /// <summary>
    /// **钉死的移动 tick 频率**：60 Hz。
    ///
    /// 需求原话：「移动 tick 我希望固定为 60 tick/s，以这个为基准」。
    /// 因此它不是一个"可调手感参数"，而是求解器时间轴的定义 ——
    /// <see cref="TickRate"/> 的默认值就是它，<c>ApexPlayer</c> 也会拒绝被改小/改大。
    /// 要省性能请降 <see cref="TraversalProbeInterval"/>（昂贵探针的抽样率），
    /// 而不是改这个数，否则所有按秒标定的 A 级数值（跳跃高度、滑铲时长…）都会失真。
    /// </summary>
    public const int PinnedTickRate = 60;

    /// <summary>钉死的追赶上限（防死亡螺旋）。60Hz 下单帧最多补 4 tick ≈ 66 ms 卡顿。</summary>
    public const int PinnedMaxCatchUpTicks = 4;

    /// <summary>
    /// [C] **移动求解器的固定 tick 频率 (Hz)，v0.3 起恒为 60**。
    ///
    /// 需求：移动 tick 固定 60 tick/s 作为基准，不要跟随工程物理频率改变；
    /// 性能开销大的子系统可以单独降到 30 tick/s（见
    /// <see cref="TraversalProbeInterval"/>）。
    ///
    /// <c>player/ApexPlayer</c> 用累加器（<c>ApexTickClock</c>）把渲染/物理帧
    /// 折算成固定 60Hz 的 tick，所以工程把 physics_ticks_per_second 设成 30 或 144
    /// 都不影响求解器时间轴 —— 只影响每帧要追赶几个 tick。
    /// </summary>
    public int TickRate = PinnedTickRate;

    /// <summary>[C] 单帧最多追赶的 tick 数（防死亡螺旋）。</summary>
    public int MaxCatchUpTicks = PinnedMaxCatchUpTicks;

    /// <summary>[C] 是否在 <see cref="ApexMovementCore.Step"/> 内自动尝试翻越/攀爬。</summary>
    public bool AutoTraverse = true;

    /// <summary>
    /// [C] **翻越探针的抽样间隔**（单位：tick）。2 = 每 2 个 tick 探一次 → 30 Hz。
    ///
    /// 翻越/窗口探测是整个求解器里最贵的一段（窗口扫描 12 条竖直探针 + 若干辅助射线，
    /// 攀爬 6 点向内扫描）。它只是"有没有可翻的东西"的**机会检测**，不推进状态，
    /// 因此抽到 30 Hz 对玩法零影响，却把最贵的一段开销直接砍半 ——
    /// 这就是需求里"性能开销大的地方可以降到 30 tick/s"的落点。
    ///
    /// 1 = 每 tick（60 Hz，最灵敏），4 = 15 Hz（更省）。
    /// </summary>
    public int TraversalProbeInterval = 2;

    /// <summary>
    /// [C] **攀爬附着探针的抽样间隔**（单位：tick）。默认 1 = 每 tick。
    ///
    /// 附着只是 1 条射线 + 几次点积，很便宜；而它决定"贴墙瞬间抓不抓住"的手感，
    /// 所以默认不降频。真需要省性能时调到 2。
    /// </summary>
    public int ClimbProbeInterval = 1;

    /// <summary>
    /// [C] **攀爬到顶探针的抽样间隔**（单位：tick）。默认 **2 = 30 Hz**。
    ///
    /// 攀爬期间每 tick 都要打一条"前面还有墙吗"的射线（到顶检测）。它是
    /// 持续开销（整段攀爬都在跑），而墙顶不会一帧之内消失，抽到 30 Hz 完全够用。
    /// 攀爬附着探针（<see cref="ClimbProbeInterval"/>）保持 1，因为它是一次性的、
    /// 且直接决定"贴墙那一瞬抓不抓住"。
    /// </summary>
    public int ClimbWallProbeInterval = 2;

    /// <summary>
    /// [C] **打滑体积采样的抽样间隔**（单位：tick）。默认 **2 = 30 Hz**。
    ///
    /// 打滑体积是静态的（关卡里的滑道/冰面），采样频率不影响判定正确性；
    /// 但它是每 tick 都要问一次世界的持续开销，降到 30 Hz 省一半。
    /// </summary>
    public int SlipProbeInterval = 2;

    // ═════════════════ 打滑（Slip / C_TriggerSlipSphere） ═════════════════
    //
    // 逆向依据（docs/REVERSE-ENGINEERING.md §2.5.10）：
    //   体积：DT_TriggerSlipSphere / C_TriggerSlipSphere / trigger_slip_sphere，
    //         字段 m_slipSpeed / m_slipAcceleration / m_defaultSlipDirection
    //   玩家：m_touchingSlipTriggersCount / Speed[] / Acceleration[] / Direction[] / Origin[]，
    //         聚合 m_touchingSlipTriggersSpeed / Acceleration / Direction，
    //         以及 m_slipAirRestrictTime / m_slipAirRestrictDirection
    //   全局：slipAirRestrictDuration、slipSpeed、slipAcceleration
    //
    // 语义：走到"打滑体积"里 → 摩擦失效、被体积的 speed/accel 拽向体积方向；
    //       离开体积后 m_slipAirRestrictTime 内，空中转向被限制在离开时的方向附近。

    /// <summary>
    /// [C] 离开打滑体积后，"空中方向限制"持续多久 (s)（<c>slipAirRestrictDuration</c>，
    /// 数值未读到，取 0.6s 手感值）。
    /// </summary>
    public float SlipAirRestrictDuration = 0.6f;

    /// <summary>
    /// [C] 记录"离开方向"所需的最小水平速度 (m/s)。太慢（几乎站着）就沿用体积方向，
    /// 免得记录出一个抖动的零向量。
    /// </summary>
    public float SlipMinSpeedToRecordDirection = 1.0f;

    /// <summary>
    /// [C] 方向限制的**圆锥半角** (deg)。离开滑区后的空中加速只能落在这个圆锥内，
    /// 而不是完全锁死方向（完全锁死会让玩家觉得"失控"）。
    /// </summary>
    public float SlipAirRestrictConeDeg = 35f;

    /// <summary>[C] 打滑态的转向能力（0 = 完全不能转向，1 = 和地面一样）。默认 0.15。</summary>
    public float SlipSteerFactor = 0.15f;

    // ═════════════════ 扒墙 / 悬垂（Wall hang / dangle） ═════════════════
    //
    // 逆向依据（docs/REVERSE-ENGINEERING.md §2.5.11）：
    //   m_wallHanging / m_wallHangTime / IsWallHanging / m_wallDangleClimbProgressFloor
    //   ACT_MP_WALLHANG_FRONT / UP / RIGHT / LEFT / IDLE  ← 5 个姿势 = 可上下左右移动
    //   sound_wallHangStart / Slip / Complete / Fall     ← 挂上 / 下滑 / 完成 / 掉落
    //   spread_wallhanging                               ← 挂着时仍可射击
    //   automantle_disable_hang                          ← 自动翻越不允许转 hang

    /// <summary>[C] 是否启用扒墙（关闭后空中按蹲不再抓墙）。</summary>
    public bool WallHangEnabled = true;

    /// <summary>
    /// [C] 扒墙是否**必须按住蹲键**。
    ///
    /// **v0.4 起默认 false** —— 需求原话：「扒墙我希望是默认扒墙，不需要按任何按键」。
    /// 保留字段是为了让别的项目可以回到 v0.3 的"显式按 Ctrl 才挂"。
    /// </summary>
    public bool WallHangRequiresCrouch = false;

    /// <summary>
    /// [C] 扒墙待机时，**多久没有任何输入就掉下去** (s)。
    ///
    /// 需求：「如果持续一小段时间没有按键输入，就会掉落」。
    /// 1.2 s 足够让"松手后想按 A/D 横移"的人来得及，又不会让人一直白挂着。
    /// 注意这是**无输入**计时，不是握力计时：按着 A/D 或 W 都会重置它。
    /// </summary>
    public float WallHangIdleFallTime = 1.2f;

    /// <summary>
    /// [C] 只横移（A/D）不往上爬时，握力从满到空要多久 (s)。
    ///
    /// 需求只规定了"无输入会掉"，横移显然不该和无输入一样快掉 ——
    /// 6 s 允许玩家沿着一面长墙横着挪一段，但不会无限期挂着。
    /// </summary>
    public float WallHangLateralFallTime = 6.0f;

    /// <summary>
    /// [C] 按 W 上爬时，握力从空回满要多久 (s)。
    ///
    /// 对应 <c>m_wallDangleClimbProgressFloor</c>：往上爬 = 把手再抓高一点，
    /// 把已经下滑掉的进度收回来。2.5 s 回满 ≈ 一次"喘口气再往上"的节奏。
    /// </summary>
    public float WallHangGripRestoreTime = 2.5f;

    /// <summary>[A] 扒墙的最大视角偏角 (deg)。与攀爬同源 = arccos(0.7)。</summary>
    public float WallHangMaxViewAngle = 45.57f;

    /// <summary>[C] 扒墙的前向抓取距离 (m)（在胶囊半径之外还要够到多远）。</summary>
    public float WallHangReach = 0.9f;

    /// <summary>
    /// [C] 抓墙时允许的"背离墙面"速度上限 (m/s)。v0.4.2 新增。
    ///
    /// 判据：<c>approach = -dot(水平速度, 墙面法线)</c>
    ///   · approach &gt; 0 ⇒ 正在靠近墙（前进 / 贴墙跳）⇒ 抓
    ///   · approach &lt; -WallGrabMaxRecedeSpeed ⇒ 正在远离墙 ⇒ 不抓
    ///   · |approach| ≤ 该值 ⇒ 垂直下落 / 几乎静止 ⇒ 抓（保留"下落自动扒住"）
    ///
    /// 修的是「站在高台上按住 S 倒退掉下去，被台子侧面吸住一小会儿」：
    /// 倒退时前向射线恰好打在刚离开的那块台的侧面上（人已越过边缘、台子在身后，
    /// 但朝向没变 ⇒ 台子落在"正前方"），旧判定只看"面向墙 + 正在下落"，
    /// 于是把人吸住了。按着 W 时无条件放行（W 就是"我要往前"的明确意图）。
    /// </summary>
    public float WallGrabMaxRecedeSpeed = 0.05f;

    /// <summary>
    /// [C] 抓墙要求的最小**离地间隙** (m)：脚底到脚下地面的距离小于这个值就不抓。
    ///
    /// 为什么需要：扒墙默认化之后，"站在墙根下"与"挂在墙上"在几何上只差几厘米 ——
    /// 玩家贴着墙走下一个小坡、或者落地那一 tick 还没被贴地探针吸附，都会满足
    /// "空中 + 够得到墙 + 正在下落"，于是人被凭空吸到墙上去。
    /// 加一条脚下探针把这两种情况彻底分开：脚底下就是地面 ⇒ 该落地，不该挂墙。
    /// </summary>
    /// ★ v0.5 二次调整（用户 m01300 BUG 2「疲劳跳期间无法攀爬」）：
    ///   上一版按跳跃能力把 0.5 缩到 0.30，结果是**任何低于 0.30 m 的腾空都抓不到墙**。
    ///   跳跃疲劳（antiMultiJumpHeightFrac = 0.30）下连续跳只有 0.75×0.30 ≈ 0.23 m 高，
    ///   永远够不到 0.30 —— 实机表现就是「疲劳跳期间完全无法攀爬」。
    ///   而且未来换超重力星球（g 越大跳得越低）会整块失效。
    ///
    ///   需求原话：「我希望改成只要跳了就攀爬」。所以现在这道门**只对"非跳跃"的腾空生效**：
    ///   · **跳出来的**腾空（<c>_leftGroundByJump</c>）→ 完全豁免，跳多低都能抓；
    ///   · 走下台沿 / 被推落 / 生成在空中 → 仍然要求 0.30 m 间隙，
    ///     防止「贴着墙走下小台阶被凭空吸到墙上」（这条门当初就是为它加的）。
    ///
    ///   这样「跳跃高度」与「能不能抓墙」彻底解耦：换星球、调跳跃高度、
    ///   跳跃疲劳都不会再影响攀爬可用性。
    public float WallGrabMinGroundClearance = 0.30f;

    /// <summary>[C] 沿墙左右横移的速度 (m/s)（`ACT_MP_WALLHANG_LEFT/RIGHT`）。</summary>
    public float WallHangShimmySpeed = 0.8f;

    /// <summary>[C] 单次扒墙的最长持续时间 (s)（<c>m_wallHangTime</c> 的上限）。</summary>
    public float WallHangMaxTime = 6.0f;

    /// <summary>
    /// [C] 松手后的**下滑速度** (m/s)。对应 <c>sound_wallHangSlip</c>：
    /// 挂着不动会缓慢往下滑，而不是像钉子一样钉住。
    /// </summary>
    public float WallHangSlipSpeed = 0.22f;

    /// <summary>
    /// [C] 累计下滑多少米后脱手（对应 <c>m_wallDangleClimbProgressFloor</c>：
    /// "能爬升的进度下限"，滑过这条线就抓不住了）。
    /// </summary>
    public float WallHangMaxSlipDistance = 1.2f;

    /// <summary>
    /// [C] 扒墙时按 W 是否能**回补**握力（抵消下滑）。
    /// 打开后"按 W 往上爬"同时把已经滑下去的进度拉回来，手感更宽容。
    /// </summary>
    public bool WallHangClimbRestoresGrip = true;

    /// <summary>
    /// [C] 是否禁止在"可自动翻越的矮墙"上扒墙。
    /// 对应 <c>automantle_disable_hang</c>：能自动翻过去就别先挂那儿。
    /// </summary>
    public bool WallHangDisableWhenAutoMantleable = true;

    /// <summary>[C] 脱手后再次扒墙的冷却 (s)，避免贴墙连点 Ctrl 反复挂。</summary>
    public float WallHangReattachCooldown = 0.35f;

    /// <summary>[C] 从扒墙"完成"（翻上去）时给的前推速度 (m/s)（`sound_wallHangComplete`）。</summary>
    public float WallHangCompleteForwardSpeed = 1.2f;

    /// <summary>
    /// 步态模型参数（v0.4）。走路/奔跑的步幅、步频、腾空比与各方向抖动幅度
    /// 全部由 <see cref="GaitModel"/> 按「身高 + 速度」推导，这里是它的常数表。
    /// 推导过程与数值落点见 <see cref="GaitParams"/> 的类型注释。
    /// </summary>
    public GaitParams Gait = GaitParams.Default;

    /// <summary>固定 tick 时长 (s)。</summary>
    public float FixedDelta => 1.0f / TickRate;

    public MovementConfig Clone() => (MovementConfig)MemberwiseClone();

    /// <summary>
    /// 由**设计目标高度**反解初速。
    ///
    /// ★ v0.5（需求 m01070）：「我希望的是玩家跳起来后，**脚底板离地面 0.75 m**」——
    ///   也就是说 <see cref="JumpHeight"/> 的口径是**实测峰高**，不是解析值。
    ///
    /// 连续解是 <c>v = √(2gh)</c>；但本项目的积分是**半隐式欧拉**
    /// （StepAir 里先 <c>v -= g·dt</c> 再位移），离散峰高会比解析值多
    /// <c>v·dt/2 + g·dt²/8</c>（60 Hz、h=0.75 时约 +3.3 cm，+4.4%）。
    /// 所以要显式扣掉这个 O(dt) 偏置：
    ///
    /// <code>
    ///   apex = v²/(2g) + v·dt/2 + g·dt²/8 = h
    ///   ⇒ 解二次式得 v = √(2gh) − g·dt/2      （dt² 项自动抵消）
    /// </code>
    ///
    /// 例：h=0.75、g=9.80665、dt=1/60 ⇒ v = 3.8354 − 0.0817 = 3.7537 m/s，
    /// 实测峰高 0.7502 m（<c>PhysicsTest</c> ⑯ 复核）。
    /// </summary>
    public float JumpVelocity
        => MathF.Sqrt(2f * PlanetProfile.Earth.SurfaceGravity * JumpHeight)
         - PlanetProfile.Earth.SurfaceGravity * FixedDelta * 0.5f;

    /// <summary>
    /// **换星球后实际能跳多高**（m）—— 固定起跳速度、不同重力下的弹道峰高。
    ///
    /// 需求（m01619）原话：「实际上是不同的星球有不同的重力加速度，然后人起跳的力度
    /// （加速度）是一样的，不同星球的跳跃高度不一样，但是人起跳的速度无法直接得出，
    /// 所以要用地球的合理跳高数值配合地球的重力反推人体想跳跃时使用的力度，
    /// 这样就能得出人干特定事情的固定的力，然后再把这个力应用到不同星球上，
    /// 结合不同星球的重力得出不同的跳跃高度效果」。
    ///
    /// 所以物理链是：
    /// <code>
    ///   地球设计高度 0.75 m + 地球 g ⇒ 起跳速度 v（= 人体腿部冲量，跨星球不变）
    ///   ⇒ 其它星球：apex = v²/(2·g_planet)  （重力越小跳得越高）
    /// </code>
    /// 这个属性就是那个 **apex**（用与求解器相同的半隐式欧拉离散求和算，不是解析值）：
    /// <code>
    ///   y = (m+1)·v·dt − g·dt²·m(m+1)/2,   m = ⌈v/(g·dt)⌉
    /// </code>
    /// 地球 0.750、月球 4.35、火星 1.90、Apex 重力 0.34（实测值见 <c>PhysicsTest</c> ⑫）。
    /// </summary>
    public float JumpApexOnPlanet(float gravity)
    {
        if (gravity <= 1e-6f) return float.PositiveInfinity;
        float v = JumpVelocity;
        float dt = FixedDelta;
        float step = v / (gravity * dt);
        int m = (int)MathF.Ceiling(step - 1e-4f);
        if (m < 0) m = 0;
        return (m + 1) * v * dt - gravity * dt * dt * m * (m + 1) * 0.5f;
    }

    /// <summary>当前星球上的实际跳跃峰高 (m)。</summary>
    public float JumpApex => JumpApexOnPlanet(Gravity);

    /// <summary>当前星球上的理论滞空时间 (s) = 2v/g（离地到回到同高度）。</summary>
    public float JumpAirTime => 2f * EffectiveJumpVelocity / Gravity;

    // ═════════════════ 起跳：有限加速时间（需求 m00460）═════════════════

    /// <summary>
    /// 起跳的**有限加速时间** (s)。**0 = 瞬时冲量**（Apex 现行行为，也是默认值）。
    ///
    /// 需求方原话：「可以先给一个合适的短暂加速时间，如 0.2 秒内匀加速到 14 m/s，
    /// 之后按物理重力减速下落」。设为 &gt;0 后，起跳不再是一次速度突变，而是持续
    /// <see cref="JumpAccelTicks"/> 个 tick 的**匀加速**（推力恒定），期间重力照常作用。
    ///
    /// **取值必须落在 FixedDelta 的整数倍上**，否则不同帧率的跳跃高度不一致
    /// （会破坏 <c>DeterminismTest</c> 的帧率不变性断言）。这里自动四舍五入：
    /// <see cref="JumpAccelTicks"/> = round(JumpAccelTime / FixedDelta)，夹在 [0, 60]。
    /// </summary>
    public float JumpAccelTime = 0f;

    /// <summary>起跳加速段占用的 tick 数（由 <see cref="JumpAccelTime"/> 四舍五入，夹 [0,60]）。</summary>
    public int JumpAccelTicks => Math.Clamp((int)MathF.Round(JumpAccelTime / FixedDelta), 0, 60);

    /// <summary>起跳加速段实际时长 (s) = <see cref="JumpAccelTicks"/> × FixedDelta。</summary>
    public float JumpAccelTimeActual => JumpAccelTicks * FixedDelta;

    /// <summary>
    /// 达到 <see cref="JumpHeight"/> 所需的**加速段出口速度** (m/s)。
    ///
    /// 纯冲量（t_a = 0）：v = √(2gh)。
    /// 有限加速（t_a &gt; 0）：加速段 T 内身体已经上升了 ½·u·T，剩余部分靠惯性，
    /// 所以总峰高是
    /// <code>
    ///     apex = u²/(2g) + u·T/2 = h
    ///     ⇒ u = (−g·T + √(g²T² + 8gh)) / 2
    /// </code>
    /// 其中 u 是加速段结束那一刻的速度（**不是**推力对应速度）。
    /// 例：h=10 m、g=9.8、T=0.2 s ⇒ u=13.054 m/s（而不是 √(2gh)=14.000）。
    /// </summary>
    public float JumpRampExitVelocity
    {
        get
        {
            // ★ v0.5（需求 m01619）：出口速度就是**固定的起跳速度** <see cref="JumpVelocity"/>，
            //   与星球无关。有限加速时间只是把同一份冲量**分摊到 n 个 tick**（推力更小、
            //   时间更长），它不改变"人的爆发力"这个物理量 —— 换星球也不该变。
            //
            //   （v0.4.3 的写法是 (−gT+√(g²T²+8gh))/2，那是"给定高度 h、在当前重力下反解"，
            //    会随星球重力变 ⇒ 与"固定力度"矛盾，已改。）
            return JumpVelocity;
        }
    }

    /// <summary>
    /// 加速段需要的**总向上加速度** (m/s²)，已含抵消重力那一份，并且做了**离散补偿**。
    ///
    /// 连续解是 <c>a = u/T + g</c>（加速段里重力同时在拉，推力要额外抵消 g）。
    /// 但起跳那一 tick 是**先给推力、不扣重力**的（否则 vy=0 会被贴地吸附当成没起跳），
    /// 所以整个加速段少扣了一次 g·dt —— 等价于出口速度多了 g·dt。
    /// 补偿办法是把推力降一点点：
    /// <code>
    ///     a = u/T + g·(1 − 1/n)        （n = 加速段 tick 数）
    /// </code>
    /// 这样第 n tick 结束时速度**逐位等于** u：<c>n·a·dt − (n−1)·g·dt = u</c>。
    ///
    /// "起跳爆发力"就是 a / g（默认配置 h=1.4224、t_a=0.2 s ⇒ 约 1.33 g，
    /// 比人类起跳的 2–3 g 还温和；这也是"0.2 s 的加速在体感上仍接近冲量"的原因）。
    /// </summary>
    public float JumpRampThrust
    {
        get
        {
            int n = JumpAccelTicks;
            if (n <= 0) return 0f;
            return JumpRampExitVelocity / JumpAccelTimeActual + Gravity * (1f - 1f / n);
        }
    }

    /// <summary>
    /// 起跳实际使用的**离地速度**（含加速时间修正）。<c>DoJump</c> 用这个值。
    /// 默认配置（t_a=0）下逐位等于 <see cref="JumpVelocity"/>。
    /// </summary>
    public float EffectiveJumpVelocity => JumpAccelTicks <= 0 ? JumpVelocity : JumpRampExitVelocity;

    /// <summary>
    /// **实际会跳多高**（m）。
    ///
    /// <see cref="JumpHeight"/> 是**解析目标**；离散积分（半隐式欧拉）会额外多跳
    /// <c>v₀·dt/2 + g·dt²/8</c>（起跳 tick 先位移后加重力，加辛欧拉的 O(dt) 偏置），
    /// 60 Hz 下约 **+4.5%**：标称 1.4224 → 实测 1.4869。
    /// 把这个差值显式暴露出来，调参的人就不会被"标称 vs 实际"困惑。
    ///
    /// 加速模式（t_a &gt; 0）下离散偏置的形式不同，这里退回解析目标值
    /// （实测偏差 &lt; 2%，由 <c>PhysicsTest</c> 复核）。
    /// </summary>
    public float EffectiveJumpHeight
    {
        get
        {
            int n = JumpAccelTicks;
            if (n <= 0) return JumpApex;                 // 纯冲量：就是当前星球的弹道峰高

            // 加速模式：加速段位移 + 剩余惯性段位移（都用离散求和，不是连续积分）
            //
            //   加速段：v_k = (k+1)·a·dt − k·g·dt  ⇒  Σ v_k·dt
            //          = a·dt²·n(n+1)/2 − g·dt²·n(n−1)/2
            //   惯性段：从 u 起按当前星球重力自由上升，直到竖直速度 ≤ 0
            float dt2 = FixedDelta * FixedDelta;
            float a = JumpRampThrust;
            float u = JumpRampExitVelocity;
            float rampRise = a * dt2 * n * (n + 1) * 0.5f
                           - Gravity * dt2 * n * (n - 1) * 0.5f;

            float gdt = Gravity * FixedDelta;
            int m = gdt > 1e-6f ? (int)MathF.Ceiling(u / gdt - 1e-4f) : 0;
            if (m < 0) m = 0;
            float coastRise = m * u * FixedDelta - Gravity * dt2 * m * (m + 1) * 0.5f;
            return rampRise + coastRise;
        }
    }

    // ═════════════════ 由物理量反推的"可读"派生量 ═════════════════

    /// <summary>
    /// 滑铲的**临界坡角** (deg) = arctan(μ)。
    /// 比 μ 本身直观：坡度小于它滑铲必然停下，大于它一路加速。
    /// 默认 μ=0.45 ⇒ 24.23°（实测 dv/dt 在 24° 为 −0.09、30° 为 +2.29，符号在此翻转）。
    /// </summary>
    public float SlideCriticalAngleDeg =>
        MathF.Atan(SlideFrictionCoefficient) * 180f / MathF.PI;

    /// <summary>
    /// 地面加速到顶速所需时间 (s) ≈ 1/<see cref="GroundAccel"/>。
    /// 这是 Source 谱系加速度公式的等效时间常数（不是严格的到达时间，
    /// 因为该公式的加速度随 |wishSpeed − v| 衰减，只有渐近到达）。
    /// </summary>
    public float AccelTimeToTopSpeed => GroundAccel > 1e-4f ? 1f / GroundAccel : float.PositiveInfinity;

    /// <summary>
    /// 把 <see cref="MaxFallSpeed"/> 硬夹换算成**二次阻力系数** k (1/m)：
    /// <c>a = g − k·v²</c>，终端速度处 g = k·v_t² ⇒ k = g / v_t²。
    /// 默认 20.7802 / 88.9² = 2.6293e-3。当前实现仍是硬夹（见 README 物理审计），
    /// 这个量用于"如果要改成真阻力"时的换算。
    /// </summary>
    public float TerminalDragK => Gravity / MathF.Max(1e-4f, MaxFallSpeed * MaxFallSpeed);

    /// <summary>根据台面相对脚底的高度分类翻越档位。</summary>
    public MantleClass ClassifyMantle(float heightAboveFeet)
    {
        if (heightAboveFeet <= MantleLevelHeight) return MantleClass.Below;
        if (heightAboveFeet <= MantleAboveHeight) return MantleClass.Level;
        if (heightAboveFeet <= MantleHighHeight) return MantleClass.Above;
        return MantleClass.High;
    }

    /// <summary>取某档翻越的总时长 (s)。</summary>
    public float MantleDurationFor(MantleClass cls) => cls switch
    {
        MantleClass.Below => MantleDurationBelow,
        MantleClass.Level => MantleDurationLevel,
        MantleClass.Above => MantleDurationAbove,
        _ => MantleDurationHigh,
    };

    /// <summary>按下落高度给落地分档。</summary>
    public LandingTier ClassifyLanding(float fallDistance)
    {
        if (fallDistance < LandDistLow) return LandingTier.Low;
        if (fallDistance < LandDistMedium) return LandingTier.Medium;
        if (fallDistance < LandDistHigh) return LandingTier.High;
        return LandingTier.Hard;
    }

    /// <summary>
    /// Fallstun 时长 (s)。wiki：0 s @ 300 hu → 1 s @ 800 hu，且"非线性（可能是二次）"。
    /// 这里用 <c>t = frac² × max</c>。
    /// </summary>
    public float FallStunDuration(float fallDistance)
    {
        if (fallDistance <= FallStunMinHeight) return 0f;
        float span = MathF.Max(1e-4f, FallStunMaxHeight - FallStunMinHeight);
        float frac = Math.Min(1f, (fallDistance - FallStunMinHeight) / span);
        return frac * frac * FallStunMaxDuration;
    }

    // ═════════════════════════════ 预设 ═════════════════════════════

    /// <summary>
    /// **默认预设**：角色尺寸用需求方口径（1.95 / 0.975 / 0.10），其余用 Apex 实机实测值。
    /// </summary>
    public static MovementConfig Apex() => new();

    /// <summary>
    /// **Legacy 预设**：v0.1 的速度口径（行走 5.4、冲刺 ×1.37 = 7.4、攀爬 6.5、跳跃 1.15）。
    /// 保留它是因为项目里已有的关卡/手感基准是按这套数值调的，一行即可切回。
    /// </summary>
    public static MovementConfig Legacy()
    {
        var c = new MovementConfig
        {
            WalkSpeed = 5.4f,
            SprintSpeed = 5.4f * 1.37f,
            CrouchWalkSpeed = 5.4f * 0.5f,
            HolsterMultiplier = 1.0f,
            MantleMaxHeight = 6.5f,
            JumpHeight = 1.15f,
            CoyoteTime = 0.10f,
            SlideMinStartSpeed = 5.0f,
            SlideBoostSpeed = 3.0f,
            SlideBoostCooldown = 0.35f,
            SlideJumpSpeedThreshold = 14.0f,
            SlideJumpTimeWindow = 0.55f,
            MantleLevelHeight = 1.1f,
            MantleAboveHeight = 2.2f,
            MantleHighHeight = 4.0f,
        };
        return c;
    }

    // ═════════════════════════════ 校验 ═════════════════════════════

    public void Validate()
    {
        if (TickRate <= 0) throw new ArgumentOutOfRangeException(nameof(TickRate));
        if (FixedDelta <= 0f) throw new ArgumentOutOfRangeException(nameof(TickRate));
        if (TraversalProbeInterval < 1) throw new ArgumentOutOfRangeException(nameof(TraversalProbeInterval));
        if (ClimbProbeInterval < 1) throw new ArgumentOutOfRangeException(nameof(ClimbProbeInterval));
        if (ClimbWallProbeInterval < 1) throw new ArgumentOutOfRangeException(nameof(ClimbWallProbeInterval));
        if (SlipProbeInterval < 1) throw new ArgumentOutOfRangeException(nameof(SlipProbeInterval));
        if (SlipAirRestrictDuration < 0f) throw new ArgumentOutOfRangeException(nameof(SlipAirRestrictDuration));
        if (SlipAirRestrictConeDeg <= 0f || SlipAirRestrictConeDeg > 180f)
            throw new ArgumentOutOfRangeException(nameof(SlipAirRestrictConeDeg));
        if (SlipSteerFactor < 0f || SlipSteerFactor > 1f)
            throw new ArgumentOutOfRangeException(nameof(SlipSteerFactor));
        if (SlideFrictionCoefficient < 0f)
            throw new ArgumentOutOfRangeException(nameof(SlideFrictionCoefficient));
        if (!float.IsFinite(JumpAccelTime) || JumpAccelTime < 0f)
            throw new ArgumentOutOfRangeException(nameof(JumpAccelTime), "JumpAccelTime 必须是有限的非负数");
        if (JumpAccelTime > FixedDelta * 60f)
            throw new ArgumentOutOfRangeException(nameof(JumpAccelTime), "JumpAccelTime 上限 1 秒（60 tick）");
        if (WallHangMaxViewAngle <= 0f || WallHangMaxViewAngle >= 90f)
            throw new ArgumentOutOfRangeException(nameof(WallHangMaxViewAngle));
        if (WallHangReach <= 0f) throw new ArgumentOutOfRangeException(nameof(WallHangReach));

        if (WallGrabMaxRecedeSpeed < 0f) throw new ArgumentOutOfRangeException(nameof(WallGrabMaxRecedeSpeed));
        if (WallGrabMinGroundClearance < 0f)
            throw new ArgumentOutOfRangeException(nameof(WallGrabMinGroundClearance));
        if (WallHangShimmySpeed < 0f) throw new ArgumentOutOfRangeException(nameof(WallHangShimmySpeed));
        if (WallHangMaxTime <= 0f) throw new ArgumentOutOfRangeException(nameof(WallHangMaxTime));
        if (WallHangSlipSpeed < 0f) throw new ArgumentOutOfRangeException(nameof(WallHangSlipSpeed));
        if (WallHangMaxSlipDistance <= 0f) throw new ArgumentOutOfRangeException(nameof(WallHangMaxSlipDistance));
        if (WallHangReattachCooldown < 0f) throw new ArgumentOutOfRangeException(nameof(WallHangReattachCooldown));
        if (WallHangIdleFallTime <= 0f) throw new ArgumentOutOfRangeException(nameof(WallHangIdleFallTime));
        if (WallHangLateralFallTime <= 0f) throw new ArgumentOutOfRangeException(nameof(WallHangLateralFallTime));
        if (WallHangGripRestoreTime <= 0f) throw new ArgumentOutOfRangeException(nameof(WallHangGripRestoreTime));
        if (CapsuleRadius <= 0f) throw new ArgumentOutOfRangeException(nameof(CapsuleRadius));
        if (ClimbAttachMaxViewAngle <= 0f || ClimbAttachMaxViewAngle >= 90f)
            throw new ArgumentOutOfRangeException(nameof(ClimbAttachMaxViewAngle));
        if (ClimbMinApproachSpeed < 0f)
            throw new ArgumentOutOfRangeException(nameof(ClimbMinApproachSpeed));
        if (ClimbNoInputGrace < 0f)
            throw new ArgumentOutOfRangeException(nameof(ClimbNoInputGrace));
        if (StandHeight < CrouchHeight)
            throw new ArgumentException("StandHeight 必须 >= CrouchHeight");
        if (CrouchHeight < 0f)
            throw new ArgumentOutOfRangeException(nameof(CrouchHeight));
        if (StandHeight <= CapsuleRadius * 2f)
            throw new ArgumentException("StandHeight 必须大于胶囊直径 2×CapsuleRadius（否则半高为负）");
        if (CrouchHeight <= CapsuleRadius * 2f)
            throw new ArgumentException("CrouchHeight 必须大于胶囊直径 2×CapsuleRadius（否则蹲下时半高为负）");
        if (GroundSnapLength <= 0f)
            throw new ArgumentOutOfRangeException(nameof(GroundSnapLength));
        if (EyeHeightOffset < 0f || EyeHeightOffset >= CrouchHeight)
            throw new ArgumentOutOfRangeException(
                nameof(EyeHeightOffset), "EyeHeightOffset 必须在 [0, CrouchHeight) 内");
        if (MantleMaxHeight < MantleAboveHeight)
            throw new ArgumentException("MantleMaxHeight 必须 >= MantleAboveHeight");
        if (MantleLevelHeight > MantleAboveHeight || MantleAboveHeight > MantleHighHeight)
            throw new ArgumentException("翻越分档阈值必须单调递增：MantleLevelHeight <= MantleAboveHeight <= MantleHighHeight");
        if (JumpFatigueMinTime > JumpFatigueMaxTime)
            throw new ArgumentException("JumpFatigueMinTime 必须 <= JumpFatigueMaxTime");
        if (JumpFatigueHeightFrac <= 0f || JumpFatigueHeightFrac > 1f)
            throw new ArgumentOutOfRangeException(nameof(JumpFatigueHeightFrac));
        if (FallStunMaxHeight <= FallStunMinHeight)
            throw new ArgumentException("FallStunMaxHeight 必须 > FallStunMinHeight");
        if (WalkableNormalDot <= 0f || WalkableNormalDot >= 1f)
            throw new ArgumentOutOfRangeException(nameof(WalkableNormalDot));
        if (MantleGrabFraction <= 0f || MantleGrabFraction >= 1f)
            throw new ArgumentOutOfRangeException(nameof(MantleGrabFraction));
    }
}
