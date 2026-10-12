class_name FirstPersonPlayer
extends Node3D
## 第一人称玩家控制器（多行星太阳系 + SDF 体素地形）。
##
## 视角规则（两套模式，按是否处于重力场平滑切换）：
##   FREE    —— 宇宙中：完整三维自由旋转，无任何限制（四元数累加，无万向节死锁）
##   SURFACE —— 进入星球重力场：以**该星球局部重力方向**为参考，
##              pitch 限制在 [-90°, +90°]（最低看向星球中心，最高抬头 90°）
##
## 重力：
##   每颗星球都有独立重力场（球面，指向各自质心）。
##   进入重力场后，重力方向与角色姿态**平滑插值**修正（不会瞬间拧麻花）。
##
## 坐标：本节点在**世界空间**（不随星球公转漂移——由 _bound_planet 补偿）。

enum ViewMode { FREE, SURFACE }

@export var move_speed: float = 12.0
@export var sprint_multiplier: float = 3.0
@export var fly_speed: float = 30.0
@export var space_speed: float = 600.0
@export var mouse_sensitivity: float = 0.0025
@export var capsule_height: float = 1.8
@export var dig_radius: float = 2.5
@export var start_flying: bool = false

## 姿态对齐速度（越大越快贴合重力方向）。0.8 ≈ 约 1.5 秒完成过渡。
@export var gravity_align_speed: float = 0.8
## 重力场切换的混合速度。
@export var field_blend_speed: float = 1.2

var solar_system: SolarSystem
var camera: Camera3D
var flying: bool = false

var view_mode: ViewMode = ViewMode.FREE
## 当前绑定的星球（处于其重力场内）。
var bound_planet: Planet = null

var _velocity: Vector3 = Vector3.ZERO
## 平滑后的“上”方向（重力反方向）。所有姿态计算都基于它，**绝不用世界坐标硬编码**。
var _up: Vector3 = Vector3.UP
var _up_target: Vector3 = Vector3.UP
## 当前重力强度（由所在重力场给出）。
var _gravity_strength: float = 0.0

## SURFACE 模式的 yaw/pitch（相对 _up 参考系）。
var _yaw: float = 0.0
var _pitch: float = 0.0
## FREE 模式的四元数累加器（真正无限制 3D 旋转）。
var _free_rot: Quaternion = Quaternion.IDENTITY
## 0 = 完全 FREE，1 = 完全 SURFACE。平滑过渡。
var _surface_weight: float = 0.0
var _mouse_captured: bool = true
## 上一帧星球位置（用于让玩家跟随公转）。
var _bound_prev_pos: Vector3 = Vector3.ZERO


func _ready() -> void:
	flying = start_flying
	camera = Camera3D.new()
	camera.name = "Camera3D"
	camera.position = Vector3(0, 1.6, 0)
	camera.fov = 75.0
	camera.far = 60000.0
	add_child(camera)
	camera.current = true
	Input.mouse_mode = Input.MOUSE_MODE_CAPTURED
	_free_rot = Quaternion(Basis.IDENTITY)
	# 出生时立即对齐到所在星球（避免第一帧姿态错乱）
	if solar_system != null:
		var dom: Dictionary = solar_system.dominant_planet_at(global_position)
		var pl: Planet = dom["planet"]
		if pl != null:
			bound_planet = pl
			_bound_prev_pos = pl.global_position
			var d := global_position - pl.global_position
			if d.length_squared() > 1e-6:
				_up = d.normalized()
				_up_target = _up
			_gravity_strength = pl.definition.gravity_strength
			_surface_weight = 1.0
			view_mode = ViewMode.SURFACE
	_apply_view()


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseMotion and _mouse_captured:
		_look(event.relative.x, event.relative.y)
	elif event.is_action_pressed("toggle_mouse"):
		_mouse_captured = not _mouse_captured
		Input.mouse_mode = Input.MOUSE_MODE_CAPTURED if _mouse_captured else Input.MOUSE_MODE_VISIBLE
	elif event.is_action_pressed("toggle_fly"):
		flying = not flying
		_velocity = Vector3.ZERO
	elif event.is_action_pressed("dig"):
		_interact(true)
	elif event.is_action_pressed("build"):
		_interact(false)


## 鼠标视角。FREE 模式用四元数累加（真正自由）；SURFACE 模式用 yaw/pitch 并夹紧。
func _look(dx: float, dy: float) -> void:
	# FREE 累加器始终更新，保证切回宇宙时朝向连续
	var yaw_q := Quaternion(Vector3.UP, -dx * mouse_sensitivity)
	var pitch_q := Quaternion(Vector3.RIGHT, -dy * mouse_sensitivity)
	_free_rot = (_free_rot * yaw_q * pitch_q).normalized()

	# SURFACE 模式（相对局部重力参考系）
	_yaw -= dx * mouse_sensitivity
	_pitch -= dy * mouse_sensitivity
	# 最低只能看到星球中心方向（-90°），最高抬头 90°
	_pitch = clampf(_pitch, -PI * 0.5 + 0.001, PI * 0.5 - 0.001)
	_apply_view()


func _physics_process(delta: float) -> void:
	if solar_system == null:
		return

	# ── 1. 重力场：找出主导星球，平滑修正 up 与重力强度 ──
	_update_gravity_field(delta)

	# ── 2. 让玩家跟随绑定星球的公转 ──
	if bound_planet != null and _bound_prev_pos != Vector3.ZERO:
		var dp := bound_planet.global_position - _bound_prev_pos
		if dp.length_squared() > 0.0:
			global_position += dp
	if bound_planet != null:
		_bound_prev_pos = bound_planet.global_position

	# ── 3. 移动 ──
	_apply_movement(delta)

	# ── 4. 地形窗口跟随 ──
	if bound_planet != null and bound_planet.volume != null:
		bound_planet.volume.follow(global_position)

	# ── 5. 姿态（每帧重建，保证 up 变化时视角跟着转） ──
	_apply_view()


# ───────────────────────── 重力场 ─────────────────────────

func _update_gravity_field(delta: float) -> void:
	var best: Planet = null
	var best_strength := 0.0
	var best_up := _up

	for p in solar_system.planets:
		var info: Dictionary = p.gravity_info_at(global_position)
		var strength: float = info["strength"]
		if strength > best_strength:
			best_strength = strength
			best = p
			best_up = info["up"]

	var target_up := best_up if best != null else _up
	# 平滑插值 up（缓慢修正姿态，不会瞬间拧麻花）
	var t := 1.0 - exp(-gravity_align_speed * delta)
	_up = _up.slerp(target_up, t).normalized()

	var was_bound := bound_planet
	bound_planet = best
	if best != null and was_bound != best:
		_bound_prev_pos = best.global_position
		# 进入新星球重力场：用当前朝向初始化 yaw/pitch，避免跳变
		_init_surface_angles_from_current()
	elif best == null:
		_bound_prev_pos = Vector3.ZERO

	_gravity_strength = best_strength

	# 模式权重平滑过渡
	var target_w: float = 1.0 if best != null else 0.0
	_surface_weight = lerpf(_surface_weight, target_w, 1.0 - exp(-field_blend_speed * delta))
	view_mode = ViewMode.SURFACE if _surface_weight > 0.5 else ViewMode.FREE


## 由当前 forward 反解出相对 _up 的 yaw/pitch（用于进入重力场时保持朝向连续）。
func _init_surface_angles_from_current() -> void:
	var fwd := -camera.global_transform.basis.z
	# 参考系：up = _up，right/fwd 由切平面构造
	var ref := _tangent_reference(_up)
	var right := ref.cross(_up).normalized()
	var base_fwd := _up.cross(right).normalized()
	_yaw = atan2(fwd.dot(right), fwd.dot(base_fwd))
	_pitch = asin(clampf(fwd.dot(_up), -1.0, 1.0))
	_pitch = clampf(_pitch, -PI * 0.5 + 0.001, PI * 0.5 - 0.001)


## 选一个与 up 不平行的参考向量，避免退化。
func _tangent_reference(up: Vector3) -> Vector3:
	var ref := Vector3.FORWARD
	if absf(ref.dot(up)) > 0.99:
		ref = Vector3.RIGHT
	return ref


# ───────────────────────── 移动 ─────────────────────────

func _apply_movement(delta: float) -> void:
	var fwd := -camera.global_transform.basis.z
	var right := camera.global_transform.basis.x

	var input_dir := Vector2.ZERO
	if Input.is_action_pressed("move_forward"): input_dir.y -= 1.0
	if Input.is_action_pressed("move_back"): input_dir.y += 1.0
	if Input.is_action_pressed("move_left"): input_dir.x -= 1.0
	if Input.is_action_pressed("move_right"): input_dir.x += 1.0
	input_dir = input_dir.normalized()

	var speed := move_speed
	if _surface_weight < 0.5:
		speed = space_speed
	elif flying:
		speed = fly_speed
	if Input.is_action_pressed("sprint"):
		speed *= sprint_multiplier

	var wish := (right * input_dir.x + fwd * -input_dir.y) * speed

	if flying or _surface_weight < 0.5:
		# 飞行 / 太空：Space 上升 / Ctrl 下落（相对当前 up）
		var up_amount := 0.0
		if Input.is_action_pressed("move_up"): up_amount += 1.0
		if Input.is_action_pressed("move_down"): up_amount -= 1.0
		var target := wish + _up * up_amount * speed
		var rate := 10.0 if _surface_weight > 0.5 else 3.0
		_velocity = _velocity.lerp(target, clampf(delta * rate, 0.0, 1.0))
		global_position += _velocity * delta
	else:
		# 步行：水平加速 + 沿局部重力下落
		var horiz := _velocity - _up * _velocity.dot(_up)
		horiz = horiz.lerp(wish, clampf(delta * 12.0, 0.0, 1.0))
		var vertical := _up * _velocity.dot(_up)
		vertical -= _up * _gravity_strength * delta
		_velocity = horiz + vertical
		if Input.is_action_pressed("move_up") and _is_grounded():
			_velocity += _up * 6.0
		global_position += _velocity * delta
		_resolve_collision()


## 沿地形 SDF 梯度把玩家推出地表（无网格碰撞）。
func _resolve_collision() -> void:
	if bound_planet == null or bound_planet.volume == null:
		return
	var den := bound_planet.sample_density(global_position)
	var guard := 0
	while den < 0.0 and guard < 8:
		var g := _world_gradient(bound_planet, global_position)
		var n := g.normalized() if g.length_squared() > 1e-9 else _up
		global_position += n * (absf(den) + 0.05)
		var vn := _velocity.dot(n)
		if vn < 0.0:
			_velocity -= n * vn
		den = bound_planet.sample_density(global_position)
		guard += 1


## 世界空间密度梯度（经 Planet 的行星局部转换）。
func _world_gradient(p: Planet, world_pos: Vector3) -> Vector3:
	if p.volume == null:
		return _up
	var local := p.to_local(world_pos)
	return p.volume.gradient(local)


## 手动绑定到某颗行星（传送用）。会立即对齐姿态。
func bind_to_planet(p: Planet) -> void:
	bound_planet = p
	_bound_prev_pos = p.global_position
	var d := global_position - p.global_position
	if d.length_squared() > 1e-6:
		_up = d.normalized()
		_up_target = _up
	_gravity_strength = p.definition.gravity_strength
	_surface_weight = 1.0
	view_mode = ViewMode.SURFACE
	_velocity = Vector3.ZERO
	_init_surface_angles_from_current()


func _is_grounded() -> bool:
	if bound_planet == null or bound_planet.volume == null:
		return false
	var foot := global_position - _up * (capsule_height * 0.5 + 0.1)
	return bound_planet.sample_density(foot) < 0.0


# ───────────────────────── 视角基（根因修复处） ─────────────────────────

## 构造 SURFACE 模式的基。
##
## 旧实现（错误）：Basis(Quaternion(up, yaw)) —— 从**世界单位帧**绕 up 旋转，
##   导致 (a) pitch 轴 basis.x 与 up 在 up≈±X 时退化成同一根轴，两轴互相吞掉；
##        (b) 当 up≈±Z 时 forward 不在切平面内（镜头直插地面）。
##
## 正确做法：先构造一个 **Y 轴 = up 的切平面正交基**，再在其上叠加 yaw（绕 up）
## 与 pitch（绕 yaw 后的 right）。这样 yaw/pitch 永远互相正交，且 forward 必在切平面内。
func _surface_basis(up: Vector3, yaw: float, pitch: float) -> Basis:
	up = up.normalized()
	var ref := _tangent_reference(up)
	var right := ref.cross(up).normalized()
	var fwd := up.cross(right).normalized()
	# base：X=right, Y=up, Z=-fwd（Godot 相机朝 -Z）
	var base := Basis(right, up, -fwd)
	# yaw 绕 up
	var q_yaw := Quaternion(up, yaw)
	# pitch 绕 yaw 之后的 right
	var right2 := (q_yaw * right).normalized()
	# 符号约定：pitch > 0 = 抬头（forward 转向 +up）。
	# 绕 right2 正角旋转把 -Z 转向 +up，因此用 +pitch。
	var q_pitch := Quaternion(right2, pitch)
	return (Basis(q_pitch) * Basis(q_yaw) * base).orthonormalized()


## 把当前视角写入 transform。
## FREE 权重与 SURFACE 权重做球面插值，切换星球时不会跳变。
func _apply_view() -> void:
	var b_surface := _surface_basis(_up, _yaw, _pitch)
	var b_free := Basis(_free_rot)
	var w := clampf(_surface_weight, 0.0, 1.0)
	var q := Basis(b_free).get_rotation_quaternion().slerp(
		b_surface.get_rotation_quaternion(), w)
	global_transform.basis = Basis(q).orthonormalized()


func get_forward() -> Vector3:
	return -camera.global_transform.basis.z


## 初始化朝向（在重力场内沿切平面看向某方向）。
func look_towards(dir: Vector3) -> void:
	if dir.length_squared() < 1e-9:
		return
	var d := dir.normalized()
	# 用 up 构造参考系，反解 yaw/pitch
	var up := _up
	var ref := _tangent_reference(up)
	var right := ref.cross(up).normalized()
	var base_fwd := up.cross(right).normalized()
	_yaw = atan2(d.dot(right), d.dot(base_fwd))
	_pitch = clampf(asin(clampf(d.dot(up), -1.0, 1.0)), -PI * 0.5 + 0.001, PI * 0.5 - 0.001)
	# FREE 累加器也对齐，避免切模式时跳
	_free_rot = _surface_basis(up, _yaw, _pitch).get_rotation_quaternion()
	_apply_view()


# ───────────────────────── 挖掘 ─────────────────────────

func _interact(dig: bool) -> void:
	if bound_planet == null or bound_planet.volume == null or not _mouse_captured:
		return
	var from := camera.global_position
	var dir := -camera.global_transform.basis.z
	var hit = bound_planet.raycast(from, dir)
	if hit == null:
		return
	var pos: Vector3 = hit
	var n := _world_gradient(bound_planet, pos).normalized()
	var target: Vector3 = pos - n * (dig_radius * 0.5 if dig else -dig_radius * 0.5)
	if dig:
		bound_planet.dig(target, dig_radius)
	else:
		bound_planet.build(target, dig_radius)
