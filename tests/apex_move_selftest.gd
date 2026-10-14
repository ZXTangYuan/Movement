extends Node
class_name ApexMoveSelfTest

## ApexMovement 无头自检。
##
## 验证本插件相对 Apex 原版**最核心的承诺**：帧率公平性。
## 以及核心求解器的基本正确性（确定性、重力、跳跃高度、摩擦、滑铲）。
##
## 运行：
##   "<godot>" --headless --path . res://tests/apex_move_selftest.tscn
## 期望输出：APEX MOVE SELFTEST PASS

const ClockScript := preload("res://addons/apex_movement/core/ApexTickClock.cs")
const CoreScript := preload("res://addons/apex_movement/core/ApexMovementCore.cs")
const ConfigScript := preload("res://addons/apex_movement/core/ApexMoveConfig.cs")
const StateScript := preload("res://addons/apex_movement/core/ApexMoveState.cs")
const InputScript := preload("res://addons/apex_movement/core/ApexMoveInput.cs")
const EnvScript := preload("res://addons/apex_movement/core/ApexMoveEnv.cs")

const BTN_JUMP := 0
const BTN_CROUCH := 1
const BTN_SPRINT := 2

var _pass := 0
var _fail := 0
var _msgs: Array[String] = []


func _ready() -> void:
	print("=== ApexMovement 自检 ===")
	_test_determinism()
	_test_framerate_independence()
	_test_zero_fps_no_momentum()
	_test_gravity_landing()
	_test_jump_height()
	_test_friction_stop()
	_test_slide_boost()
	_test_mantle_trigger()

	print("")
	for m in _msgs:
		print(m)
	print("")
	if _fail == 0:
		print("APEX MOVE SELFTEST PASS  (%d 项)" % _pass)
	else:
		print("APEX MOVE SELFTEST FAIL  (通过 %d，失败 %d)" % [_pass, _fail])
	get_tree().quit(0 if _fail == 0 else 1)


# ───────────────────────── 用例 ─────────────────────────

## 1. 确定性：同输入同结果
func _test_determinism() -> void:
	var a := _run_scripted(1.0 / 60.0)
	var b := _run_scripted(1.0 / 60.0)
	var d: float = a.distance_to(b)
	_check("确定性（同输入同结果）", d < 1e-5, "偏差 %.9f m" % d)


## 2. 帧率无关：60fps 与 15fps 用同一输入序列应产生近似轨迹
func _test_framerate_independence() -> void:
	# 60fps：每帧 1 个 tick
	var hi := _run_scripted(1.0 / 60.0)
	# 15fps：每帧 4 个 tick（走同一总时长）
	var lo := _run_scripted(1.0 / 15.0)
	var d: float = hi.distance_to(lo)
	# 两者 tick 数相同（都是整数步），应几乎完全一致
	_check("帧率无关（60fps vs 15fps）", d < 0.05, "偏差 %.6f m" % d)


## 3. 0 fps：不推进 → 不移动、不累计动量
func _test_zero_fps_no_momentum() -> void:
	var clock = ClockScript.new()
	clock.TickHz = 60.0
	var cfg = ConfigScript.new()
	var core = CoreScript.new()
	var s = StateScript.new()
	var inp = InputScript.new()
	var env = EnvScript.new()

	s.Reset(Vector3(0, 10, 0), Vector3.UP)
	s.Velocity = Vector3(5, 0, 0)      # 给一个初速度（模拟「被外力推动」）

	# 模拟 2 秒 0 fps（无帧）
	var steps_total := 0
	for i in 120:
		var n: int = clock.Advance(0.0)   # 帧时间为 0
		steps_total += n

	var moved: float = s.Position.distance_to(Vector3(0, 10, 0))
	_check("0 fps 不推进（tick=0 且未移动）", steps_total == 0 and moved < 1e-6,
		"tick=%d 位移=%.6f" % [steps_total, moved])

	# 恢复：一帧给了 2 秒的时间 → 应被有界追赶截断并计数，而不是一次性灌入
	var n2: int = clock.Advance(2.0)
	_check("恢复时有界追赶（不超上限）",
		n2 <= clock.MaxSubstepsPerFrame and clock.DroppedTicks > 0,
		"本帧 tick=%d 上限=%d 丢弃=%d" % [n2, clock.MaxSubstepsPerFrame, clock.DroppedTicks])


## 4. 重力落地
func _test_gravity_landing() -> void:
	var cfg = ConfigScript.new()
	var core = CoreScript.new()
	var s = StateScript.new()
	var inp = InputScript.new()
	var env = EnvScript.new()
	var dt := 1.0 / 60.0

	s.Reset(Vector3(0, 5, 0), Vector3.UP)
	var floor_y := 0.9
	for i in 600:
		env.Clear()
		env.GravityMagnitude = cfg.Gravity
		env.Grounded = s.Position.y <= floor_y + 0.05
		if env.Grounded:
			env.GroundSnap = Vector3(0, floor_y - s.Position.y, 0)
			s.Position.y = floor_y
			s.Velocity.y = maxf(s.Velocity.y, 0.0)
		core.Step(s, inp, env, cfg, dt)
	_check("重力落地并停住", s.Position.y <= floor_y + 0.2 and absf(s.Velocity.y) < 1.0,
		"y=%.3f vy=%.3f" % [s.Position.y, s.Velocity.y])


## 5. 跳跃高度符合配置（v = sqrt(2gh)）
func _test_jump_height() -> void:
	var cfg = ConfigScript.new()
	var core = CoreScript.new()
	var s = StateScript.new()
	var inp = InputScript.new()
	var env = EnvScript.new()
	var dt := 1.0 / 60.0

	s.Reset(Vector3(0, 0, 0), Vector3.UP)
	env.Clear()
	env.Grounded = true
	env.GroundSnap = Vector3.ZERO
	inp.SetHeld(BTN_JUMP, true)
	core.Step(s, inp, env, cfg, dt)      # 起跳这一 tick
	inp.SetHeld(BTN_JUMP, false)

	var peak := 0.0
	for i in 200:
		env.Clear()
		env.Grounded = false
		core.Step(s, inp, env, cfg, dt)
		peak = maxf(peak, s.Position.y)
	var expect: float = cfg.JumpHeight
	var err: float = absf(peak - expect)
	_check("跳跃高度 ≈ 配置值", err < expect * 0.15,
		"实测 %.3f m / 配置 %.3f m（差 %.1f%%）" % [peak, expect, err / expect * 100.0])


## 6. 摩擦停车
func _test_friction_stop() -> void:
	var cfg = ConfigScript.new()
	var core = CoreScript.new()
	var s = StateScript.new()
	var inp = InputScript.new()
	var env = EnvScript.new()
	var dt := 1.0 / 60.0

	s.Reset(Vector3.ZERO, Vector3.UP)
	s.Velocity = Vector3(cfg.WalkSpeed, 0, 0)
	for i in 300:
		env.Clear()
		env.Grounded = true
		env.GroundNormal = Vector3.UP
		core.Step(s, inp, env, cfg, dt)
	_check("无输入时摩擦停车", s.HorizontalSpeed < 0.5,
		"残余速度 %.4f m/s" % s.HorizontalSpeed)


## 7. 滑铲一次性加速
func _test_slide_boost() -> void:
	var cfg = ConfigScript.new()
	var core = CoreScript.new()
	var s = StateScript.new()
	var inp = InputScript.new()
	var env = EnvScript.new()
	var dt := 1.0 / 60.0

	s.Reset(Vector3.ZERO, Vector3.UP)
	var start: float = cfg.SlideStartSpeed + 1.0
	s.Velocity = Vector3(start, 0, 0)
	inp.SetHeld(BTN_CROUCH, true)

	var peak := start
	for i in 60:
		env.Clear()
		env.Grounded = true
		env.GroundNormal = Vector3.UP
		core.Step(s, inp, env, cfg, dt)
		peak = maxf(peak, s.HorizontalSpeed)
	_check("滑铲起手获得加速", peak > start + 0.5 and peak <= cfg.SlideBoostCap + 0.1,
		"%.2f → 峰值 %.2f（cap %.2f）" % [start, peak, cfg.SlideBoostCap])


## 8. 翻越触发（给一个 1.2m 的障碍）
func _test_mantle_trigger() -> void:
	var cfg = ConfigScript.new()
	var core = CoreScript.new()
	var s = StateScript.new()
	var inp = InputScript.new()
	var env = EnvScript.new()
	var dt := 1.0 / 60.0

	s.Reset(Vector3(0, 0.9, 0), Vector3.UP)
	s.Velocity = Vector3(0, 0, -3)
	env.Clear()
	env.Grounded = true
	env.ObstacleAhead = true
	env.ObstacleHeight = 1.2
	env.MantleTargetPoint = Vector3(0, 2.1, -1.5)
	core.Step(s, inp, env, cfg, dt)
	_check("翻越被触发", s.Mantling, "Mantling=%s" % str(s.Mantling))


# ───────────────────────── 脚本化轨迹（确定性/帧率用）─────────────────────────

## 用固定输入序列跑一段，返回终点位置。
## frame_dt 是「渲染帧」时长；内部按固定 tick 推进。
func _run_scripted(frame_dt: float) -> Vector3:
	var clock = ClockScript.new()
	clock.TickHz = 60.0
	clock.MaxSubstepsPerFrame = 64        # 放宽上限，便于比较帧率差异
	var cfg = ConfigScript.new()
	var core = CoreScript.new()
	var s = StateScript.new()
	var inp = InputScript.new()
	var env = EnvScript.new()

	s.Reset(Vector3(0, 0.9, 0), Vector3.UP)

	# 输入序列：前 60 tick 前进，然后起跳，再持续前进
	var tick := 0
	var frames := int(3.0 / frame_dt)
	for f in frames:
		var n: int = clock.Advance(frame_dt)
		for i in n:
			tick += 1
			inp.WishDir = Vector3(0, 0, -1)
			inp.WishStrength = 1.0
			if tick == 60:
				inp.SetHeld(BTN_JUMP, true)
			elif tick == 61:
				inp.SetHeld(BTN_JUMP, false)
			env.Clear()
			env.Grounded = s.Position.y <= 0.95
			env.GroundSnap = Vector3(0, maxf(0.0, 0.9 - s.Position.y), 0)
			core.Step(s, inp, env, cfg, clock.FixedDelta)
	return s.Position


# ───────────────────────── 断言 ─────────────────────────

func _check(name: String, ok: bool, detail: String) -> void:
	if ok:
		_pass += 1
		_msgs.append("  [PASS] %s  (%s)" % [name, detail])
	else:
		_fail += 1
		_msgs.append("  [FAIL] %s  (%s)" % [name, detail])
