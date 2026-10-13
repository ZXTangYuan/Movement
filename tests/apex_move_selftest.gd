extends Node
## Apex 移动核心自检（无头，无需物理空间）。
## 用法：godot --headless --path . res://tests/apex_move_selftest.tscn
##
## 覆盖：确定性 / 地面加速 / 摩擦停止 / 跳跃高度 / 双跳 / 空中控制 /
##       滑铲门槛与加速 / 蹬墙跑门槛 / 序列化往返。

const DT := 1.0 / 60.0

## 输入按钮位（与 C# ApexMoveInput.BTN_* 一致；C# const 不暴露给 GDScript）。
const BTN_JUMP := 1 << 0
const BTN_CROUCH := 1 << 1
const BTN_SPRINT := 1 << 2

var _core: ApexMovementCore
var _fails := 0


func _ready() -> void:
	_core = ApexMovementCore.new()
	_t_determinism()
	_t_ground_accel()
	_t_friction_stop()
	_t_jump_height()
	_t_double_jump()
	_t_air_control_momentum()
	_t_slide_requires_speed()
	_t_slide_boosts()
	_t_wallrun_requires_wall()
	_t_serialize_roundtrip()
	print("=== APEX MOVE SELFTEST %s (%d failures) ===" % ["PASS" if _fails == 0 else "FAIL", _fails])
	get_tree().quit(_fails)


# ───────────────────────── 工具 ─────────────────────────

func _cfg() -> ApexMoveConfig:
	return ApexMoveConfig.new()

func _state(pos: Vector3 = Vector3.ZERO) -> ApexMoveState:
	var s := ApexMoveState.new()
	s.Position = pos
	return s

func _mk_input(dir: Vector3 = Vector3.ZERO, jump: bool = false) -> ApexMoveInput:
	var i := ApexMoveInput.new()
	i.WishDir = dir
	i.JumpPressed = jump
	return i

func _env(grounded: bool = true, wall: bool = false, wall_normal: Vector3 = Vector3.ZERO) -> ApexMoveEnv:
	var e := ApexMoveEnv.new()
	e.Up = Vector3.UP
	e.GravityMagnitude = 9.8
	e.Grounded = grounded
	e.GroundNormal = Vector3.UP
	e.HasWall = wall
	e.WallNormal = wall_normal
	e.WallIsRunable = wall
	e.WallDistance = 0.5 if wall else 999.0
	return e

func _run(s: ApexMoveState, i: ApexMoveInput, e: ApexMoveEnv, cfg: ApexMoveConfig, steps: int) -> void:
	for n in steps:
		_core.Step(s, i, e, cfg, DT)

func _check(name: String, ok: bool, detail: String = "") -> void:
	print("  [%s] %s %s" % ["OK" if ok else "FAIL", name, detail])
	if not ok:
		_fails += 1


# ───────────────────────── 用例 ─────────────────────────

func _t_determinism() -> void:
	print("-- 确定性 --")
	var cfg := _cfg()
	var a := _state(); var b := _state()
	var e := _env()
	var i := _mk_input(Vector3(1, 0, 0))
	for n in 60:
		_core.Step(a, i, e, cfg, DT)
		_core.Step(b, i, e, cfg, DT)
	_check("同输入同状态", a.Position.distance_to(b.Position) < 1e-6,
		"a=%s b=%s" % [a.Position, b.Position])


func _t_ground_accel() -> void:
	print("-- 地面加速 --")
	var cfg := _cfg()
	var s := _state()
	var e := _env()
	var i := _mk_input(Vector3(1, 0, 0))
	_run(s, i, e, cfg, 120)
	var sp: float = s.Velocity.length()
	_check("达到并接近 MaxSpeed", sp > cfg.MaxSpeed * 0.9 and sp <= cfg.MaxSpeed * 1.05,
		"speed=%.2f max=%.2f" % [sp, cfg.MaxSpeed])


func _t_friction_stop() -> void:
	print("-- 摩擦停止 --")
	var cfg := _cfg()
	var s := _state()
	s.Velocity = Vector3(cfg.MaxSpeed, 0, 0)
	var e := _env()
	var i := _mk_input(Vector3.ZERO)
	_run(s, i, e, cfg, 180)
	var sp: float = s.Velocity.length()
	_check("无输入时停下", sp < 0.1, "speed=%.3f" % sp)


func _t_jump_height() -> void:
	print("-- 跳跃高度 --")
	var cfg := _cfg()
	var s := _state()
	var e := _env()
	_core.Step(s, _mk_input(Vector3.ZERO, false), e, cfg, DT)   # 落地稳定
	var e_air := _env(false)
	_core.Step(s, _mk_input(Vector3.ZERO, true), e_air, cfg, DT)
	var start_y: float = s.Position.y
	var peak: float = start_y
	var i_no := _mk_input(Vector3.ZERO, false)
	for n in 120:
		_core.Step(s, i_no, e_air, cfg, DT)
		peak = maxf(peak, s.Position.y)
	var h: float = peak - start_y
	_check("跳跃高度接近配置", absf(h - cfg.JumpHeight) < 0.25,
		"h=%.2f 期望=%.2f" % [h, cfg.JumpHeight])


func _t_double_jump() -> void:
	print("-- 双跳 --")
	var cfg := _cfg()
	var s := _state()
	var e_air := _env(false)
	_core.Step(s, _mk_input(Vector3.ZERO, true), e_air, cfg, DT)
	var v1: float = s.Velocity.y
	_core.Step(s, _mk_input(Vector3.ZERO, true), e_air, cfg, DT)
	var v2: float = s.Velocity.y
	_core.Step(s, _mk_input(Vector3.ZERO, true), e_air, cfg, DT)
	var v3: float = s.Velocity.y
	_check("双跳生效", v2 > 0.5 and s.AirJumpsUsed >= 1, "v1=%.2f v2=%.2f" % [v1, v2])
	_check("不会无限跳", v3 < v2 + 0.01, "v3=%.2f" % v3)


func _t_air_control_momentum() -> void:
	print("-- 空中控制 --")
	var cfg := _cfg()
	var s := _state()
	s.Velocity = Vector3(cfg.MaxSpeed, 0, 0)
	var e_air := _env(false)
	var i := _mk_input(Vector3(1, 0, 1).normalized())
	var before: Vector3 = s.Velocity.normalized()
	_run(s, i, e_air, cfg, 30)
	var after: Vector3 = s.Velocity.normalized()
	var turned: float = rad_to_deg(before.angle_to(after))
	_check("空中可改变方向", turned > 3.0, "转向=%.1f°" % turned)
	var sp: float = s.Velocity.length()
	_check("空中不丢失速度", sp > cfg.MaxSpeed * 0.8, "speed=%.2f" % sp)


func _t_slide_requires_speed() -> void:
	print("-- 滑铲门槛 --")
	var cfg := _cfg()
	var slow := _state()
	slow.Velocity = Vector3(cfg.SlideRequiredStartSpeed * 0.5, 0, 0)
	var e := _env()
	var i := ApexMoveInput.new()
	i.Buttons = BTN_CROUCH
	_core.Step(slow, i, e, cfg, DT)
	_check("低速不能起滑", not slow.Sliding, "speed=%.2f" % slow.Velocity.length())
	var fast := _state()
	fast.Velocity = Vector3(cfg.SlideRequiredStartSpeed * 1.5, 0, 0)
	_core.Step(fast, i, e, cfg, DT)
	_check("高速可起滑", fast.Sliding, "")


func _t_slide_boosts() -> void:
	print("-- 滑铲加速 --")
	var cfg := _cfg()
	var s := _state()
	var v0: float = cfg.SlideRequiredStartSpeed + 1.0
	s.Velocity = Vector3(v0, 0, 0)
	var e := _env()
	var i := ApexMoveInput.new()
	i.Buttons = BTN_CROUCH
	var peak: float = v0
	for n in 20:
		_core.Step(s, i, e, cfg, DT)
		peak = maxf(peak, s.Velocity.length())
	_check("起滑获得速度加成", peak > v0 + 0.3, "v0=%.2f peak=%.2f" % [v0, peak])


func _t_wallrun_requires_wall() -> void:
	print("-- 蹬墙跑门槛 --")
	var cfg := _cfg()
	var s1 := _state()
	s1.Velocity = Vector3(0, 0, cfg.WallrunMinSpeed + 2.0)
	_core.Step(s1, _mk_input(Vector3(0, 0, 1)), _env(false, false), cfg, DT)
	_check("无墙不墙跑", not s1.Wallrunning, "")
	var s2 := _state()
	s2.Velocity = Vector3(0, 0, cfg.WallrunMinSpeed + 3.0)
	var wall_n := Vector3(1, 0, 0)
	_core.Step(s2, _mk_input(Vector3(0, 0, 1)), _env(false, true, wall_n), cfg, DT)
	_check("有墙且够快 → 墙跑", s2.Wallrunning, "")


func _t_serialize_roundtrip() -> void:
	print("-- 序列化往返 --")
	var a := _state(Vector3(1, 2, 3))
	a.Velocity = Vector3(4, 5, 6)
	a.Sliding = true
	a.SlideTime = 0.42
	a.OnGround = true
	a.CrouchFraction = 0.7
	var data := a.GetState()
	var b := ApexMoveState.new()
	b.SetState(data)
	var ok: bool = b.Position.distance_to(a.Position) < 1e-6 \
		and b.Velocity.distance_to(a.Velocity) < 1e-6 \
		and b.Sliding == a.Sliding and absf(b.SlideTime - a.SlideTime) < 1e-6 \
		and b.OnGround == a.OnGround and absf(b.CrouchFraction - a.CrouchFraction) < 1e-6
	_check("状态往返一致", ok, "")
