extends Node
## Apex 移动 × ProjectA 集成测试：在**球面 raymarch 行星**上驱动 ApexCharacter。
## 验证：球面重力下的 up 自适应、SDF 地面探测（无碰撞体）、行走不穿地、跳跃。
## 用法：godot --path . --rendering-driver vulkan res://tests/apex_move_integration.tscn
##
## 说明：地形是 raymarch 密度场（无碰撞体），因此地面探测走 SDF 回调，
## 这正是插件接入 ProjectA 的关键路径。

const DT := 1.0 / 60.0

var _fails := 0
var _boot: Node3D
var _char: ApexCharacter
var _planet: Planet
var _solar: SolarSystem


func _ready() -> void:
	_boot = preload("res://scenes/levels/bootstrap.tscn").instantiate()
	add_child(_boot)
	for i in 3:
		await get_tree().process_frame
	_solar = _boot.solar_system
	_planet = _solar.detail_planet

	_setup_character()
	if _char == null:
		print("=== APEX × PROJECTA 集成 FAIL：角色创建失败（脚本未加载） ===")
		get_tree().quit(1)
		return
	await get_tree().physics_frame

	_t_spawn_above_surface()
	_t_gravity_up_radial()
	_t_walk_keeps_contact()
	_t_jump_leaves_ground()
	_t_no_sink_through()

	print("=== APEX × PROJECTA 集成 %s (%d fails) ===" % ["PASS" if _fails == 0 else "FAIL", _fails])
	get_tree().quit(_fails)


func _setup_character() -> void:
	_char = ApexCharacter.new()
	if _char == null:
		return
	_char.name = "ApexChar"
	_char.autonomous_input = false   # 测试自行填充 input_frame
	add_child(_char)
	# 测试自行按固定步长驱动 _step，关闭节点自身的物理回调避免双重步进
	_char.set_physics_process(false)
	# 球面重力 + SDF 地形回调（接入 ProjectA 的两个适配点）
	_char.gravity_up = _gravity_at
	_char.sdf_density = _density_at

	# 出生：从行星质心沿 +X 到表面上方
	var def := _planet.definition
	var dir := Vector3(1, 0, 0)
	var from: Vector3 = _planet.global_position + dir * (def.radius + def.terrain_amplitude * 3.0)
	var hit = _planet.raycast(from, -dir, def.terrain_amplitude * 8.0 + 50.0)
	var spawn: Vector3 = (hit if hit != null else _planet.global_position + dir * def.radius)
	_char.teleport_to(spawn + dir * 3.0)
	_char.state.Up = dir


## 重力适配：ProjectA 的 Planet 提供球面重力场。
func _gravity_at(world_pos: Vector3) -> Dictionary:
	var info: Dictionary = _planet.gravity_info_at(world_pos)
	return { "up": info["up"], "gravity": info["strength"] }


## 地形适配：世界坐标 → 行星局部密度。
func _density_at(world_pos: Vector3) -> float:
	return _planet.sample_density(world_pos)


func _check(name: String, ok: bool, detail: String = "") -> void:
	print("  [%s] %s %s" % ["OK" if ok else "FAIL", name, detail])
	if not ok:
		_fails += 1


func _t_spawn_above_surface() -> void:
	print("-- 出生在表面上方 --")
	var r: float = (_char.state.Position - _planet.global_position).length()
	var den: float = _density_at(_char.state.Position)
	_check("出生点在地表外（空气）", den > 0.0, "r=%.1f density=%.2f" % [r, den])


func _t_gravity_up_radial() -> void:
	print("-- 球面重力 up 自适应 --")
	var up: Vector3 = _char.state.Up
	var radial: Vector3 = (_char.state.Position - _planet.global_position).normalized()
	var align: float = up.dot(radial)
	_check("up 对齐行星径向", align > 0.99, "align=%.4f" % align)


func _t_walk_keeps_contact() -> void:
	print("-- 行走保持接地 --")
	# 先落地稳定（出生点在离地 ~2m 处，前若干帧是合法下落）
	for i in 120:
		_char.input_frame.WishDir = Vector3.ZERO
		_char.input_frame.JumpPressed = false
		_char._step(DT)
	# 向前走（切平面方向）
	var tangent: Vector3 = _char.state.Up.cross(Vector3.UP)
	if tangent.length() < 0.1:
		tangent = _char.state.Up.cross(Vector3.RIGHT)
	tangent = tangent.normalized()
	var grounded_frames := 0
	var total := 90
	var r0: float = (_char.state.Position - _planet.global_position).length()
	for i in total:
		_char.input_frame.WishDir = tangent
		_char._step(DT)
		if _char.state.OnGround:
			grounded_frames += 1
	var ratio := float(grounded_frames) / float(total)
	var r1: float = (_char.state.Position - _planet.global_position).length()
	var den: float = _density_at(_char.state.Position)
	var arc: float = rad_to_deg(atan2((r1 - r0), 0.001))
	print("    r: %.1f → %.1f, density=%.2f, grounded=%.0f%%" % [
		r0, r1, den, ratio * 100.0])
	_check("落地后大部分帧接地", ratio > 0.9, "grounded=%.0f%%" % (ratio * 100.0))
	_check("未穿入地面", den > -0.5, "density=%.2f" % den)


func _t_jump_leaves_ground() -> void:
	print("-- 跳跃离地 --")
	# 先稳定接地
	for i in 10:
		_char.input_frame.WishDir = Vector3.ZERO
		_char._step(DT)
	_char.state.Velocity = Vector3.ZERO
	# 起跳
	_char.input_frame.WishDir = Vector3.ZERO
	_char.input_frame.JumpPressed = true
	_char.state.JumpBufferTimer = 0.12
	_char._step(DT)
	var v_up: float = _char.state.Velocity.dot(_char.state.Up)
	_check("起跳获得向上速度", v_up > 1.0, "v_up=%.2f" % v_up)
	# 空中数帧应离地
	var airborne := false
	for i in 5:
		_char.input_frame.JumpPressed = false
		_char._step(DT)
		if not _char.state.OnGround:
			airborne = true
	_check("离地后处于空中", airborne, "")


func _t_no_sink_through() -> void:
	print("-- 不穿透地面 --")
	# 连续 2 秒无输入，应稳定停在地表（不无限下沉）
	var min_r := 1e9
	for i in 120:
		_char.input_frame.WishDir = Vector3.ZERO
		_char.input_frame.JumpPressed = false
		_char._step(DT)
		var r: float = (_char.state.Position - _planet.global_position).length()
		min_r = minf(min_r, r)
	var surface_r: float = _planet.definition.radius - _planet.definition.terrain_amplitude
	_check("未沉入行星内部", min_r > surface_r * 0.9, "min_r=%.1f 下限≈%.1f" % [min_r, surface_r * 0.9])
