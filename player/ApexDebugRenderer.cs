using System;
using System.Collections.Generic;
using Godot;
using ApexMovement.Core;

namespace ApexMovement.Player;

/// <summary>
/// **三维速度 / 加速度调试渲染器**（v0.5，需求 m01621）。
///
/// 需求原话：
/// 「目前我们的移动系统已经非常复杂了，我没办法只通过 HUD 的速度描述来确认速度，
///  我需要一个调试渲染器。这个速度调试渲染器需要一个第三人称视角，然后会将玩家透明化
///  （不是完全透明，能看得出玩家轮廓），然后以玩家重心，向玩家的速度方向射出三维空间的
///  箭头，箭头长度随着速度大小变化，并在箭头上标注速度大小。除了速度还有作用力产生的
///  加速度，也实时渲染三维箭头，并且标注大小。不同的速度和加速度要用不同颜色区分，
///  并且也要附带左上角 HUD 多颜色对应的文字显示各项数值的大小。」
///
/// ## 为什么是"箭头长度 = 大小 × 比例"
///
/// 速度与加速度量纲不同（m/s vs m/s²），所以用**两套独立比例尺**：
///   · <see cref="VelocityScale"/>（默认 0.12 m 每 m/s）⇒ 6.6 m/s 画成 0.79 m 长的箭头，
///     与角色身高（1.9 m）同一数量级，一眼能看出"跑得比走得快多少"；
///   · <see cref="AccelScale"/>（默认 0.03 m 每 m/s²）⇒ 重力 9.8 画成 0.29 m。
/// 比例尺写在箭头标签里（"×0.12"），避免"看着长以为快"的误读。
///
/// ## 加速度为什么能分项显示
///
/// <see cref="ApexMovementCore.LastAccel"/> 在求解器内部按来源累计每个 tick 的 Δv/dt：
/// 重力 / 摩擦 / 输入 / 冲量 各自记账，**碰撞**用"总变化 − 各来源之和"反推
/// （投影是非线性的，不能逐项相加去凑）。所以这里画出来的箭头之和恒等于速度变化量。
///
/// ## 节点结构
///
/// 每个箭头 = 圆柱（杆）+ 圆锥（头）+ Label3D（数值），全部是**预先建好、每帧只改
/// transform 与 text**，不在运行时 new 节点（避免 GC 抖动影响手感调试）。
/// </summary>
public partial class ApexDebugRenderer : Node3D
{
    /// <summary>一根箭头（杆 + 头 + 标签）的可复用句柄。</summary>
    private sealed class Arrow
    {
        public MeshInstance3D Shaft = null!;
        public MeshInstance3D Head = null!;
        public Label3D Label = null!;
        public string Caption = "";
        public Color Color;

        // ★ 缓存（v0.5.2 关键修复）：这些资源**必须只建一次**。
        //
        // 原来每帧 new CylinderMesh / StandardMaterial3D，会持续向渲染服务器注册
        // 新的 uniform set；D3D12 后端的 RESOURCES 描述符堆是有限的，几万帧之后
        // 直接耗尽 —— 报错 "Cannot create uniform set because there's not enough
        // room in the RESOURCES descriptor heap."（实测一次会话 13950 条）。
        // 现在材质与网格都缓存，每帧只改 Transform（缩放即长度）。
        public StandardMaterial3D Material = null!;
        public CylinderMesh ShaftMesh = null!;
        public CylinderMesh HeadMesh = null!;
        public string LastText = "";

        // ★ 关于"箭头在黑/亮背景上看不清"的处理：
        //
        //   试过"黑色几何描边"（外层放大 1.9 倍的黑色圆锥），**不可行** ——
        //   箭头是 Unshaded + NoDepthTest 的不透明材质，外层黑色壳会把内层彩色
        //   整个盖住（实测截图里箭头变成一个黑团）。
        //   改用**高饱和色相 + 加粗 + 黑底图例**三件套，效果更稳且零额外开销。
    }

    // ── 颜色（与 HUD 图例严格一致）────────────────────────────
    // ★ v0.5.3：七种颜色重新挑过，保证两两之间在**色相**上分得开。
    //
    //   原来重力(1,0.36,0.36 浅红) 与 碰撞/支撑(1,0.42,1 洋红) 太接近，
    //   而合计(0.55,1,1 青) 与 摩擦(0.36,0.68,1 蓝) 也容易混。
    //   现在按"红→蓝→绿→黄→紫→青→白"拉满色相间隔，并配合黑色描边，
    //   在任何背景（亮天空 / 亮地面 / 暗地形）上都能一眼分辨。
    public static readonly Color ColorVelocity = new(1.00f, 1.00f, 1.00f);   // 白：速度
    public static readonly Color ColorGravity = new(1.00f, 0.20f, 0.20f);    // 红：重力
    public static readonly Color ColorFriction = new(0.25f, 0.55f, 1.00f);   // 蓝：摩擦
    public static readonly Color ColorInput = new(0.25f, 1.00f, 0.35f);      // 绿：输入推进
    public static readonly Color ColorImpulse = new(1.00f, 0.85f, 0.15f);    // 黄：瞬时冲量
    public static readonly Color ColorCollision = new(0.72f, 0.35f, 1.00f);  // 紫：碰撞/贴地
    public static readonly Color ColorNet = new(0.15f, 1.00f, 0.95f);        // 青：合计

    // ── 可调参数 ──────────────────────────────────────────────
    /// <summary>速度 → 箭头长度（米 / (m/s)）。</summary>
    public float VelocityScale = 0.20f;
    /// <summary>加速度 → 箭头长度（米 / (m/s²)）。</summary>
    public float AccelScale = 0.05f;
    /// <summary>短于此长度的箭头不画（避免 0.001 m/s² 的噪声箭头糊在角色身上）。</summary>
    public float MinVelocityToDraw = 0.05f;
    public float MinAccelToDraw = 0.05f;

    /// <summary>总开关（关掉 = 整个节点隐藏 + 玩家恢复不透明）。</summary>
    public bool Enabled { get; private set; }

    private readonly List<Arrow> _arrows = new();
    private int _labelStagger;
    private int _warmupFrames;
    private Arrow _velocity = null!;
    private Arrow _gravity = null!;
    private Arrow _friction = null!;
    private Arrow _input = null!;
    private Arrow _impulse = null!;
    private Arrow _collision = null!;
    private Arrow _net = null!;

    private Node3D? _player;
    private readonly List<(MeshInstance3D Mesh, Material? Original)> _playerMeshes = new();
    private StandardMaterial3D? _ghostMaterial;

    /// <summary>
    /// **碰撞胶囊的半透明替身**。
    ///
    /// 为什么需要它：测试场里的玩家节点是纯逻辑 Node3D（没有美术网格），
    /// 第三人称下什么都看不到；而且调试时真正要看的本来就是**碰撞体**的形状
    /// （蹲下时胶囊真的变矮了没有、收腿时脚底在哪里）。所以这里直接按
    /// <c>CurrentHeight / CapsuleRadius</c> 画一个半透明胶囊 —— 有美术网格时
    /// 两者会同时被"透明化"，轮廓照样看得见。
    /// </summary>
    private MeshInstance3D _ghostCapsule = null!;
    private StandardMaterial3D _capsuleMaterial = null!;
    private CapsuleMesh? _capsuleMesh;
    private float _capsuleRadius = -1f;
    private float _capsuleHeight = -1f;

    public override void _Ready()
    {
        _capsuleMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.55f, 0.78f, 1.0f, 0.13f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _ghostCapsule = new MeshInstance3D
        {
            Name = "GhostCapsule",
            MaterialOverride = _capsuleMaterial,
        };
        AddChild(_ghostCapsule);

        BuildArrows();
        Warmup();
    }

    /// <summary>
    /// **预热**：让箭头与胶囊各绘制一帧再隐藏。
    ///
    /// 需求方实测：「我打开第三人称和速度监视器后，会强烈卡顿一下」。
    /// 原因不是逻辑慢，而是**首次绘制**要付的固定成本：
    ///   · 7 组网格 + 材质第一次上传到 GPU（各自要建 uniform set）；
    ///   · 7 个 Label3D 第一次生成字形图集（中文标签 + 数字）；
    ///   · 半透明胶囊第一次编译它的材质变体。
    /// 这些成本在"按开关的那一刻"集中爆发，所以看起来像卡了一下。
    /// 预热把这份成本挪到加载时（那时玩家本来就在等场景），开关就变即时了。
    /// </summary>
    private void Warmup()
    {
        Visible = true;
        var far = new Vector3(0f, -5000f, 0f);
        foreach (Arrow a in _arrows)
        {
            a.Shaft.Visible = true;
            a.Head.Visible = true;
            a.Label.Visible = true;
            a.Shaft.GlobalPosition = far;
            a.Head.GlobalPosition = far;
            a.Label.GlobalPosition = far;
            a.Label.Text = a.Caption + " 0.00";
            a.LastText = a.Label.Text;
        }
        _ghostCapsule.Mesh = new CapsuleMesh { Radius = 0.4f, Height = 1.9f };
        _capsuleMesh = (CapsuleMesh)_ghostCapsule.Mesh;
        _capsuleRadius = 0.4f;
        _capsuleHeight = 1.9f;
        _ghostCapsule.GlobalPosition = far;
        _warmupFrames = 2;
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        if (_warmupFrames > 0 && !Enabled)
        {
            _warmupFrames--;
            if (_warmupFrames == 0)
            {
                Visible = false;
                SetProcess(false);
            }
        }
    }

    /// <summary>每帧更新半透明胶囊（跟随当前身高，蹲下时会真的变矮）。</summary>
    public void RefreshCapsule(Vector3 center, Vector3 up, float height, float radius)
    {
        if (!Enabled) return;
        // 网格只在**身高变化超过 1 mm** 时重建（蹲下/起身时会变，跑步时不变）。
        float wantH = Mathf.Max(height, radius * 2f + 0.01f);
        if (_capsuleMesh == null ||
            Mathf.Abs(_capsuleRadius - radius) > 1e-4f ||
            Mathf.Abs(_capsuleHeight - wantH) > 1e-3f)
        {
            _capsuleMesh = new CapsuleMesh
            {
                Radius = radius,
                Height = wantH,
                RadialSegments = 16,
                Rings = 8,
            };
            _ghostCapsule.Mesh = _capsuleMesh;
            _capsuleRadius = radius;
            _capsuleHeight = wantH;
        }
        // CapsuleMesh 沿 +Y；参考系任意时用 Quaternion(Up → up) 转过去。
        var basis = up.IsEqualApprox(Vector3.Up)
            ? Basis.Identity
            : new Basis(new Quaternion(Vector3.Up, up.Normalized()));
        _ghostCapsule.GlobalTransform = new Transform3D(basis, center);
    }

    /// <summary>绑定要透明化的玩家节点（递归收集它的 MeshInstance3D）。</summary>
    public void BindPlayer(Node3D player)
    {
        _player = player;
        _playerMeshes.Clear();
        CollectMeshes(player);
    }

    /// <summary>
    /// 递归收集玩家身上的网格（用于"玩家透明化"）。
    ///
    /// ★ 必须**跳过本节点自己的子树**：渲染器是挂在玩家下面的子节点，
    ///   而箭头/胶囊本身就是 MeshInstance3D —— 如果一并收进来，
    ///   开 ghost 时会把所有箭头的 MaterialOverride 换成同一份半透明蓝色材质，
    ///   于是七种颜色的箭头全变成一种蓝，"颜色区分"直接失效
    ///   （v0.5.2 第一版就是这个 bug，需求方实测报"现在没有颜色了"）。
    /// </summary>
    private void CollectMeshes(Node node)
    {
        if (node == this) return;                       // 跳过调试渲染器自己
        if (node is MeshInstance3D mi) _playerMeshes.Add((mi, mi.MaterialOverride));
        foreach (Node child in node.GetChildren()) CollectMeshes(child);
    }

    private void BuildArrows()
    {
        _velocity = AddArrow("速度", ColorVelocity);
        _gravity = AddArrow("重力", ColorGravity);
        _friction = AddArrow("摩擦", ColorFriction);
        _input = AddArrow("输入", ColorInput);
        _impulse = AddArrow("冲量", ColorImpulse);
        _collision = AddArrow("碰撞/支撑", ColorCollision);
        _net = AddArrow("合计", ColorNet);
    }

    private Arrow AddArrow(string caption, Color color)
    {
        var shaft = new MeshInstance3D { Name = caption + "Shaft" };
        var head = new MeshInstance3D { Name = caption + "Head" };
        var label = new Label3D
        {
            Name = caption + "Label",
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            FontSize = 40,
            PixelSize = 0.0018f,
            // 黑色描边：标签是纯色文字直接画在场景上，遇到亮天空/亮地面时
            // 白字（速度）与青字（合计）几乎读不出来 —— 描边让任何背景下都清晰。
            OutlineSize = 18,
            OutlineModulate = new Color(0f, 0f, 0f, 1f),
            Modulate = color,
            Text = caption,
        };
        AddChild(shaft);
        AddChild(head);
        AddChild(label);

        // 单位尺寸的网格 + 一份共享材质：长度/粗细全靠 Transform 缩放。
        var material = Unshaded(color);
        var shaftMesh = new CylinderMesh
        {
            TopRadius = 1f,
            BottomRadius = 1f,
            Height = 1f,
            RadialSegments = 8,
            Rings = 1,
        };
        var headMesh = new CylinderMesh
        {
            TopRadius = 0f,
            BottomRadius = 1f,
            Height = 1f,
            RadialSegments = 10,
            Rings = 1,
        };
        shaft.Mesh = shaftMesh;
        shaft.MaterialOverride = material;
        head.Mesh = headMesh;
        head.MaterialOverride = material;

        var arrow = new Arrow
        {
            Shaft = shaft, Head = head, Label = label,
            Caption = caption, Color = color,
            Material = material, ShaftMesh = shaftMesh, HeadMesh = headMesh,
        };
        _arrows.Add(arrow);
        return arrow;
    }

    /// <summary>开关渲染器（同时切换玩家半透明）。</summary>
    public void SetEnabled(bool on)
    {
        Enabled = on;
        Visible = on;
        SetProcess(on);
        ApplyGhost(on);
        if (!on)
        {
            foreach (Arrow a in _arrows) a.Label.Text = "";
        }
    }

    /// <summary>玩家半透明化（能看出轮廓，但不会挡住箭头）。</summary>
    private void ApplyGhost(bool on)
    {
        if (_playerMeshes.Count == 0 && _player != null) CollectMeshes(_player);

        if (on)
        {
            _ghostMaterial ??= new StandardMaterial3D
            {
                AlbedoColor = new Color(0.55f, 0.75f, 1.0f, 0.16f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            foreach ((MeshInstance3D mesh, Material? _) in _playerMeshes)
                mesh.MaterialOverride = _ghostMaterial;
        }
        else
        {
            foreach ((MeshInstance3D mesh, Material? original) in _playerMeshes)
                mesh.MaterialOverride = original;
        }
    }

    /// <summary>
    /// 每帧刷新箭头。参数来自求解器：重心、速度、上一 tick 的加速度分解。
    /// </summary>
    public void Refresh(Vector3 center, Vector3 velocity, ApexMovementCore.AccelBreakdown accel,
                        float accelDt)
    {
        if (!Enabled) return;

        _labelStagger = 0;
        UpdateArrow(_velocity, center, velocity, VelocityScale,
                    MinVelocityToDraw, velocity.Length(), "m/s");

        // 加速度来源（矢量方向 + 大小）
        UpdateAccel(_gravity, center, accel.Gravity, accelDt, "重力");
        UpdateAccel(_friction, center, accel.Friction, accelDt, "摩擦");
        UpdateAccel(_input, center, accel.Input, accelDt, "输入");
        UpdateAccel(_impulse, center, accel.Impulse, accelDt, "冲量");
        UpdateAccel(_collision, center, accel.Collision, accelDt, "碰撞/支撑");
        UpdateAccel(_net, center, accel.Net, accelDt, "合计");
    }

    private void UpdateAccel(Arrow arrow, Vector3 origin, F3 v, float dt, string caption)
    {
        Vector3 vec = new(v.X, v.Y, v.Z);
        UpdateArrow(arrow, origin, vec, AccelScale, MinAccelToDraw, vec.Length(), "m/s²", caption);
    }

    /// <summary>把一根箭头摆到 origin + dir·len 的位置，并把数值写进标签。</summary>
    private void UpdateArrow(Arrow arrow, Vector3 origin, Vector3 vec, float scale,
                             float minLen, float magnitude, string unit, string? caption = null)
    {
        bool show = magnitude >= minLen && vec.LengthSquared() > 1e-12f;
        arrow.Shaft.Visible = show;
        arrow.Head.Visible = show;
        arrow.Label.Visible = show;
        if (!show)
        {
            if (arrow.LastText.Length != 0)
            {
                arrow.Label.Text = "";
                arrow.LastText = "";
            }
            return;
        }

        Vector3 dir = vec.Normalized();
        float len = Mathf.Max(magnitude * scale, 0.02f);
        float headLen = Mathf.Min(len * (ReferenceEquals(arrow, _velocity) ? 0.34f : 0.28f), 0.16f);
        float shaftLen = Mathf.Max(len - headLen, 0.01f);
        // 粗细：速度箭头单独加粗一点（第三人称机位在背后时，前向的速度箭头正对镜头，
        // 会被透视压成一个圆点；加粗 + 放大箭头头部能让"它指向正前方"这件事看得出来）。
        bool isVelocity = ReferenceEquals(arrow, _velocity);
        float radius = Mathf.Clamp(len * (isVelocity ? 0.075f : 0.055f), 0.014f, 0.055f);

        // 圆柱默认沿 +Y：先转到目标方向，再用**非均匀缩放**表达长度与粗细。
        // 网格与材质都是预先建好的单位尺寸资源（见 Arrow 里的注释），
        // 每帧只写 Transform —— 不产生任何新的渲染资源。
        //
        // ★ 必须用 `rot * Basis.FromScale(s)`（后乘 = **局部轴**缩放）。
        //
        //   v0.5.2 第一版写的是 `rot.Scaled(s)`，那是错的：Godot 的
        //   `Basis::scale()` 是**按行**缩放（`rows[i] *= s[i]`），等价于
        //   `diag(s) * rot` —— **全局轴**缩放。对一根指向 +Z 的箭头来说，
        //   局部 Y 轴（长度方向）映射到世界 +Z，却被乘上了全局 Z 的缩放系数
        //   （= radius，只有 1~5 cm），于是箭杆被压成一根几乎为零长的薄片，
        //   看上去只剩一条竖线 —— 这正是"所有箭头都变成竖直的"的原因。
        //   后乘才是"先旋转、再沿自身轴缩放"，长度/粗细才落在正确的轴上。
        var rot = new Basis(new Quaternion(Vector3.Up, dir));

        arrow.Shaft.GlobalTransform = new Transform3D(
            rot * Basis.FromScale(new Vector3(radius, shaftLen, radius)),
            origin + dir * (shaftLen * 0.5f));

        arrow.Head.GlobalTransform = new Transform3D(
            rot * Basis.FromScale(new Vector3(radius * 2.6f, headLen, radius * 2.6f)),
            origin + dir * (shaftLen + headLen * 0.5f));

        // 文本只在数值真的变化时写（Label3D 改 text 会重建字形布局，
        // 每帧无条件写会造成无谓的 CPU 峰值）。
        string name = caption ?? arrow.Caption;
        string text = $"{name} {magnitude:F2} {unit}";
        if (text != arrow.LastText)
        {
            arrow.Label.Text = text;
            arrow.LastText = text;
        }

        // 标签沿"垂直于箭头、且尽量朝上"的方向错开一点，
        // 否则重力/摩擦/输入/合计 五六根箭头的标签会全叠在重心那一点上。
        Vector3 side = Vector3.Up.Cross(dir);
        if (side.LengthSquared() < 1e-4f) side = Vector3.Right;
        side = side.Normalized();
        Vector3 stagger = side * (_labelStagger++ % 2 == 0 ? 0.10f : -0.10f)
                        + Vector3.Up * (0.06f * (_labelStagger % 3));
        arrow.Label.GlobalPosition = origin + dir * (len + 0.10f) + stagger;
    }

    /// <summary>
    /// 箭头材质：**不参与深度测试**。
    ///
    /// 必须这样：第三人称下箭头常常从角色身体里穿出去，若做深度测试，
    /// 箭杆前半段会被半透明胶囊/地形挡住，看起来"只有一小截"。
    /// 调试可视化的第一诉求是"看得见"，不是"插进场景里"。
    /// </summary>
    private static StandardMaterial3D Unshaded(Color c) => new()
    {
        AlbedoColor = c,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        NoDepthTest = true,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
    };

    /// <summary>HUD 图例文本（BBCode，颜色与箭头一一对应）。</summary>
    /// <summary>
    /// 左上角图例（颜色与三维箭头一一对应）。
    ///
    /// v0.5.2 增补：把**体重与摩擦力**也写出来（需求 m01928 第 4 点要求按 BMI 算体重、
    /// 并把最大静摩擦/滑动摩擦显式化）。力的单位是牛顿，箭头单位是加速度 ——
    /// 两者差一个质量，所以这里同时给出 m、N、Fs、Fk，方便对照。
    /// </summary>
    /// <param name="mass">体重 (kg)</param>
    /// <param name="normalForce">当前法向力 (N)</param>
    /// <param name="staticFriction">最大静摩擦力 (N)</param>
    /// <param name="kineticFriction">滑动摩擦力 (N)</param>
    /// <param name="grip">抓地比值 0..1（1 = 完全抓地，冰面接近 0）</param>
    /// <param name="sliding">是否在打滑</param>
    public string LegendBbcode(Vector3 velocity, ApexMovementCore.AccelBreakdown accel,
                               float mass = 0f, float normalForce = 0f,
                               float staticFriction = 0f, float kineticFriction = 0f,
                               float grip = 1f, bool sliding = false)
    {
        string Hex(Color c) => $"#{c.R8:X2}{c.G8:X2}{c.B8:X2}";
        float v = velocity.Length();
        string body = mass > 0f
            ? $"\n[color=#8899AA]体重[/color] {mass:F2} kg   " +
              $"N {normalForce:F1} N   " +
              $"Fs [color={Hex(ColorFriction)}]{staticFriction:F1}[/color] N   " +
              $"Fk {kineticFriction:F1} N   " +
              $"抓地 [color={(sliding ? "#FF6B6B" : "#7CFF9B")}]{grip * 100f:F0}%[/color]" +
              (sliding ? " [color=#FF6B6B]打滑中[/color]" : "")
            : "";
        return
            $"[color={Hex(ColorVelocity)}]速度[/color] {v:F2} m/s" +
            $"  [color=#8899AA](长度 ×{VelocityScale:F2} m per m/s)[/color]\n" +
            $"[color={Hex(ColorGravity)}]重力[/color] {Mag(accel.Gravity):F2}  " +
            $"[color={Hex(ColorFriction)}]摩擦[/color] {Mag(accel.Friction):F2}  " +
            $"[color={Hex(ColorInput)}]输入[/color] {Mag(accel.Input):F2}  m/s²\n" +
            $"[color={Hex(ColorImpulse)}]冲量[/color] {Mag(accel.Impulse):F2}  " +
            $"[color={Hex(ColorCollision)}]碰撞/支撑[/color] {Mag(accel.Collision):F2}  " +
            $"[color={Hex(ColorNet)}]合计[/color] {Mag(accel.Net):F2}  m/s²" +
            $"  [color=#8899AA](×{AccelScale:F3})[/color]" + body;
    }

    private static float Mag(F3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
}
