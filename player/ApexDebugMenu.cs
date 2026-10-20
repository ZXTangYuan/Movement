using System;
using Godot;

namespace ApexMovement;

/// <summary>
/// 运行时调试菜单（v0.4 新增）。
///
/// 需求原话：「我没看到调试菜单，我希望按 "\" 键进入调试菜单，调试菜单可以更改 fov，
/// 再按 "\" 关闭」。
///
/// 设计取舍：
///   · 这是**游戏内**的即时面板（不是 Godot 编辑器的面板），因为它要能在打包后的
///     游戏里用，也要能在截图/自检脚本里驱动。
///   · 它自己不知道任何移动逻辑：所有值都通过 C# `event` 回调交给 <see cref="ApexPlayer"/>，
///     面板只负责"显示 + 收输入"。这样面板可以单独替换而不碰移动代码。
///   · 面板打开时由 <see cref="ApexPlayer"/> 释放鼠标捕获，否则 HSlider 拖不动。
///
/// 面板里能改的东西（每一项都是"调手感时最想边看边改"的量）：
///   FOV                60 … 120（水平角，与 Apex 的 FOV 口径一致）
///   鼠标灵敏度          0.0005 … 0.0100
///   步态摇晃倍率        0 … 2（1 = 步态模型的物理推导值，0 = 完全关掉走路摇晃）
///   落地冲击倍率        0 … 2（1 = 舒适档默认）
///   相机效果总开关      关掉 = 只留干净的视点（排障用）
///   调试 HUD           显隐左上角的状态行
/// </summary>
public partial class ApexDebugMenu : CanvasLayer
{
    // ── 对外事件（ApexPlayer 订阅） ─────────────────────────────
    public event Action<float>? FovChanged;
    public event Action<float>? SensitivityChanged;
    public event Action<float>? BobScaleChanged;
    public event Action<float>? KickScaleChanged;
    public event Action<bool>? CameraEffectsToggled;
    public event Action<bool>? HudToggled;
    public event Action? CloseRequested;

    // ── 控件 ───────────────────────────────────────────────────
    private Control _root = null!;
    private Label _fovValue = null!;
    private Label _sensValue = null!;
    private Label _bobValue = null!;
    private Label _kickValue = null!;
    private HSlider _fovSlider = null!;
    private HSlider _sensSlider = null!;
    private HSlider _bobSlider = null!;
    private HSlider _kickSlider = null!;
    private CheckButton _camToggle = null!;
    private CheckButton _hudToggle = null!;

    /// <summary>面板是否已构建（重复调用 <see cref="Build"/> 只建一次）。</summary>
    public bool Built { get; private set; }

    public override void _Ready()
    {
        // 菜单必须在暂停/暂停之外都能用；这里不改 process mode，
        // 由 ApexPlayer 决定什么时候显示。
        Visible = false;
    }

    /// <summary>
    /// 构建面板。参数是"当前值"，用于初始化滑条位置 —— 这样打开菜单看到的
    /// 就是玩家此刻真实在用的数值，而不是一份默认值。
    /// </summary>
    public void Build(float fov, float sensitivity, float bobScale, float kickScale,
                      bool cameraEffects, bool hud)
    {
        if (Built) return;
        Built = true;

        _root = new Control
        {
            Name = "Root",
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        // 贴右上角：左上角是调试 HUD 的地盘，两个都放左边会互相压字。
        // 用锚点而不是绝对坐标 —— 换分辨率/窗口大小后面板仍贴住右边缘。
        _root.AnchorLeft = 1f;
        _root.AnchorRight = 1f;
        _root.AnchorTop = 0f;
        _root.AnchorBottom = 0f;
        _root.OffsetLeft = -370f;
        _root.OffsetRight = -28f;
        _root.OffsetTop = 28f;
        _root.OffsetBottom = 28f;
        AddChild(_root);

        var panel = new PanelContainer { Name = "Panel" };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.08f, 0.10f, 0.92f),
            BorderColor = new Color(0.35f, 0.85f, 0.65f, 0.85f),
            ContentMarginLeft = 18f,
            ContentMarginRight = 18f,
            ContentMarginTop = 14f,
            ContentMarginBottom = 16f,
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(6);
        panel.AddThemeStyleboxOverride("panel", style);
        _root.AddChild(panel);

        var box = new VBoxContainer { Name = "Box" };
        box.AddThemeConstantOverride("separation", 10);
        panel.AddChild(box);

        // 标题
        var title = new Label { Text = "调试菜单" };
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeColorOverride("font_color", new Color(0.65f, 1f, 0.85f));
        box.AddChild(title);

        var hint = new Label { Text = "按 \\ 关闭    （鼠标已释放，可直接拖滑条）" };
        hint.AddThemeFontSizeOverride("font_size", 13);
        hint.AddThemeColorOverride("font_color", new Color(0.65f, 0.70f, 0.75f));
        box.AddChild(hint);

        box.AddChild(new HSeparator());

        // ── 各个可调项 ──
        _fovSlider = AddSlider(box, "视野 FOV（水平角）", 60f, 120f, 1f, fov, out _fovValue, "°");
        _fovSlider.ValueChanged += v =>
        {
            _fovValue.Text = $"{v:0}°";
            FovChanged?.Invoke((float)v);
        };

        _sensSlider = AddSlider(box, "鼠标灵敏度", 0.0005f, 0.0100f, 0.0001f, sensitivity,
                                out _sensValue, "", "0.0000");
        _sensSlider.ValueChanged += v =>
        {
            _sensValue.Text = $"{v:0.0000}";
            SensitivityChanged?.Invoke((float)v);
        };

        _bobSlider = AddSlider(box, "步态摇晃倍率", 0f, 2f, 0.05f, bobScale, out _bobValue, "×");
        _bobSlider.ValueChanged += v =>
        {
            _bobValue.Text = $"{v:0.00}×";
            BobScaleChanged?.Invoke((float)v);
        };

        _kickSlider = AddSlider(box, "落地冲击倍率", 0f, 2f, 0.05f, kickScale, out _kickValue, "×");
        _kickSlider.ValueChanged += v =>
        {
            _kickValue.Text = $"{v:0.00}×";
            KickScaleChanged?.Invoke((float)v);
        };

        box.AddChild(new HSeparator());

        _camToggle = AddToggle(box, "相机效果（摇晃 / 冲击 / FOV 动态）", cameraEffects);
        _camToggle.Toggled += on => CameraEffectsToggled?.Invoke(on);

        _hudToggle = AddToggle(box, "调试 HUD（左上角状态行）", hud);
        _hudToggle.Toggled += on => HudToggled?.Invoke(on);

        box.AddChild(new HSeparator());

        var close = new Button { Text = "关闭（\\）" };
        close.Pressed += () => CloseRequested?.Invoke();
        box.AddChild(close);
    }

    /// <summary>
    /// 自动化脚本用：把 FOV 滑条推到某个值。
    /// 走的是和玩家拖动**完全同一条** ValueChanged → FovChanged 回调链，
    /// 所以截图里看到的 FOV 变化就是真实交互路径的结果，不是旁路改状态。
    /// </summary>
    public void SetFovSliderValue(float v)
    {
        if (!Built) return;
        _fovSlider.Value = Mathf.Clamp(v, (float)_fovSlider.MinValue, (float)_fovSlider.MaxValue);
    }

    /// <summary>
    /// 一行滑条：左边标题、右边数值、下面滑条。返回滑条本身以便挂回调。
    /// </summary>
    private static HSlider AddSlider(VBoxContainer parent, string label, float min, float max,
                                     float step, float value, out Label valueLabel,
                                     string suffix, string fmt = "0")
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);

        var name = new Label { Text = label };
        name.AddThemeFontSizeOverride("font_size", 15);
        name.CustomMinimumSize = new Vector2(210f, 0f);
        row.AddChild(name);

        valueLabel = new Label { Text = suffix.Length > 0 ? value.ToString(fmt) + suffix : value.ToString(fmt) };
        valueLabel.AddThemeFontSizeOverride("font_size", 15);
        valueLabel.AddThemeColorOverride("font_color", new Color(1f, 0.88f, 0.45f));
        valueLabel.CustomMinimumSize = new Vector2(72f, 0f);
        valueLabel.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(valueLabel);

        parent.AddChild(row);

        var slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            Value = value,
            CustomMinimumSize = new Vector2(300f, 22f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        parent.AddChild(slider);
        return slider;
    }

    private static CheckButton AddToggle(VBoxContainer parent, string label, bool value)
    {
        var toggle = new CheckButton { Text = label };
        toggle.AddThemeFontSizeOverride("font_size", 15);
        parent.AddChild(toggle);
        // ★ 必须等进树之后再设初值：在构造器里设 ButtonPressed，按钮图标的
        //   按下态不会重算（实测截图里两个开关明明该是开着的，画出来却是关的）。
        toggle.SetPressedNoSignal(value);
        return toggle;
    }

    // ── 外部同步（例如玩家用 [ / ] 改了 FOV，菜单里的滑条要跟上） ──

    /// <summary>把 FOV 滑条同步到指定值（不触发回调）。</summary>
    public void SyncFov(float fov)
    {
        if (!Built) return;
        _fovSlider.SetValueNoSignal(Mathf.Clamp(fov, (float)_fovSlider.MinValue, (float)_fovSlider.MaxValue));
        _fovValue.Text = $"{fov:0}°";
    }

    /// <summary>把灵敏度滑条同步到指定值（不触发回调）。</summary>
    public void SyncSensitivity(float sensitivity)
    {
        if (!Built) return;
        _sensSlider.SetValueNoSignal(Mathf.Clamp(sensitivity,
            (float)_sensSlider.MinValue, (float)_sensSlider.MaxValue));
        _sensValue.Text = $"{sensitivity:0.0000}";
    }
}
