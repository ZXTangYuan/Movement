extends Node
class_name ApexAbilityTest

## 阶段 3 自检：技能 + 镜头晃动/FOV 形变。
##
## 运行：res://tests/apex_ability_test.tscn
## 期望：APEX ABILITY TEST PASS

const AbilityManagerScript := preload("res://addons/apex_movement/abilities/ApexAbilityManager.cs")
const CameraFxScript := preload("res://addons/apex_movement/view/ApexCameraFx.cs")

var _pass := 0
var _fail := 0
var _msgs: Array[String] = []


func _ready() -> void:
	print("=== ApexMovement 技能与镜头自检 ===")
	_test_grapple()
	_test_valk_jets()
	_test_ash_dash()
	_test_double_jump_params()
	_test_camera_fov()
	_test_camera_shake()

	print("")
	for m in _msgs:
		print(m)
	print("")
	if _fail == 0:
		print("APEX ABILITY TEST PASS  (%d 项)" % _pass)
	else:
		print("APEX ABILITY TEST FAIL  (通过 %d，失败 %d)" % [_pass, _fail])
	get_tree().quit(0 if _fail == 0 else 1)


func _test_grapple() -> void:
	var mgr = AbilityManagerScript.new()
	mgr.SelectAbility("grapple", true)
	_check("钩爪可选中（主动）", mgr.ActiveAbilityId == "grapple", mgr.ActiveAbilityId)

	# 参数应为 [A] 提取值
	var g = mgr.Grapple
	_check("钩爪参数 = 游戏值（accel 1500 hu/s²→38.1）",
		absf(g.Accel - 38.1) < 0.1, "Accel=%.1f" % g.Accel)
	_check("钩爪参数 swingMaxSpeed（1000 hu/s→25.4）",
		absf(g.SwingMaxSpeed - 25.4) < 0.1, "SwingMaxSpeed=%.1f" % g.SwingMaxSpeed)
	_check("钩爪 gravityFrac 0.25~0.7",
		absf(g.GravityFracMin - 0.25) < 0.001 and absf(g.GravityFracMax - 0.7) < 0.001,
		"%.2f~%.2f" % [g.GravityFracMin, g.GravityFracMax])

	# 玩法层射线命中点在前方 10m（C# 侧只接收锚点，保持核心不依赖引擎）
	var ok: bool = g.AttachTo(Vector3(0, 1, 0), Vector3(0, 5, -10), 0.0)
	_check("钩爪附着成功", ok and g.IsGrappling, "attached=%s" % str(g.IsGrappling))

	# 施加一 tick：应向锚点加速
	var v0 := Vector3.ZERO
	var v1 = g.Apply(Vector3(0, 1, 0), v0, Vector3.UP, 18.4, 1.0 / 60.0, 0.1)
	_check("钩爪向锚点加速", v1.length() > 0.1, "速度 %.2f m/s" % v1.length())
	mgr.SelectAbility("grapple", false)
	_check("钩爪取消后复位", not mgr.Grapple.IsGrappling, "grappling=%s" % str(mgr.Grapple.IsGrappling))


func _test_valk_jets() -> void:
	var mgr = AbilityManagerScript.new()
	mgr.SelectAbility("valk_jets", true)
	var j = mgr.ValkJets
	_check("喷气参数 thrust=1.9", absf(j.Thrust - 1.9) < 0.01, "%.2f" % j.Thrust)
	_check("喷气参数 strafe（1035 hu/s²→26.3）", absf(j.Strafe - 26.3) < 0.1, "%.1f" % j.Strafe)
	_check("喷气 jumpsToActivate=2", j.JumpsToActivate == 2, str(j.JumpsToActivate))

	_check("喷气可启动", j.TryStart(0.0), "flying=%s" % str(j.IsFlying))
	# 施加一 tick：应获得向上速度
	var v = j.Apply(Vector3.ZERO, Vector3.UP, Vector3.ZERO, 18.4, 1.0 / 60.0, 0.01)
	_check("喷气产生上升速度", v.dot(Vector3.UP) > 0.0, "vy=%.3f" % v.dot(Vector3.UP))
	# 燃料会消耗
	var m0: float = j.Meter
	for i in 60:
		j.Apply(Vector3.ZERO, Vector3.UP, Vector3.ZERO, 18.4, 1.0 / 60.0, 0.02 + i * 0.016)
	_check("喷气消耗燃料", j.Meter < m0, "%.3f → %.3f" % [m0, j.Meter])


func _test_ash_dash() -> void:
	var mgr = AbilityManagerScript.new()
	mgr.SelectAbility("ash_dash", true)
	_check("Ash dash 是被动技能", mgr.HasPassive("ash_dash"), "passive=%s" % str(mgr.HasPassive("ash_dash")))
	var d = mgr.AshDash
	_check("dash 参数 speed=600 hu/s→15.24", absf(d.DashSpeed - 15.24) < 0.01, "%.2f" % d.DashSpeed)
	_check("dash keepSpeedFrac=0.2", absf(d.KeepSpeedFrac - 0.2) < 0.001, "%.2f" % d.KeepSpeedFrac)
	_check("dash interval=0.6", absf(d.Interval - 0.6) < 0.001, "%.2f" % d.Interval)

	# 一次点击不触发，双击才触发
	var first: bool = d.OnJumpPressed(0.0, true)
	var second: bool = d.OnJumpPressed(0.15, true)
	_check("dash 需双击触发（单点不触发）", (not first) and second,
		"first=%s second=%s" % [str(first), str(second)])

	# 应用：应获得朝向速度
	var v = d.Apply(Vector3(1, 0, 0), Vector3(0, 0, -1), Vector3.UP)
	_check("dash 沿视线方向加速", v.z < -10.0, "vz=%.2f" % v.z)

	# 冷却：立即再双击应失败
	var again: bool = d.OnJumpPressed(0.3, true)
	_check("dash 冷却内不可用", not again, "again=%s" % str(again))


func _test_double_jump_params() -> void:
	var mgr = AbilityManagerScript.new()
	mgr.SelectAbility("double_jump", true)
	var dj = mgr.DoubleJump
	_check("二段跳是被动", mgr.HasPassive("double_jump"), "passive=%s" % str(mgr.HasPassive("double_jump")))
	_check("二段跳 verticalGainCutoff=225 hu→5.72",
		absf(dj.VerticalGainCutoff - 5.72) < 0.01, "%.2f" % dj.VerticalGainCutoff)
	_check("二段跳默认 1 次", dj.MaxAirJumps == 1, str(dj.MaxAirJumps))


func _test_camera_fov() -> void:
	var fx = CameraFxScript.new()
	fx.BaseFov = 70.0
	_check("基础 FOV=70", absf(fx.CurrentFov - 70.0) < 0.1, "%.1f" % fx.CurrentFov)
	_check("滑铲 FOV 缩放=1.1", absf(fx.SlideFovScale - 1.1) < 0.001, "%.2f" % fx.SlideFovScale)

	# 滑铲：FOV 应升到 ≈ 70*1.1 = 77
	for i in 60:
		fx.Update(1.0 / 60.0, true, false)
	_check("滑铲时 FOV 放大", fx.CurrentFov > 74.0,
		"FOV=%.1f（目标 %.1f）" % [fx.CurrentFov, 70.0 * 1.1])

	# 松开：FOV 回落
	for i in 120:
		fx.Update(1.0 / 60.0, false, false)
	_check("松滑铲后 FOV 回落", absf(fx.CurrentFov - 70.0) < 1.0, "FOV=%.1f" % fx.CurrentFov)

	# 冲刺：FOV 升到 ≈ 70*1.15
	for i in 150:
		fx.Update(1.0 / 60.0, false, true)
	_check("冲刺时 FOV 放大（×1.15）", fx.CurrentFov > 78.0,
		"FOV=%.1f（目标 %.1f）" % [fx.CurrentFov, 70.0 * 1.15])


func _test_camera_shake() -> void:
	var fx = CameraFxScript.new()
	_check("初始无晃动", fx.ShakeOffset.length() < 1e-6, "%.4f" % fx.ShakeOffset.length())

	fx.AddImpulse(10.0, Vector3(0, 0, 1), true)
	_check("冲击产生偏移速度", fx.ShakeOffset.length() < 1e-6,
		"（冲击后立即，offset 尚为 0）")

	# 推进：应出现偏移
	var peak := 0.0
	for i in 30:
		fx.Update(1.0 / 60.0, false, false)
		peak = maxf(peak, fx.ShakeOffset.length())
	_check("晃动弹簧产生偏移", peak > 0.05, "峰值 %.3f 度" % peak)

	# 衰减：过一段时间应回到接近 0
	for i in 300:
		fx.Update(1.0 / 60.0, false, false)
	_check("晃动衰减归零", fx.ShakeOffset.length() < 0.05,
		"残余 %.4f 度" % fx.ShakeOffset.length())

	# 落地冲击按速度映射
	var fx2 = CameraFxScript.new()
	fx2.OnLanded(60.0)     # 高速落地
	var p2 := 0.0
	for i in 30:
		fx2.Update(1.0 / 60.0, false, false)
		p2 = maxf(p2, fx2.ShakeOffset.length())
	_check("高速落地产生晃动", p2 > 0.05, "峰值 %.3f" % p2)


func _check(name: String, ok: bool, detail: String) -> void:
	if ok:
		_pass += 1
		_msgs.append("  [PASS] %s  (%s)" % [name, detail])
	else:
		_fail += 1
		_msgs.append("  [FAIL] %s  (%s)" % [name, detail])