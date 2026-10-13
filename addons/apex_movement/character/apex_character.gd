class_name ApexCharacter
extends Node3D
## Apex 风格角色（玩法层）。
##
## 职责边界（严格遵守项目约定）：
##   - **本类（GDScript）**：读输入、做物理探测（地面/墙射线）、驱动 <see cref="ApexMovementCore"/>、
##     把结果写回自身 transform。球面重力由外部 <c>SolarSystem</c> 提供。
##   - **C# 核心（ApexMovementCore）**：纯确定性移动求解，不碰引擎、不做射线。
##
## 因此移动逻辑可在客户端权威（本地预测）与服务器权威（重放校验）间共用。
##
## 环境适配：ProjectA 是**球面行星**——up 不是世界 Y，而是「背离行星质心」。
## 本类不硬编码任何世界坐标；up 由 <see cref="gravity_up"/> 回调给出。

const Core := preload("res://addons/apex_movement/core/ApexMovementCore.cs")

## 输入按钮位（与 C# ApexMoveInput.BTN_* 常量一致；GDScript 读不到 C# 嵌套枚举）。
const BTN_JUMP := 1 << 0
const BTN_CROUCH := 1 << 1
const BTN_SPRINT := 1 << 2
const BTN_FORWARD := 1 << 3
const BTN_BACK := 1 << 4
const BTN_LEFT := 1 << 5
const BTN_RIGHT := 1 << 6

@export var config: ApexMoveConfig
## 固定物理步长（与 ProjectA 的物理帧率一致，保证确定性）。
@export var fixed_dt: float = 1.0 / 60.0
## 地面探测距离（从脚底向下）。
@export var ground_probe_distance: float = 0.6
## 墙探测距离（侧向，蹬墙跑用）。
@export var wall_probe_distance: float = 1.2
## 角色碰撞体半径 / 站立高度（供射线探测与碰撞胶囊用）。
@export var radius: float = 0.4
@export var stand_height: float = 1.8

## 是否自行采集本地输入。false 时由外部（网络回放 / AI / 测试）填充 <see cref="input_frame"/>。
@export var autonomous_input: bool = true

var state: ApexMoveState
var env: ApexMoveEnv
var input_frame: ApexMoveInput
var camera: Camera3D
## C# 确定性求解器实例（持有即可复用，无每帧分配）。
var core: ApexMovementCore

## 球面重力回调：返回 { "up": Vector3, "gravity": float }。
## 由外部（如 bootstrap / SolarSystem 适配器）设置；未设置时用世界 Y。
var gravity_up: Callable = Callable()

## 地形 SDF 回调（可选）。签名：func(world_pos: Vector3) -> float，返回密度（<0 = 实体）。
## ProjectA 的地形是 **raymarch 密度场，没有碰撞体** —— 因此地面/墙探测必须走这里，
## 用密度梯度求法线，而不是物理射线。未设置时回退到物理射线（适用于有碰撞体的场景）。
var sdf_density: Callable = Callable()

var _sprinting: bool = false
var _jump_was_down: bool = false
## 本帧调试信息（供 HUD / 测试读取）。
var debug_info: Dictionary = {}


func _ready() -> void:
	if config == null:
		config = ApexMoveConfig.new()
	state = ApexMoveState.new()
	env = ApexMoveEnv.new()
	input_frame = ApexMoveInput.new()
	core = ApexMovementCore.new()
	_setup_camera()


func _setup_camera() -> void:
	camera = Camera3D.new()
	camera.name = "Camera3D"
	camera.position = Vector3(0, stand_height * 0.85, 0)
	camera.fov = 75.0
	camera.far = 60000.0
	add_child(camera)
	camera.current = true


# ───────────────────────── 主循环 ─────────────────────────

func _physics_process(delta: float) -> void:
	if state == null:
		return
	# 固定步长累积（确定性）；一帧内可能跑多步，保证与网络 tick 对齐
	_accumulate(delta)


var _accum: float = 0.0

func _accumulate(delta: float) -> void:
	_accum += delta
	var steps := 0
	while _accum >= fixed_dt and steps < 5:
		_step(fixed_dt)
		_accum -= fixed_dt
		steps += 1


func _step(dt: float) -> void:
	# 1) 探测环境（GDScript 侧）
	_probe_environment()

	# 2) 组装确定性输入（外部驱动时保留调用方填入的 input_frame）
	if autonomous_input:
		_build_input()

	# 3) 核心推进
	core.Step(state, input_frame, env, config, dt)

	# 4) 写回 transform
	global_position = state.Position
	_update_orientation(dt)

	debug_info = {
		"speed": state.Velocity.length(),
		"on_ground": state.OnGround,
		"sliding": state.Sliding,
		"wallrunning": state.Wallrunning,
		"crouching": state.Crouching,
	}


# ───────────────────────── 环境探测 ─────────────────────────

func _probe_environment() -> void:
	var up := _get_up()
	env.Up = up
	env.GravityMagnitude = _get_gravity()

	if sdf_density.is_valid():
		_probe_environment_sdf(up)
	else:
		_probe_environment_raycast(up)


## SDF 探测（ProjectA raymarch 地形专用，无碰撞体）。
## 密度场近似有符号距离（baseline = (r-radius)+noise，梯度≈单位径向），
## 因此可直接把脚底**投影**到 density=0 的等值面上，得到精确吸附偏移。
func _probe_environment_sdf(up: Vector3) -> void:
	var foot: Vector3 = state.Position - up * (stand_height * 0.5)
	var d_foot: float = sdf_density.call(foot)

	# 接地判定：脚底在地表内，或在地表上方 probe 距离内
	if d_foot < ground_probe_distance:
		env.Grounded = true
		var surf := _project_to_surface(foot, up)
		env.GroundNormal = surf["normal"]
		# 吸附偏移 = 把脚底移到等值面上的位移；夹紧上限，避免密度场陡峭时瞬移
		var snap: Vector3 = surf["point"] - foot
		var max_snap: float = ground_probe_distance * 2.0
		if snap.length() > max_snap:
			snap = snap.normalized() * max_snap
		env.GroundSnap = snap
	else:
		env.Grounded = false
		env.GroundNormal = up
		env.GroundSnap = Vector3.ZERO

	env.SurfaceFriction = 1.0

	# 墙探测（蹬墙跑）
	env.HasWall = false
	env.WallIsRunable = false
	env.WallDistance = 999.0
	if config.WallrunEnabled and not env.Grounded:
		_probe_walls_sdf(up)


## 把点沿密度梯度投影到 density=0 的等值面（牛顿步，2 次迭代足够收敛）。
## 返回 { "point": Vector3, "normal": Vector3, "signed_dist": float }。
func _project_to_surface(p: Vector3, up: Vector3) -> Dictionary:
	var cur := p
	var d: float = sdf_density.call(cur)
	var n := _sdf_normal(cur, up)
	for i in 2:
		var g := _sdf_normal(cur, up)
		var gl := g.length()
		if gl < 1e-6:
			break
		# 沿梯度方向走 d/|grad| 即到达等值面
		cur -= g * (d / (gl * gl))
		n = g
		d = sdf_density.call(cur)
	return { "point": cur, "normal": n.normalized(), "signed_dist": d }


## 用密度梯度求表面法线。
## 约定：密度 <0 = 实体、>0 = 空气；密度沿「实体→空气」方向增大，
## 故梯度方向即为指向空气侧的表面法线（与 ProjectA 的 SDF 推挤方向一致）。
func _sdf_normal(p: Vector3, up: Vector3) -> Vector3:
	var e := 0.25
	var gx: float = sdf_density.call(p + Vector3(e, 0, 0)) - sdf_density.call(p - Vector3(e, 0, 0))
	var gy: float = sdf_density.call(p + Vector3(0, e, 0)) - sdf_density.call(p - Vector3(0, e, 0))
	var gz: float = sdf_density.call(p + Vector3(0, 0, e)) - sdf_density.call(p - Vector3(0, 0, e))
	var g := Vector3(gx, gy, gz)
	if g.length_squared() < 1e-9:
		return up
	return g.normalized()


func _probe_walls_sdf(up: Vector3) -> void:
	var horiz: Vector3 = state.Velocity - up * state.Velocity.dot(up)
	if horiz.length_squared() < 0.01:
		return
	var vd: Vector3 = horiz.normalized()
	for side in [vd.cross(up).normalized(), -vd.cross(up).normalized()]:
		var step := 0.15
		var dist := 0.0
		while dist < wall_probe_distance:
			dist += step
			var p: Vector3 = state.Position + side * dist
			if sdf_density.call(p) < 0.0:
				var n := _sdf_normal(p, up)
				# 墙面需近似竖直：法线与 up 接近垂直
				if absf(n.dot(up)) < 0.35:
					env.HasWall = true
					env.WallNormal = n
					env.WallDistance = dist
					env.WallIsRunable = true
					return
				break


## 物理射线探测（适用于有碰撞体的场景）。
func _probe_environment_raycast(up: Vector3) -> void:
	var foot: Vector3 = state.Position - up * (stand_height * 0.5)
	var space := get_world_3d().direct_space_state
	var ground_query := PhysicsRayQueryParameters3D.create(
		foot + up * 0.1, foot - up * ground_probe_distance)
	var hit := space.intersect_ray(ground_query)
	if hit:
		env.Grounded = true
		env.GroundNormal = hit.normal
		env.SurfaceFriction = 1.0
	else:
		env.Grounded = false
		env.GroundNormal = up

	env.HasWall = false
	env.WallIsRunable = false
	env.WallDistance = 999.0
	if config.WallrunEnabled and not env.Grounded:
		_probe_walls_raycast(space, up)


func _probe_walls_raycast(space: PhysicsDirectSpaceState3D, up: Vector3) -> void:
	var horiz: Vector3 = state.Velocity - up * state.Velocity.dot(up)
	if horiz.length_squared() < 0.01:
		return
	var vd: Vector3 = horiz.normalized()
	for side in [vd.cross(up).normalized(), -vd.cross(up).normalized()]:
		var from: Vector3 = state.Position
		var q := PhysicsRayQueryParameters3D.create(from, from + side * wall_probe_distance)
		var h := space.intersect_ray(q)
		if h:
			var n: Vector3 = h.normal
			if absf(n.dot(up)) < 0.35:
				env.HasWall = true
				env.WallNormal = n
				env.WallDistance = from.distance_to(h.position)
				env.WallIsRunable = true
				return


# ───────────────────────── 输入 ─────────────────────────

func _build_input() -> void:
	var up: Vector3 = env.Up
	# 相机朝向投影到 up 切平面 → 世界空间 wishdir
	var cam_fwd := -camera.global_transform.basis.z
	var cam_right := camera.global_transform.basis.x
	cam_fwd -= up * cam_fwd.dot(up)
	cam_right -= up * cam_right.dot(up)

	var dir := Vector2.ZERO
	if Input.is_action_pressed("move_forward"): dir.y -= 1.0
	if Input.is_action_pressed("move_back"): dir.y += 1.0
	if Input.is_action_pressed("move_left"): dir.x -= 1.0
	if Input.is_action_pressed("move_right"): dir.x += 1.0

	var wish := Vector3.ZERO
	if dir.length_squared() > 0.0:
		dir = dir.normalized()
		wish = (cam_right.normalized() * dir.x + cam_fwd.normalized() * -dir.y).normalized()
	input_frame.WishDir = wish

	var buttons := 0
	if Input.is_action_pressed("sprint"): buttons |= BTN_SPRINT
	if Input.is_action_pressed("move_down"): buttons |= BTN_CROUCH
	input_frame.Buttons = buttons

	var jump_down := Input.is_action_pressed("move_up")
	input_frame.JumpHeld = jump_down
	input_frame.JumpPressed = jump_down and not _jump_was_down
	_jump_was_down = jump_down


# ───────────────────────── 朝向 ─────────────────────────

func _update_orientation(dt: float) -> void:
	# 角色直立方向对齐 up（球面重力）；朝向由外部相机控制
	var up: Vector3 = state.Up
	var fwd := -camera.global_transform.basis.z
	fwd -= up * fwd.dot(up)
	if fwd.length_squared() < 1e-6:
		return
	fwd = fwd.normalized()
	var right := fwd.cross(up).normalized()
	var basis := Basis(right, up, -fwd).orthonormalized()
	global_transform.basis = global_transform.basis.slerp(basis, clampf(dt * 12.0, 0.0, 1.0))


# ───────────────────────── 重力适配 ─────────────────────────

func _get_up() -> Vector3:
	if gravity_up.is_valid():
		var r = gravity_up.call(state.Position)
		if r is Dictionary:
			return r.get("up", Vector3.UP)
		elif r is Vector3:
			return r
	return Vector3.UP


func _get_gravity() -> float:
	if gravity_up.is_valid():
		var r = gravity_up.call(state.Position)
		if r is Dictionary:
			return r.get("gravity", 9.8)
	return 9.8


# ───────────────────────── 外部 API（网络 / 传送） ─────────────────────────

## 传送到指定位置并重置速度（跨行星旅行用）。
func teleport_to(pos: Vector3, keep_velocity: bool = false) -> void:
	state.Position = pos
	if not keep_velocity:
		state.Velocity = Vector3.ZERO
	state.Sliding = false
	state.Wallrunning = false
	state.AirJumpsUsed = 0
	global_position = pos


## 应用一条来自远端的权威状态（服务器权威模式）。
func apply_authoritative_state(s: ApexMoveState) -> void:
	s.CopyTo(state)
	global_position = state.Position


## 导出当前状态（网络同步 / 存档）。
func get_state() -> PackedFloat32Array:
	return PackedFloat32Array(state.GetState())


func set_state(data: PackedFloat32Array) -> void:
	state.SetState(data)
	global_position = state.Position


## 初始化朝向（出生时）。
func look_towards(dir: Vector3) -> void:
	if dir.length_squared() < 1e-6:
		return
	camera.look_at(camera.global_position + dir.normalized(), _get_up())
