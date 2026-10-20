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

        bool all = detPass && featurePass && camPass;
        GD.Print(all
            ? "=== APEX MOVEMENT SELFTEST PASS ==="
            : "=== APEX MOVEMENT SELFTEST FAIL ===");

        // 无头运行完立即退出，避免挂住
        if (DisplayServer.GetName() == "headless")
            GetTree().Quit(all ? 0 : 1);
    }
}
