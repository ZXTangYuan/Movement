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

    [ExportGroup("Input")]
    [Export] public float MouseSensitivity = 0.0025f;
    [Export] public bool InvertY = false;

    [ExportGroup("Tick")]
    [Export] public int TickRate = 60;

    [ExportGroup("Safety")]
    /// <summary>掉到这个世界 Y 值以下就原地复活（防"掉入虚空"后无法继续测试）。</summary>
    [Export] public float KillPlaneY = -50f;

    private ApexMovementCore _core = null!;
    private ApexCameraRig _rig = null!;
    private GodotMovementWorld _world = null!;
    private Camera3D _camera = null!;
    private Label _hud = null!;

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
    /// 把 <see cref="TickRate"/> 对齐到工程的物理频率。
    ///
    /// <para>
    /// <see cref="_PhysicsProcess"/> 每帧**只推进一个 tick**，用的是
    /// <c>1/TickRate</c> 的固定 dt。如果工程把 <c>physics/common/physics_ticks_per_second</c>
    /// 改成 120 而这里仍是 60，那么每帧推进 1/60 秒、但一秒钟有 120 帧 ⇒
    /// 角色以**两倍速**移动。这类"改了工程设置角色速度就变了"的隐性 bug
    /// 很难在自检里发现，所以直接在运行时对齐。
    /// </para>
    /// </summary>
    private void SyncTickRateWithEngine()
    {
        int engineTick = (int)Engine.PhysicsTicksPerSecond;
        if (engineTick > 0 && engineTick != TickRate)
        {
            GD.PushWarning(
                $"[ApexPlayer] TickRate={TickRate} 与工程物理频率 {engineTick}Hz 不一致，" +
                $"已自动对齐到 {engineTick}Hz（否则移动速度会按 {engineTick / (float)TickRate:0.##}× 失真）。");
            TickRate = engineTick;
        }
    }

    public override void _Ready()
    {
        SyncTickRateWithEngine();

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

        Input.MouseMode = Input.MouseModeEnum.Captured;
        _mouseCaptured = true;
    }

    private void BuildHud()
    {
        var layer = new CanvasLayer { Name = "HudLayer" };
        AddChild(layer);

        _hud = new Label
        {
            Name = "DebugHud",
            Position = new Vector2(16, 16),
            Text = "ApexMovement v0.2",
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
        }
        else if (@event.IsActionPressed("apex_fov_down"))
        {
            _rig.AdjustBaseFov(-_rig.Config.FovUserStep);
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

        // 2) 物理空间（每 tick 取一次，保证查询与 tick 对齐）
        PhysicsDirectSpaceState3D space = GetWorld3D().DirectSpaceState;
        if (_world == null) { _world = new GodotMovementWorld(space); _core.BindWorld(_world); }

        // 3) 组装本 tick 输入：鼠标增量**整份消费**
        var input = new MovementInput
        {
            MoveX = _moveX,
            MoveZ = _moveZ,
            JumpPressed = _jumpQueued,
            JumpHeld = jumpHeld,
            CrouchHeld = _crouchHeld,
            SprintHeld = _sprintHeld,
            Holstered = _holstered,
            Yaw = _core.Yaw,
            Pitch = _core.Pitch,
            MouseDeltaYaw = _pendingMouseX,
            MouseDeltaPitch = InvertY ? _pendingMouseY : -_pendingMouseY,
        };
        _pendingMouseX = 0f;
        _pendingMouseY = 0f;
        _jumpQueued = false;

        // 4) 固定 tick 推进（dt 恒为 FixedDelta）
        _core.Step(input, _core.Config.FixedDelta);

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

        // 7) 同步到场景
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

        _hud.Text =
            $"state={_core.State}  speed={_core.HorizontalSpeed:F2} m/s  vy={_core.VerticalSpeed:F2}\n" +
            $"ground={_core.OnGround}  crouch={_core.CrouchAmount:F2}  h={_core.CurrentHeight:F2}  {holster}\n" +
            $"fov={_camOut.Fov:F1}°  roll={_camOut.Roll * 180f / Mathf.Pi:F1}°  bobPhase={_snapshot.BobPhase:F2}\n" +
            $"mantle={_core.CurrentMantleClass}/{_core.CurrentMantlePhase}  xBlend={_camOut.TraversalBlend:F2}\n" +
            $"slideBoost={boost}  slideHops={_core.SlideHopCount}  bhops={_core.BunnyHopCount}  mantles={_core.MantleCount}\n" +
            $"jumpFatigue={fatigue}  fallstun={stun}  tick={_core.TickCount}\n" +
            $"[WASD] move  [Space] jump/climb  [Ctrl] crouch  [Shift] sprint\n" +
            $"[Shift+Ctrl] slide  [Shift+Ctrl+Space] slide-jump  [H] holster  [ [ / ] ] FOV  [R] reset";
    }
}
