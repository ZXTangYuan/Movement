using Godot;
using ApexMovement.Camera;
using ApexMovement.Core;
using ApexMovement.GodotBridge;

namespace ApexMovement.Player;

/// <summary>
/// Apex 风格第一人称玩家节点（v0.2）。
///
/// ## 帧率无关是怎么做到的
/// 1. 状态推进只在 <see cref="_PhysicsProcess"/> 里发生 —— Godot 的物理帧是**固定步长**，
///    与渲染帧率无关（30/60/144/240fps 下物理步长恒为 1/TickRate）。
/// 2. 鼠标增量在渲染帧里**累积**，在每个物理 tick 边界**整份消费**。
///    总增量守恒 ⇒ 任意帧率下累计转角一致。
/// 3. 求解器内部只用固定 dt 与确定性浮点（见 F3）。
/// 4. 走路摇晃的步频相位也在 tick 内推进（不在渲染帧），因此摇晃也是帧率无关的。
///
/// ## 相机
/// 相机效果由纯 C# 的 <see cref="ApexCameraRig"/> 计算（FOV / 侧倾 / 视角踢 / 眼位偏移），
/// 本节点只负责把结果写进 Godot 的 <see cref="Camera3D"/>。
/// 这样相机逻辑可以脱离引擎做逐帧断言（见 tests/CameraTest.cs）。
///
/// ## 与 ProjectA 的边界
/// 本节点不依赖 ProjectA 的任何类（不引用 SolarSystem / Planet / NetworkCore）。
/// 重力方向通过 <see cref="SetUp"/> 注入，默认世界 Y 轴向上；
/// 将来接入球形行星时，每 tick 传入该行星的局部 up 即可复用。
/// </summary>
[GlobalClass]
public partial class ApexPlayer : Node3D
{
    /// <summary>数值预设。</summary>
    public enum Preset
    {
        /// <summary>角色尺寸用需求方口径，其余用 Apex 实机实测值（默认）。</summary>
        Apex = 0,
        /// <summary>v0.1 的速度口径（行走 5.4 / 冲刺 7.4 / 跳跃 1.15）。</summary>
        Legacy = 1,
    }

    [ExportGroup("Preset")]
    [Export] public Preset ConfigPreset = Preset.Apex;

    [ExportGroup("Movement（留空则用预设值）")]
    /// <summary>行走速度 (m/s)。&lt;= 0 表示用预设值。</summary>
    [Export] public float WalkSpeedOverride = -1f;
    /// <summary>冲刺速度 (m/s)。&lt;= 0 表示用预设值。</summary>
    [Export] public float SprintSpeedOverride = -1f;
    /// <summary>跳跃高度 (m)。&lt;= 0 表示用预设值。</summary>
    [Export] public float JumpHeightOverride = -1f;
    /// <summary>是否默认收枪（收枪 = ×1.15 速度）。</summary>
    [Export] public bool StartHolstered = true;

    [ExportGroup("Body")]
    [Export] public float StandHeight = 1.95f;
    [Export] public float CrouchHeight = 0.975f;
    [Export] public float EyeHeightOffset = 0.10f;

    [ExportGroup("Camera")]
    [Export] public float FovBase = 90f;
    [Export] public bool CameraEffectsEnabled = true;
    [Export] public bool ShowDebugHud = true;
    /// <summary>
    /// 是否启用运行时调试菜单（v0.4 新增）。按 <c>\</c> 打开/关闭。
    /// 关掉它 = 这个节点完全不创建菜单节点（打包发布时可关）。
    /// </summary>
    [Export] public bool ShowDebugMenu = true;

    [ExportGroup("Input")]
    [Export] public float MouseSensitivity = 0.0025f;
    [Export] public bool InvertY = false;

    [ExportGroup("Tick")]
    /// <summary>
    /// 移动 tick 频率。**v0.3 起固定为 60**（<see cref="MovementConfig.PinnedTickRate"/>）：
    /// 这个值只作为 Inspector 可见的"确认项"，运行时会被强制钳到 60 并给一次提示。
    /// 需要降低昂贵子系统的开销请改 <see cref="MovementConfig.TraversalProbeInterval"/>。
    /// </summary>
    [Export] public int TickRate = MovementConfig.PinnedTickRate;

    [ExportGroup("Safety")]
    /// <summary>掉到这个世界 Y 值以下就原地复活（防"掉入虚空"后无法继续测试）。</summary>
    [Export] public float KillPlaneY = -50f;

    private ApexMovementCore _core = null!;
    private ApexCameraRig _rig = null!;
    private GodotMovementWorld _world = null!;
    private Camera3D _camera = null!;
    private Label _hud = null!;
    /// <summary>运行时调试菜单（`\` 键开关，v0.4 新增）。未启用时为 null。</summary>
    private ApexDebugMenu? _debugMenu;

    /// <summary>调试面板（截图 / 自检脚本用；未构建时为 null）。</summary>
    public ApexDebugMenu? DebugMenu => _debugMenu;
    /// <summary>调试菜单是否正在显示（显示时冻结移动输入、释放鼠标）。</summary>
    private bool _debugMenuOpen;

    /// <summary>
    /// 固定 tick 累加器：把物理帧时间折算成 60Hz 的求解器 tick。
    /// 这是"移动 tick 固定 60/s 不随工程物理频率漂移"的实现点。
    /// </summary>
    private ApexTickClock _clock = null!;

    /// <summary>上一物理帧实际跑了几个 tick（HUD/自检观察用）。</summary>
    private int _lastFrameTicks;

    private Vector3 _spawnPoint;
    private bool _spawnCaptured;

    // 输入累积（渲染帧累加，tick 消费）
    private float _pendingMouseX;
    private float _pendingMouseY;
    private bool _jumpQueued;
    private bool _crouchHeld;
    private bool _sprintHeld;
    private bool _holstered;
    private float _moveX;
    private float _moveZ;
    private bool _mouseCaptured = true;

    // 相机：本帧要应用的输出
    private CameraOutput _camOut;

    // 落地事件：物理 tick 里产生，渲染帧里消费（相机按渲染帧更新才平滑）
    private LandingTier _pendingLandingTier = LandingTier.None;
    private float _pendingFallDistance;
    private float _pendingImpact;
    private int _pendingLandingTick = -1;

    private MovementSnapshot _snapshot;

    public ApexMovementCore Core => _core;
    public ApexCameraRig Rig => _rig;

    // ── 跨语言只读调试接口 ──────────────────────────────────────────
    // GDScript 集成测试与 HUD 需要读求解器状态。ApexMovementCore 是纯 C# 类
    // （不继承 GodotObject），无法直接跨边界传递，所以在这里投影成基元类型。
    public float DebugFeetY => _core.FeetPosition.Y;
    public float DebugHeight => _core.CurrentHeight;
    public float DebugSpeed => _core.HorizontalSpeed;
    public bool DebugOnGround => _core.OnGround;
    public int DebugSlideHops => _core.SlideHopCount;
    public int DebugBunnyHops => _core.BunnyHopCount;
    public int DebugMantles => _core.MantleCount;
    public int DebugTick => _core.TickCount;
    public string DebugState => _core.State.ToString();
    /// <summary>HUD 上那一行摘要（截图脚本用来核对"图上数字 == 物理状态"，防止截到半路的旧帧）。</summary>
    public string DebugHudLine => _hud != null ? _hud.Text : string.Empty;
    public string DebugMantleClass => _core.CurrentMantleClass.ToString();
    public string DebugMantlePhase => _core.CurrentMantlePhase.ToString();
    public string DebugLandingTier => _core.LastLandingTier.ToString();
    public bool DebugJumpFatigue => _core.JumpFatigueActive;
    public bool DebugSlideBoostReady => !_core.SlideBoostOnCooldown;
    public float DebugFallStun => _core.FallStunRemaining;
    public float DebugFov => _rig.Fov;
    public float DebugRollDeg => _camOut.Roll * 180f / Mathf.Pi;
    public float DebugTraversalBlend => _camOut.TraversalBlend;
    public float DebugVerticalSpeed => _core.VerticalSpeed;

    /// <summary>台阶倾斜角（度）。>0 表示这一 tick 正在把水平速度抬成斜向上。</summary>
    public float DebugStepTiltDeg => _core.StepTiltAngle * (180f / Mathf.Pi);
    public bool DebugHolstered => _holstered;

    /// <summary>当前相机相对脚底的高度（米）。站立应为 1.85，蹲下应为 0.875。</summary>
    public float DebugCameraAboveFeet
    {
        get
        {
            if (_camera == null || _core == null) return 0f;
            F3 up = _core.Up;
            F3 feet = _core.FeetPosition;
            return F3.Dot(F3.From(_camera.GlobalPosition), up) - F3.Dot(feet, up);
        }
    }

    /// <summary>设定朝向（yaw，弧度）。测试脚本用它对准目标。</summary>
    public void DebugSetYaw(float yaw) => _core.Reset(_core.Position, yaw, _core.Pitch, _core.Up);

    /// <summary>设定俯仰（pitch，弧度，正数抬头）。截图脚本用它把视线压到墙面/地面，制造景深。</summary>
    public void DebugSetPitch(float pitch) => _core.Reset(_core.Position, _core.Yaw, pitch, _core.Up);

    /// <summary>把角色瞬移到指定位置（脚底贴地由物理自然处理）。</summary>
    public void DebugTeleport(float x, float y, float z)
    {
        GlobalPosition = new Vector3(x, y, z);
        _core.Reset(F3.From(GlobalPosition));
        _rig.Reset();
        _pendingLandingTier = LandingTier.None;
    }

    /// <summary>手动触发一次相机视角踢（测试 / 受击反馈）。</summary>
    public void DebugKick(float pitch, float yaw, float roll) => _rig.AddKick(pitch, yaw, roll);

    /// <summary>设定收枪状态（测试用）。收枪 = ×1.15 速度。</summary>
    public void DebugSetHolstered(bool holstered) => _holstered = holstered;

    /// <summary>直接设定基础 FOV（测试用）。</summary>
    public void DebugSetFov(float deg) => _rig.SetBaseFov(deg);

    /// <summary>
    /// **不再**把 <see cref="TickRate"/> 对齐到工程物理频率。
    ///
    /// ## 为什么 v0.3 反过来做
    /// v0.2 是"物理帧 = 移动 tick"，于是工程把 <c>physics_ticks_per_second</c>
    /// 改成 30 或 144，移动的时间轴就跟着变（`SyncTickRateWithEngine` 只能
    /// 强行把 TickRate 改成同样的值来遮住速度失真，代价是**求解器时间轴完全
    /// 由工程设置决定** —— 同一份输入在不同工程设置下轨迹不同）。
    ///
    /// 需求要的是"移动 tick 固定 60 /s 为基准"。所以 v0.3 改成：
    ///   · 求解器 tick **恒为 60 Hz**（<see cref="MovementConfig.TickRate"/>）
    ///   · 物理帧只负责"把这一帧的时间交给累加器"
    ///   · <see cref="ApexTickClock"/> 决定这帧该跑 0/1/2… 个 tick
    ///
    /// 于是工程物理频率设 30 也不影响轨迹（一帧跑 2 个 tick），设 144 也不影响
    /// （多数帧跑 0 个 tick）。速度永远正确，不需要"自动对齐"这种补偿。
    ///
    /// 性能开销：单帧要追赶的 tick 数受 <see cref="MovementConfig.MaxCatchUpTicks"/>
    /// 限制；而求解器里最贵的翻越/窗口探针在配置层就降到 30 Hz
    /// （<see cref="MovementConfig.TraversalProbeInterval"/> = 2），
    /// 这就是需求里"开销大的地方可以降到 30 tick/s"。
    /// </summary>
    private void SetupTickClock()
    {
        int engineTick = (int)Engine.PhysicsTicksPerSecond;

        if (TickRate != MovementConfig.PinnedTickRate)
        {
            GD.PushWarning(
                $"[ApexPlayer] TickRate 被设为 {TickRate}，v0.3 起移动求解器固定为 " +
                $"{MovementConfig.PinnedTickRate}Hz 基准，已强制使用 {MovementConfig.PinnedTickRate}。" +
                "需要降档请改 MovementConfig.TraversalProbeInterval（昂贵探针），不要改 TickRate。");
            TickRate = MovementConfig.PinnedTickRate;
        }

        if (engineTick > 0 && engineTick != TickRate)
        {
            // 只是提示，不做任何补偿 —— 累加器保证速度正确。
            GD.Print(
                $"[ApexPlayer] 工程物理频率 {engineTick}Hz ≠ 移动 tick {TickRate}Hz，" +
                $"由 ApexTickClock 折算（每帧平均 {TickRate / (float)engineTick:0.##} 个 tick）。");
        }

        _clock = new ApexTickClock(1f / TickRate, MovementConfig.PinnedMaxCatchUpTicks);
    }

    public override void _Ready()
    {
        SetupTickClock();

        MovementConfig cfg = ConfigPreset == Preset.Legacy
            ? MovementConfig.Legacy()
            : MovementConfig.Apex();

        cfg.StandHeight = StandHeight;
        cfg.CrouchHeight = CrouchHeight;
        cfg.EyeHeightOffset = EyeHeightOffset;
        cfg.TickRate = TickRate;
        if (WalkSpeedOverride > 0f) cfg.WalkSpeed = WalkSpeedOverride;
        if (SprintSpeedOverride > 0f) cfg.SprintSpeed = SprintSpeedOverride;
        if (JumpHeightOverride > 0f) cfg.JumpHeight = JumpHeightOverride;
        cfg.Validate();

        _core = new ApexMovementCore(cfg);
        _core.Reset(F3.From(GlobalPosition));
        _snapshot = _core.Snapshot;

        var camCfg = new CameraEffectsConfig { FovBase = FovBase };
        camCfg.Validate();
        _rig = new ApexCameraRig(camCfg);

        _holstered = StartHolstered;

        _spawnPoint = GlobalPosition;
        _spawnCaptured = true;

        _camera = GetNodeOrNull<Camera3D>("Camera3D");
        if (_camera == null)
        {
            _camera = new Camera3D { Name = "Camera3D", Current = true };
            AddChild(_camera);
        }

        // FOV 以**水平**角解释（与 Apex 的 FOV 设置一致）
        _camera.KeepAspect = Camera3D.KeepAspectEnum.Width;
        _camera.Fov = FovBase;
        _camera.Near = 0.05f;
        _camera.Far = 4000f;

        // 让外部工具（截图 / 观战相机 / 调试面板）能按组找到玩家相机，
        // 而不必硬编码节点路径。截图脚本 shot_testbed.gd 依赖这个组名。
        if (!_camera.IsInGroup("player_cameras")) _camera.AddToGroup("player_cameras");

        if (ShowDebugHud) BuildHud();
        if (ShowDebugMenu)
        {
            // 先保证 `\` 这个动作存在，再建面板 —— 否则面板建好了却按不出来。
            EnsureAction("apex_debug_menu", Key.Backslash);
            BuildDebugMenu();
        }

        Input.MouseMode = Input.MouseModeEnum.Captured;
        _mouseCaptured = true;
    }

    /// <summary>
    /// 构建运行时调试菜单（`\` 键开关）。
    ///
    /// 需求原话：「我没看到调试菜单，我希望按 "\" 键进入调试菜单，
    /// 调试菜单可以更改 fov，再按 "\" 关闭」。
    ///
    /// 菜单节点自己不知道移动逻辑，只发 C# 事件；这里把事件接到求解器/相机上。
    /// </summary>
    private void BuildDebugMenu()
    {
        _debugMenu = new ApexDebugMenu { Name = "DebugMenu", Layer = 20 };
        AddChild(_debugMenu);
        _debugMenu.Build(FovBase, MouseSensitivity,
                         _rig.Config.BobGaitScale, _rig.Config.KickScale,
                         CameraEffectsEnabled, ShowDebugHud);

        _debugMenu.FovChanged += v =>
        {
            _rig.SetBaseFov(v);
            FovBase = v;
        };
        _debugMenu.SensitivityChanged += v => MouseSensitivity = v;
        _debugMenu.BobScaleChanged += v => _rig.Config.BobGaitScale = v;
        _debugMenu.KickScaleChanged += v => _rig.Config.KickScale = v;
        _debugMenu.CameraEffectsToggled += on => CameraEffectsEnabled = on;
        _debugMenu.HudToggled += SetHudVisible;
        _debugMenu.CloseRequested += CloseDebugMenu;

        // 初始不可见（_Ready 里已设，这里再确认一次语义）
        _debugMenu.Visible = false;
        _debugMenuOpen = false;
    }

    /// <summary>切换调试菜单显隐（`\` 键）。</summary>
    public void ToggleDebugMenu()
    {
        if (_debugMenu == null) return;
        if (_debugMenuOpen) CloseDebugMenu(); else OpenDebugMenu();
    }

    private void OpenDebugMenu()
    {
        if (_debugMenu == null || _debugMenuOpen) return;
        _debugMenuOpen = true;
        _debugMenu.Visible = true;

        // 打开菜单必须放开鼠标，否则滑条拖不动。
        _mouseCaptured = false;
        Input.MouseMode = Input.MouseModeEnum.Visible;

        // 把面板上的数值同步成"此刻真实在用的值"（玩家可能用 [ / ] 改过 FOV）。
        _debugMenu.SyncFov(_rig.Config.FovBase);
        _debugMenu.SyncSensitivity(MouseSensitivity);
    }

    private void CloseDebugMenu()
    {
        if (_debugMenu == null || !_debugMenuOpen) return;
        _debugMenuOpen = false;
        _debugMenu.Visible = false;

        _mouseCaptured = true;
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    /// <summary>调试菜单是否正在显示（自检/截图脚本用）。</summary>
    public bool DebugMenuOpen => _debugMenuOpen;

    /// <summary>显示/隐藏左上角状态行。</summary>
    public void SetHudVisible(bool on)
    {
        ShowDebugHud = on;
        if (on)
        {
            if (_hud == null) BuildHud();
            else _hud.Visible = true;
        }
        else if (_hud != null)
        {
            _hud.Visible = false;
        }
    }

    /// <summary>
    /// 保证某个输入动作存在（不存在就现场注册）。
    ///
    /// 为什么不在 C# 里依赖 `apex_input_map.gd`：
    /// 那个脚本是 GDScript，而 <see cref="ApexPlayer"/> 是纯 C# 节点 ——
    /// 插件被拖进宿主项目时如果没人调 `ApexInputMap.ensure_all()`，
    /// 这里就会 <c>Input.IsActionPressed("apex_debug_menu")</c> 报错。
    /// 调试菜单是"排障入口"，它必须无论如何都能打开，所以在这里自给自足。
    ///
    /// 已存在的同名动作**不覆盖**（尊重宿主项目的键位自定义）。
    /// </summary>
    private static void EnsureAction(string action, Key physicalKey)
    {
        if (InputMap.HasAction(action)) return;
        InputMap.AddAction(action, 0.2f);
        InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = physicalKey });
    }

    private void BuildHud()
    {
        var layer = new CanvasLayer { Name = "HudLayer" };
        AddChild(layer);

        _hud = new Label
        {
            Name = "DebugHud",
            Position = new Vector2(16, 16),
            Text = "ApexMovement v0.4.1",
        };
        _hud.AddThemeColorOverride("font_color", new Color(0.9f, 1f, 0.9f));
        _hud.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 1));
        _hud.AddThemeConstantOverride("outline_size", 4);
        layer.AddChild(_hud);
    }

    /// <summary>注入重力方向（球形行星时传行星局部 up）。</summary>
    public void SetUp(F3 up) => _core.Reset(_core.Position, _core.Yaw, _core.Pitch, up);

    public override void _UnhandledInput(InputEvent @event)
    {
        // ── 调试菜单开关（`\` 键，v0.4） ──────────────────────────
        //
        // 放在最前面：菜单打开时其它按键（包括 Esc）不应该穿透到游戏逻辑，
        // 否则"想拖滑条"会变成"切鼠标捕获"。
        if (@event.IsActionPressed("apex_debug_menu"))
        {
            ToggleDebugMenu();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (_debugMenuOpen)
        {
            // 菜单打开时 Esc 也当"关闭菜单"，符合直觉。
            if (@event.IsActionPressed("ui_cancel"))
            {
                CloseDebugMenu();
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        if (@event is InputEventMouseMotion mm && _mouseCaptured)
        {
            _pendingMouseX += mm.Relative.X * MouseSensitivity;
            _pendingMouseY += mm.Relative.Y * MouseSensitivity;
        }
        else if (@event.IsActionPressed("ui_cancel"))
        {
            _mouseCaptured = !_mouseCaptured;
            Input.MouseMode = _mouseCaptured
                ? Input.MouseModeEnum.Captured
                : Input.MouseModeEnum.Visible;
        }
        else if (@event.IsActionPressed("apex_fov_up"))
        {
            _rig.AdjustBaseFov(_rig.Config.FovUserStep);
            _debugMenu?.SyncFov(_rig.Config.FovBase);
        }
        else if (@event.IsActionPressed("apex_fov_down"))
        {
            _rig.AdjustBaseFov(-_rig.Config.FovUserStep);
            _debugMenu?.SyncFov(_rig.Config.FovBase);
        }
        else if (@event.IsActionPressed("apex_holster"))
        {
            _holstered = !_holstered;
        }
        else if (@event.IsActionPressed("apex_reset"))
        {
            Respawn();
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        // 1) 采样输入（保持状态）
        _moveX = Input.GetAxis("apex_move_left", "apex_move_right");
        _moveZ = Input.GetAxis("apex_move_back", "apex_move_forward");
        _crouchHeld = Input.IsActionPressed("apex_crouch");
        _sprintHeld = Input.IsActionPressed("apex_sprint");

        bool jumpPressed = Input.IsActionJustPressed("apex_jump");
        bool jumpHeld = Input.IsActionPressed("apex_jump");
        if (jumpPressed) _jumpQueued = true;

        // 1b) 调试菜单打开时**冻结移动输入**：
        //     拖滑条时手不会离开鼠标，若不冻结，玩家会一边拖一边往前跑。
        //     注意只冻结输入，物理照常跑（下落/惯性仍然正常），
        //     这样"边调 FOV 边看跑动"也能工作。
        if (_debugMenuOpen)
        {
            _moveX = 0f;
            _moveZ = 0f;
            _crouchHeld = false;
            _sprintHeld = false;
            _jumpQueued = false;
            _pendingMouseX = 0f;
            _pendingMouseY = 0f;
        }

        // 2) 物理空间（每次查询前取一次；求解器的所有射线都在 tick 内部完成）
        PhysicsDirectSpaceState3D space = GetWorld3D().DirectSpaceState;
        if (_world == null)
        {
            _world = new GodotMovementWorld(space);
            _core.BindWorld(_world);
            RefreshSlipVolumes();
        }

        // 3) 把本帧时间交给累加器，然后**跑够 60Hz 该跑的 tick 数**
        //
        //    · 工程物理 60Hz ⇒ 每帧 1 个 tick（和 v0.2 一样）
        //    · 工程物理 30Hz ⇒ 每帧 2 个 tick
        //    · 工程物理 144Hz ⇒ 多数帧 0 个、少数帧 1 个
        //    三种情况下移动的时间轴完全一样 —— 这就是"固定 60 tick/s"。
        _clock.Accumulate((float)delta);
        _lastFrameTicks = 0;

        while (_clock.TryConsumeTick(out float tickDt))
        {
            // 鼠标增量**整份只消费一次**（一帧跑两个 tick 时，第二个 tick 不能再转一次视角，
            // 否则视角转速会随帧率翻倍）。
            float mdx = _pendingMouseX, mdy = _pendingMouseY;
            _pendingMouseX = 0f;
            _pendingMouseY = 0f;

            // 跳跃上升沿同理：一帧只应触发一次。
            bool jumpEdge = _jumpQueued;
            _jumpQueued = false;

            var input = new MovementInput
            {
                MoveX = _moveX,
                MoveZ = _moveZ,
                JumpPressed = jumpEdge,
                JumpHeld = jumpHeld,
                CrouchHeld = _crouchHeld,
                SprintHeld = _sprintHeld,
                Holstered = _holstered,
                Yaw = _core.Yaw,
                Pitch = _core.Pitch,
                MouseDeltaYaw = mdx,
                MouseDeltaPitch = InvertY ? mdy : -mdy,
            };

            // 4) 固定 tick 推进（dt 恒为 FixedDelta = 1/60）
            _core.Step(input, tickDt);
            _lastFrameTicks++;

            // 5) 掉出世界兜底：直接复活，避免"掉入虚空后无法继续测试"
            if (_core.Position.Y < KillPlaneY)
            {
                Respawn();
                return;
            }

            // 6) 抓取落地事件（本 tick 只出现一次，交给渲染帧的相机消费）
            _snapshot = _core.Snapshot;
            if (_snapshot.LandingTier != LandingTier.None)
            {
                _pendingLandingTier = _snapshot.LandingTier;
                _pendingFallDistance = _snapshot.LandingFallDistance;
                _pendingImpact = _snapshot.LandingImpactSpeed;
                _pendingLandingTick = _snapshot.TickCount;
            }
        }

        // 7) 同步到场景（一帧内已跑完所有 tick，只写一次变换）
        GlobalPosition = _core.Position.ToGodot();
    }

    public override void _Process(double delta)
    {
        if (_core == null) return;

        // 相机按**渲染帧**更新（观感平滑），输入是最近一次物理 tick 的状态快照。
        MovementSnapshot snap = _snapshot;
        if (_pendingLandingTick >= 0)
        {
            snap.LandingTier = _pendingLandingTier;
            snap.LandingFallDistance = _pendingFallDistance;
            snap.LandingImpactSpeed = _pendingImpact;
            snap.TickCount = _pendingLandingTick;
            _pendingLandingTier = LandingTier.None;
            _pendingLandingTick = -1;
        }
        else
        {
            snap.LandingTier = LandingTier.None;
        }

        if (CameraEffectsEnabled)
            _camOut = _rig.Update(snap, (float)delta);
        else
            _camOut = new CameraOutput { Fov = _rig.Config.FovBase };

        UpdateCamera();

        if (ShowDebugHud) UpdateHud();
    }

    /// <summary>回到出生点（掉出世界兜底 / 手动重置）。</summary>
    public void Respawn()
    {
        Vector3 target = _spawnCaptured ? _spawnPoint : new Vector3(0f, 2f, 0f);
        GlobalPosition = target;
        _core.Reset(F3.From(target));
        _rig.Reset();
        _pendingMouseX = 0f;
        _pendingMouseY = 0f;
        _jumpQueued = false;
        _pendingLandingTier = LandingTier.None;
        _pendingLandingTick = -1;
    }

    /// <summary>
    /// 收集场景里所有 <c>slip_volume</c> 组的节点，注册给世界查询。
    ///
    /// 逆向里打滑是 <c>C_TriggerSlipSphere</c> 这类**世界体积**推挤（不是地面材质），
    /// 所以这里是"场景里放了几个体积就有几个"。随时可以再调一次刷新
    /// （例如关卡流式加载后）。
    /// </summary>
    public void RefreshSlipVolumes()
    {
        if (_world == null) return;

        var volumes = new System.Collections.Generic.List<Node3D>();
        foreach (Node node in GetTree().GetNodesInGroup("slip_volume"))
        {
            if (node is Node3D n3) volumes.Add(n3);
        }
        _world.SetSlipVolumes(volumes);
    }

    private void UpdateCamera()
    {
        if (_camera == null) return;

        F3 up = _core.Up;
        float pitch = _core.Pitch + _camOut.PitchOffset;
        float yaw = _core.Yaw + _camOut.YawOffset;

        // 水平前向（支持任意重力方向）
        F3 fwdH = ApexCameraRig.ForwardFromYaw(yaw, up);
        F3 rightH = F3.Cross(fwdH, up).Normalized();

        // 加上俯仰
        float cp = Mathf.Cos(pitch), sp = Mathf.Sin(pitch);
        F3 fwd = (fwdH * cp + up * sp).Normalized();
        F3 camUp = F3.Cross(rightH, fwd).Normalized();

        // 叠加滚转（绕视线轴旋转）
        float cr = Mathf.Cos(_camOut.Roll), sr = Mathf.Sin(_camOut.Roll);
        F3 right = rightH * cr + camUp * sr;
        F3 upR = camUp * cr - rightH * sr;

        // 眼位 = 脚底 + (当前身高 − EyeHeightOffset) + 相机偏移
        Vector3 feet = (_core.Position - up * (_core.CurrentHeight * 0.5f)).ToGodot();
        Vector3 eye = feet
                    + up.ToGodot() * (_core.CurrentHeight - _core.Config.EyeHeightOffset)
                    + _camOut.EyeOffset.ToGodot();

        var basis = new Basis(
            new Vector3(right.X, right.Y, right.Z),
            new Vector3(upR.X, upR.Y, upR.Z),
            new Vector3(-fwd.X, -fwd.Y, -fwd.Z));

        _camera.GlobalTransform = new Transform3D(basis, eye);
        _camera.Fov = _camOut.Fov;
    }

    private void UpdateHud()
    {
        if (_hud == null) return;

        string fatigue = _core.JumpFatigueActive ? " YES" : " no";
        string boost = _core.SlideBoostOnCooldown ? "冷却" : "就绪";
        string holster = _holstered ? "收枪" : "持枪";
        string stun = _core.FallStunRemaining > 0.01f ? $"{_core.FallStunRemaining:F2}s" : "-";

        // v0.3 完整版：打滑 / 扒墙 一行摘要
        string slip = _snapshot.Slipping
            ? $"SLIP dir={_snapshot.SlipDirection.X:F2},{_snapshot.SlipDirection.Z:F2}"
            : (_core.IsSlipAirRestricted ? $"restrict {_snapshot.SlipAirRestrictRemaining:F2}s" : "no");
        string hang = _snapshot.WallHanging
            ? $"WALLHANG grip={_snapshot.WallHangGrip:F2} t={_snapshot.WallHangTime:F2}s"
            : "no";

        // v0.4：步态摘要（频率来自步态模型，是"抖动频率"最直接的证据）
        string gait = _snapshot.Gait.CadenceHz > 0.05f
            ? $"{_snapshot.Gait.CadenceHz:F2}Hz stride={_snapshot.Gait.StrideLength:F2}m " +
              $"fly={_snapshot.Gait.FlightFraction:F2} vAmp={_snapshot.Gait.VerticalAmplitude * 1000f:F1}mm"
            : "idle";

        // v0.4：台阶倾斜（<=0.25m 台阶「只改方向不改大小」的可读证据）
        float tiltDeg = _core.StepTiltAngle * (180f / Mathf.Pi);
        if (tiltDeg > 0.05f) gait += $" step={tiltDeg:F1}deg";

        _hud.Text =
            $"ApexMovement v0.4.1  tick={_core.TickCount}\n" +
            $"state={_core.State}  speed={_core.HorizontalSpeed:F2} m/s  vy={_core.VerticalSpeed:F2}\n" +
            $"ground={_core.OnGround}  crouch={_core.CrouchAmount:F2}  h={_core.CurrentHeight:F2}  {holster}\n" +
            $"fov={_camOut.Fov:F1}°  roll={_camOut.Roll * 180f / Mathf.Pi:F1}°  bobPhase={_snapshot.BobPhase:F2}\n" +
            $"gait={gait}\n" +
            $"mantle={_core.CurrentMantleClass}/{_core.CurrentMantlePhase}  xBlend={_camOut.TraversalBlend:F2}\n" +
            $"slip={slip}  hang={hang}\n" +
            $"slideBoost={boost}  slideHops={_core.SlideHopCount}  bhops={_core.BunnyHopCount}  mantles={_core.MantleCount}\n" +
            $"jumpFatigue={fatigue}  fallstun={stun}\n" +
            $"[WASD] move  [Space] jump/vault  [Ctrl] crouch  [Shift] sprint\n" +
            $"[W贴墙+Space] 爬墙  [松W] 扒住  [S] 松手直落  [A/D] 横移  [H] 收枪  [ [ / ] ] FOV  [R] 复位  [\\] 调试菜单";
    }
}
