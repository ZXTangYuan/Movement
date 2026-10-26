using Godot;
using ApexMovement.Tests;

namespace ApexMovement.Scenes;

/// <summary>
/// 无头自检入口：跨帧率确定性 + 功能断言 + 相机子系统断言。
///
/// 运行：
///   godot --headless --path . res://addons/apex_movement/tests/determinism_test.tscn
///
/// 期望输出：=== APEX MOVEMENT SELFTEST PASS ===
/// </summary>
[GlobalClass]
public partial class DeterminismTestRunner : Node
{
    public override void _Ready()
    {
        GD.Print("=== 跨帧率确定性 ===");
        (bool detPass, string detReport) = DeterminismTest.Run();
        GD.Print(detReport);

        GD.Print("=== 功能验证 ===");
        (bool featurePass, string featureReport) = FeatureTest.Run();
        GD.Print(featureReport);

        GD.Print("=== 相机子系统 ===");
        (bool camPass, string camReport) = CameraTest.Run();
        GD.Print(camReport);

        GD.Print("=== v0.4 贴墙（攀爬 / 扒墙） / 三档障碍 / tick / 探针 ===");
        (bool climbPass, string climbReport) = ClimbTest.Run();
        GD.Print(climbReport);

        GD.Print("=== v0.4 步态模型 / 镜头抖动推导 ===");
        (bool gaitPass, string gaitReport) = GaitTest.Run();
        GD.Print(gaitReport);

        GD.Print("=== v0.4 打滑 / 扒墙（滑区与握力） ===");
        (bool slipHangPass, string slipHangReport) = SlipHangTest.Run();
        GD.Print(slipHangReport);

        GD.Print("=== v0.4.2 滑铲跳链 / 空中滑铲 / 抓墙方向 ===");
        (bool slideJumpPass, string slideJumpReport) = SlideJumpTest.Run();
        GD.Print(slideJumpReport);

        GD.Print("=== v0.4.3 镜头摇晃波形（∞ / 无回绕跳变） ===");
        (bool bobPass, string bobReport) = BobWaveformTest.Run();
        GD.Print(bobReport);

        GD.Print("=== v0.4.3 物理合规（重力 / 弹道 / 有限加速起跳 / 派生量） ===");
        (bool physicsPass, string physicsReport) = PhysicsTest.Run();
        GD.Print(physicsReport);

        GD.Print("=== v0.5.2 摩擦模型（体重 / 静摩擦驱动 / 冰面 / 滑铲输入 / 空中控制） ===");
        (bool frictionPass, string frictionReport) = FrictionTest.Run();
        GD.Print(frictionReport);

        bool all = detPass && featurePass && camPass && climbPass && gaitPass
                   && slipHangPass && slideJumpPass && bobPass && physicsPass
                   && frictionPass;
        GD.Print(all
            ? "=== APEX MOVEMENT SELFTEST PASS ==="
            : "=== APEX MOVEMENT SELFTEST FAIL ===");

        // 无头运行完立即退出，避免挂住
        if (DisplayServer.GetName() == "headless")
            GetTree().Quit(all ? 0 : 1);
    }
}
