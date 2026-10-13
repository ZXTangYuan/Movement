extends Node3D
## Apex 移动 × 独立测试关卡 的**自动化验证**（无头可跑）。
##
## 用法：godot --headless --path . res://tests/apex_test_level/apex_level_test.tscn
##
## 在真实碰撞体关卡上驱动 ApexCharacter，验证：
##   平地接地 / 上坡 / 下坡 / 台阶 / 障碍翻越(mantle) / 高墙攀爬(climb) /
##   地洞跌落 / 蹬墙跑 / 滑铲。
## 这是「攀爬 + 翻越」的验收测试 —— 断言角色确实**上到了障碍顶部**。

const LEVEL := preload("res://tests/apex_test_level/test_level.gd")
const DT := 1.0 / 60.0

var _level: Node3D
var _char: ApexCharacter
var _fails := 0


func _ready() -> void:
	_level = LEVEL.new()
	_level.name = "TestLevel"
	add_child(_level)
	await get_tree().physics_frame
	await get_tree().physics_frame

	_char = ApexCharacter.new()
	_char.name = "Char"
	_char.autonomous_input = false
	_char.set_physics_process(false)
	add_child(_char)
	await get_tree().physics_frame

	_t_flat_ground()
	_t_ramp_up()
	_t_ramp_down()
	_t_steps()
	_t_mantle_low()
	_t_climb_high()
	_t_pit_fall()
	_t_wallrun()
	_t_slide_ramp()

	print("=== APEX LEVEL TEST %s (%d fails) ===" % ["PASS" if _fails == 0 else "FAIL", _fails])
	get_tree().quit(_fails)


# ───────────────────────── 工具 ─────────────────────────

func _check(name: String, ok: bool, detail: String = "") -> void:
	print("  [%s] %s %s" % ["OK" if ok else "FAIL", name, detail])
	if not ok:
		_fails += 1


## 放到指定位置，落地稳定若干帧。
func _spawn(pos: Vector3, face: Vector3) -> void:
	_char.teleport_to(pos)
	_char.state.Up = Vector3.UP
	_char.state.OnGround = false
	_char.state.Mantling = false
	_char.state.Sliding = false
	_char.state.Wallrunning = false
	_char.state.JumpLockTimer = 0.0
	_char.yaw = atan2(-face.x, -face.z)
	_char.pitch = 0.0
	_char.input_frame.WishDir = Vector3.ZERO
	_char.input_frame.JumpPressed = false
	_char.input_frame.Buttons = 0
	for i in 30:
		_char._step(DT)


## 朝 dir 走 frames 帧；返回是否观察到某个状态回调。
func _walk(dir: Vector3, frames: int, watch: Callable = Callable()) -> bool:
	var saw := false
	var d := dir.normalized()
	for i in frames:
		_char.input_frame.WishDir = d
		_char.input_frame.JumpPressed = false
		_char._step(DT)
		if watch.is_valid() and watch.call():
			saw = true
	return saw


# ───────────────────────── 用例 ─────────────────────────

func _t_flat_ground() -> void:
	print("-- 平地 --")
	_spawn(Vector3(0, 1.5, 0), Vector3(0, 0, -1))
	_walk(Vector3(1, 0, 0), 60)
	_check("平地接地", _char.state.OnGround, "y=%.2f" % _char.state.Position.y)
	_check("平地未下沉", _char.state.Position.y > -0.5, "y=%.2f" % _char.state.Position.y)
	_check("平地可加速", _char.state.Velocity.length() > 3.0,
		"speed=%.2f" % _char.state.Velocity.length())


func _t_ramp_up() -> void:
	print("-- 上坡 --")
	var f: Dictionary = _level.features["ramp_up"]
	var bot: Vector3 = f["bottom"]
	_spawn(bot + Vector3(0, 1.5, 2), Vector3(0, 0, -1))
	var y0: float = _char.state.Position.y
	_walk(Vector3(0, 0, -1), 200)
	var y1: float = _char.state.Position.y
	print("    y: %.2f → %.2f" % [y0, y1])
	_check("走上坡（高度增加）", y1 > y0 + 1.0, "y %.2f → %.2f" % [y0, y1])
	_check("上坡保持接地", _char.state.OnGround, "")


func _t_ramp_down() -> void:
	print("-- 下坡 --")
	var f: Dictionary = _level.features["ramp_down"]
	var top: Vector3 = f["top"]
	_spawn(top + Vector3(0, 1.5, -2), Vector3(0, 0, 1))
	var y0: float = _char.state.Position.y
	_walk(Vector3(0, 0, 1), 200)
	var y1: float = _char.state.Position.y
	print("    y: %.2f → %.2f" % [y0, y1])
	_check("走下坡（高度下降）", y1 < y0 - 1.0, "y %.2f → %.2f" % [y0, y1])
	_check("下坡未穿透地面", y1 > -2.0, "y=%.2f" % y1)


func _t_steps() -> void:
	print("-- 台阶 --")
	var f: Dictionary = _level.features["steps"]
	var base: Vector3 = f["base"]
	_spawn(base + Vector3(-3.0, 1.5, 0), Vector3(1, 0, 0))
	var y0: float = _char.state.Position.y
	var peak := y0
	for i in 160:
		_char.input_frame.WishDir = Vector3(1, 0, 0)
		_char.input_frame.JumpPressed = false
		_char._step(DT)
		peak = maxf(peak, _char.state.Position.y)
		if i % 40 == 0:
			print("    f%03d x=%.2f y=%.2f" % [i, _char.state.Position.x, _char.state.Position.y])
	print("    y: %.2f → peak %.2f, x=%.2f" % [y0, peak, _char.state.Position.x])
	_check("台阶可上（升高）", peak > y0 + 0.1, "peak=%.2f" % peak)


## 走向障碍，翻越完成后立即停止并断言高度。
func _walk_until_mantle_done(dir: Vector3, max_frames: int, want_climb: bool) -> Dictionary:
	var d := dir.normalized()
	var started := false
	var saw := false
	var top_y := -1e9
	for i in max_frames:
		_char.input_frame.WishDir = d
		_char.input_frame.JumpPressed = false
		_char._step(DT)
		if _char.state.Mantling:
			started = true
			if _char.state.MantleIsClimb == want_climb:
				saw = true
		elif started:
			# 翻越刚结束：记录稳定后的高度（再稳 5 帧）
			for k in 5:
				_char.input_frame.WishDir = d
				_char._step(DT)
			top_y = _char.state.Position.y
			break
	return { "saw": saw, "top_y": top_y }


func _t_mantle_low() -> void:
	print("-- 障碍翻越 (1.2m) --")
	var f: Dictionary = _level.features["mantle_low"]
	_spawn(f["spawn"], Vector3(0, 0, -1))
	var y0: float = _char.state.Position.y
	var r := _walk_until_mantle_done(Vector3(0, 0, -1), 200, false)
	print("    y: %.2f → %.2f, 触发=%s" % [y0, r["top_y"], r["saw"]])
	_check("触发翻越", r["saw"], "")
	_check("翻越后站上顶部", r["top_y"] > y0 + 0.7, "top_y=%.2f" % r["top_y"])


func _t_climb_high() -> void:
	print("-- 高墙攀爬 (2.6m) --")
	var f: Dictionary = _level.features["climb_high"]
	_spawn(f["spawn"], Vector3(0, 0, -1))
	var y0: float = _char.state.Position.y
	var r := _walk_until_mantle_done(Vector3(0, 0, -1), 260, true)
	print("    y: %.2f → %.2f, 触发攀爬=%s" % [y0, r["top_y"], r["saw"]])
	_check("触发攀爬", r["saw"], "")
	_check("攀爬后到达高处", r["top_y"] > y0 + 1.5, "top_y=%.2f" % r["top_y"])


func _t_pit_fall() -> void:
	print("-- 地洞跌落 --")
	var f: Dictionary = _level.features["pit"]
	var spawn: Vector3 = f["spawn"]
	_spawn(spawn, Vector3(0, 0, -1))
	var y0: float = _char.state.Position.y
	_walk(Vector3.ZERO, 150)
	var y1: float = _char.state.Position.y
	print("    y: %.2f → %.2f" % [y0, y1])
	_check("跌入地洞", y1 < y0 - 2.0, "y %.2f → %.2f" % [y0, y1])
	_check("停在地洞底部（未穿透）", y1 > -8.0, "y=%.2f" % y1)


func _t_wallrun() -> void:
	print("-- 蹬墙跑 --")
	var f: Dictionary = _level.features["wallrun"]
	var spawn: Vector3 = f["spawn"]
	_spawn(spawn, Vector3(0, 0, -1))
	var saw := false
	for i in 240:
		_char.input_frame.WishDir = Vector3(0, 0, -1)
		_char.input_frame.JumpPressed = (i == 0)
		_char._step(DT)
		if _char.state.Wallrunning:
			saw = true
	print("    触发=%s, y=%.2f" % [saw, _char.state.Position.y])
	_check("触发蹬墙跑", saw, "")


func _t_slide_ramp() -> void:
	print("-- 滑铲 (下坡) --")
	var f: Dictionary = _level.features["slide_ramp"]
	var spawn: Vector3 = f["spawn"]
	_spawn(spawn, Vector3(0, 0, 1))
	# 先冲刺加速
	for i in 50:
		_char.input_frame.WishDir = Vector3(0, 0, 1)
		_char.input_frame.Buttons = 1 << 2   # BTN_SPRINT
		_char._step(DT)
	# 按住蹲 → 滑铲
	var saw := false
	var peak_speed := 0.0
	for i in 120:
		_char.input_frame.WishDir = Vector3(0, 0, 1)
		_char.input_frame.Buttons = 1 << 1   # BTN_CROUCH
		_char._step(DT)
		if _char.state.Sliding:
			saw = true
			peak_speed = maxf(peak_speed, _char.state.Velocity.length())
	print("    触发滑铲=%s, 峰值速度=%.2f" % [saw, peak_speed])
	_check("下坡触发滑铲", saw, "")
	_check("滑铲维持速度（不立即停）", peak_speed > 3.0, "peak=%.2f" % peak_speed)
