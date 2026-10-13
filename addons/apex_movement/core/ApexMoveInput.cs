using Godot;

namespace ApexMovement;

/// <summary>移动输入按钮位（供网络同步的输入帧使用）。</summary>
public enum ApexMoveButton
{
    None = 0,
    Jump = 1 << 0,
    Crouch = 1 << 1,
    Sprint = 1 << 2,
    Forward = 1 << 3,
    Back = 1 << 4,
    Left = 1 << 5,
    Right = 1 << 6,
}

/// <summary>
/// 一帧移动输入（确定性、可序列化）。玩法层把按键 + 相机朝向转成 <see cref="WishDir"/>，
/// 核心只认这个归一化方向 + 按钮位，因此不依赖任何引擎输入 API。
/// </summary>
[GlobalClass]
public partial class ApexMoveInput : RefCounted
{
    /// <summary>期望移动方向（**世界空间**，已投影到 up 切平面并归一化）。无输入时为零向量。</summary>
    public Vector3 WishDir { get; set; } = Vector3.Zero;

    /// <summary>本帧按下的按钮位（见 <see cref="ApexMoveButton"/>）。</summary>
    public int Buttons { get; set; }

    /// <summary>本帧「刚按下」跳跃（边沿触发）。</summary>
    public bool JumpPressed { get; set; }

    /// <summary>跳跃键是否按住。</summary>
    public bool JumpHeld { get; set; }

    public bool IsDown(ApexMoveButton b) => (Buttons & (int)b) != 0;

    // GDScript 无法直接访问 C# 嵌套枚举，故暴露整型常量（与 ApexMoveButton 一致）。
    public const int BTN_JUMP = 1 << 0;
    public const int BTN_CROUCH = 1 << 1;
    public const int BTN_SPRINT = 1 << 2;
    public const int BTN_FORWARD = 1 << 3;
    public const int BTN_BACK = 1 << 4;
    public const int BTN_LEFT = 1 << 5;
    public const int BTN_RIGHT = 1 << 6;

    public ApexMoveInput Clone() => new()
    {
        WishDir = WishDir,
        Buttons = Buttons,
        JumpPressed = JumpPressed,
        JumpHeld = JumpHeld,
    };

    /// <summary>紧凑序列化：wishdir(3) + buttons(1) + jumpPressed(1) + jumpHeld(1)。</summary>
    public float[] GetInput()
    {
        return new[]
        {
            WishDir.X, WishDir.Y, WishDir.Z,
            Buttons,
            JumpPressed ? 1f : 0f,
            JumpHeld ? 1f : 0f,
        };
    }

    public void SetInput(float[] v)
    {
        WishDir = new Vector3(v[0], v[1], v[2]);
        Buttons = (int)v[3];
        JumpPressed = v[4] > 0.5f;
        JumpHeld = v[5] > 0.5f;
    }
}
