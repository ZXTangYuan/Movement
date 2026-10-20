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

        bool all = detPass && featurePass && camPass && climbPass && gaitPass && slipHangPass;
        GD.Print(all
            ? "=== APEX MOVEMENT SELFTEST PASS ==="
            : "=== APEX MOVEMENT SELFTEST FAIL ===");

        // 无头运行完立即退出，避免挂住
        if (DisplayServer.GetName() == "headless")
            GetTree().Quit(all ? 0 : 1);
    }
}
