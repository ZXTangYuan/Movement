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

## 是否做横向碰撞解算（collide-and-slide）。有碰撞体的场景应为 true。
## 纯 SDF 地形（raymarch）下由玩法层的推挤负责，可设 false。
@export var horizontal_collision: bool = true
## 碰撞解算的最大迭代（每次滑动一个平面）。
@export var collision_max_slides: int = 4
## 调试：打印碰撞解算细节。
@export var debug_collision: bool = false

var _sprinting: bool = false
var _jump_was_down: bool = false
## 视角 yaw/pitch（相对当前 up 参考系；球面重力下自动随局部 up 变化）。
var yaw: float = 0.0
var pitch: float = 0.0
## 鼠标灵敏度。
@export var mouse_sensitivity: float = 0.0025
## 眼睛相对脚底的高度比例（决定相机位置）。
@export var eye_height_ratio: float = 0.85
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

	# 3) 核心推进（记录推进前位置，供横向碰撞解算）
	var pre_pos: Vector3 = state.Position
	var was_mantling: bool = state.Mantling
	core.Step(state, input_frame, env, config, dt)

	# 3.5) 横向碰撞解算（collide-and-slide）——有碰撞体的场景必需。
	#      翻越/攀爬是脚本化位移：本帧「曾处于」翻越状态也要跳过解算，
	#      否则翻越完成的那一帧（Mantling 已置 false）会被当成穿墙而弹回墙前。
	if horizontal_collision and not state.Mantling and not was_mantling:
		_resolve_horizontal_collision(pre_pos)

	# 4) 写回 transform
	global_position = state.Position
	_update_orientation(dt)
	_apply_look()

	debug_info = {
		"speed": state.Velocity.length(),
		"on_ground": state.OnGround,
		"sliding": state.Sliding,
		"wallrunning": state.Wallrunning,
		"crouching": state.Crouching,
		"mantling": state.Mantling,
		"climbing": state.MantleIsClimb,
		"obstacle_ahead": env.ObstacleAhead,
		"obstacle_height": env.ObstacleHeight,
	}


## 横向碰撞解算：collide-and-slide + 台阶上抬（对应 Source 的 StepMove）。
## 用胶囊体做 shape cast（cast_motion + get_rest_info），最多迭代 collision_max_slides 次。
func _resolve_horizontal_collision(pre_pos: Vector3) -> void:
	var target: Vector3 = state.Position
	var motion: Vector3 = target - pre_pos
	if motion.length_squared() < 1e-10:
		return
	var space := get_world_3d().direct_space_state
	var up: Vector3 = state.Up

	var capsule := _make_capsule()
	var cur: Vector3 = pre_pos
	for i in collision_max_slides:
		var q := PhysicsShapeQueryParameters3D.new()
		q.shape = capsule
		q.transform = Transform3D(_up_basis(up), cur)
		q.motion = motion
		q.margin = 0.001
		var res: PackedFloat32Array = space.cast_motion(q)
		if res.size() < 2:
			break
		var safe: float = res[0]
		cur += motion * safe
		if safe >= 0.999:
			break

		var rest := space.get_rest_info(q)
		var n: Vector3 = rest["normal"] if rest.has("normal") else -motion.normalized()

		if debug_collision:
			print("      [collide] i=%d safe=%.3f n=%s n·up=%.3f" % [
				i, safe, n.snapped(Vector3(0.01, 0.01, 0.01)), n.dot(up)])

		# 台阶上抬（Source StepMove）：撞到近竖直障碍时，尝试抬高 StepHeight 再前进。
		# 不依赖 rest normal 的具体值——只要被挡住就试抬升，抬升后能过就走台阶。
		if n.dot(up) < 0.7:
			var horiz: Vector3 = motion - up * motion.dot(up)
			var stepped := false
			# 从 StepHeight 往下试几个高度，取能通过的最高抬升（避免小台阶被大抬升顶飞）
			var try_h := config.StepHeight
			while try_h > 0.01:
				var lifted: Vector3 = cur + up * (try_h + 0.02)
				var lifted_target: Vector3 = lifted + horiz
				if _is_free(space, capsule, lifted, up) and _is_free(space, capsule, lifted_target, up):
					cur = lifted_target
					stepped = true
					break
				try_h -= 0.05
			if stepped:
				continue

		# 沿墙滑动：去掉剩余位移与速度的法向分量
		motion = motion - n * motion.dot(n)
		var vn: float = state.Velocity.dot(n)
		if vn < 0.0:
			state.Velocity -= n * vn
		if motion.length_squared() < 1e-10:
			break

	state.Position = cur
	if debug_collision:
		print("      [collide-exit] pre=%s motion=%s cur=%s" % [
			pre_pos.snapped(Vector3(0.01,0.01,0.01)), (target - pre_pos).snapped(Vector3(0.01,0.01,0.01)),
			cur.snapped(Vector3(0.01,0.01,0.01))])


## 胶囊体（沿 up 对齐，中心在角色原点）。
## 注意：**内缩一个 skin**——角色脚底正好贴地时，若不内缩，胶囊底面与地面持续接触，
## cast_motion 会返回 safe_fraction=0，导致每帧位移被清零、角色无法横向移动。
func _make_capsule() -> CapsuleShape3D:
	var c := CapsuleShape3D.new()
	var skin := 0.05
	c.radius = maxf(radius - skin, 0.05)
	c.height = maxf(stand_height - skin * 2.0, c.radius * 2.0 + 0.01)
	return c


## 该位置放置胶囊是否不重叠任何碰撞体。
func _is_free(space: PhysicsDirectSpaceState3D, capsule: CapsuleShape3D,
		pos: Vector3, up: Vector3) -> bool:
	var q := PhysicsShapeQueryParameters3D.new()
	q.shape = capsule
	q.transform = Transform3D(_up_basis(up), pos)
	q.margin = 0.001
	return space.intersect_shape(q, 1).is_empty()


## 构造 Y 轴对齐到 up 的基（胶囊朝向用）。
func _up_basis(up: Vector3) -> Basis:
	var y: Vector3 = up.normalized()
	var ref: Vector3 = Vector3.FORWARD if absf(y.dot(Vector3.FORWARD)) < 0.99 else Vector3.RIGHT
	var x: Vector3 = ref.cross(y).normalized()
	var z: Vector3 = x.cross(y).normalized()
	return Basis(x, y, z)


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
	env.ObstacleAhead = false
	if config.WallrunEnabled and not env.Grounded:
		_probe_walls_sdf(up)
	# 障碍探测（攀爬/翻越）
	_probe_obstacle_sdf(up)


## 障碍探测（SDF）：沿移动方向步进找「前方有竖直挡面 + 上方有可站立顶面」。
func _probe_obstacle_sdf(up: Vector3) -> void:
	var move_dir: Vector3 = input_frame.WishDir
	move_dir -= up * move_dir.dot(up)
	if move_dir.length_squared() < 0.01:
		return
	move_dir = move_dir.normalized()

	var foot: Vector3 = state.Position - up * (stand_height * 0.5)
	var probe_h: float = minf(config.MantleHeight, stand_height * 0.6)
	var base: Vector3 = foot + up * probe_h

	# 1) 前方步进找表面（密度由 >0 变 <0）
	var wall_hit := false
	var hit_pos := Vector3.ZERO
	var dist := 0.0
	var step := 0.2
	var max_d := radius + config.MantleForwardOffset + 0.35
	while dist < max_d:
		dist += step
		var p: Vector3 = base + move_dir * dist
		if sdf_density.call(p) < 0.0:
			wall_hit = true
			hit_pos = p
			break
	if not wall_hit:
		return
	# 表面必须竖直（法线近水平）
	var wall_n := _sdf_normal(hit_pos, up)
	if absf(wall_n.dot(up)) > 0.4:
		return

	# 2) 从命中点内侧向上找顶面（密度由 <0 变 >0）
	var inside: Vector3 = hit_pos + move_dir * 0.25
	var up_dist := 0.0
	var top_hit := false
	var top_pos := Vector3.ZERO
	var max_up: float = config.ClimbHeight + 0.5
	while up_dist < max_up:
		up_dist += step
		var p: Vector3 = inside + up * up_dist
		if sdf_density.call(p) > 0.0:
			top_hit = true
			top_pos = p
			break
	if not top_hit:
		return
	var top_n := _sdf_normal(top_pos, up)
	if top_n.dot(up) < 0.7:
		return
	var obstacle_h: float = top_pos.dot(up) - foot.dot(up)
	if obstacle_h <= config.StepHeight or obstacle_h > config.ClimbHeight:
		return

	env.ObstacleAhead = true
	env.ObstacleHeight = obstacle_h
	env.ObstacleTopNormal = top_n
	env.MantleTargetPoint = top_pos + up * (stand_height * 0.5 + 0.05) + move_dir * config.MantleForwardOffset


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

	# 地面吸附：接地时把脚底对齐到命中面（射线路径下也用，保证上下坡稳定）
	if env.Grounded and hit:
		env.GroundSnap = hit.position - foot
	else:
		env.GroundSnap = Vector3.ZERO

	env.HasWall = false
	env.WallIsRunable = false
	env.WallDistance = 999.0
	env.ObstacleAhead = false
	if config.WallrunEnabled and not env.Grounded:
		_probe_walls_raycast(space, up)
	# 障碍探测（攀爬/翻越）：始终探测（地面走近障碍也要能翻）
	_probe_obstacle_raycast(space, up)


## 障碍探测（射线）：前方竖直挡面 → 从墙上方**向下**打射线找可站立顶面。
## （不用「从墙内向上打」——Godot 对起点在实体内部的射线不可靠。）
func _probe_obstacle_raycast(space: PhysicsDirectSpaceState3D, up: Vector3) -> void:
	var move_dir: Vector3 = input_frame.WishDir
	move_dir -= up * move_dir.dot(up)
	if move_dir.length_squared() < 0.01:
		return
	move_dir = move_dir.normalized()

	var foot: Vector3 = state.Position - up * (stand_height * 0.5)

	# 1) 前方（胸口高度）找竖直挡面
	var probe_h: float = minf(config.MantleHeight, stand_height * 0.6)
	var from: Vector3 = foot + up * probe_h
	var to: Vector3 = from + move_dir * (radius + config.MantleForwardOffset + 0.5)
	var hit := space.intersect_ray(PhysicsRayQueryParameters3D.create(from, to))
	if not hit:
		return
	var wall_n: Vector3 = hit.normal
	if absf(wall_n.dot(up)) > 0.4:      # 必须是竖直面
		return
	var wall_pos: Vector3 = hit.position

	# 2) 从墙面上方内侧**向下**打射线 → 找顶面
	var over: Vector3 = wall_pos + move_dir * 0.15        # 越过墙面一点点（在墙顶上方）
	var down_from: Vector3 = over + up * (config.ClimbHeight + 0.6)
	var down_to: Vector3 = over - up * 0.3
	var top_hit := space.intersect_ray(PhysicsRayQueryParameters3D.create(down_from, down_to))
	if not top_hit:
		return
	var top_n: Vector3 = top_hit.normal
	if top_n.dot(up) < 0.7:             # 顶面必须接近水平（可站立）
		return
	var top_point: Vector3 = top_hit.position
	var obstacle_h: float = top_point.dot(up) - foot.dot(up)
	if obstacle_h <= config.StepHeight or obstacle_h > config.ClimbHeight:
		return

	env.ObstacleAhead = true
	env.ObstacleHeight = obstacle_h
	env.ObstacleTopNormal = top_n
	env.MantleTargetPoint = top_point + up * (stand_height * 0.5 + 0.05) + move_dir * config.MantleForwardOffset


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


# ───────────────────────── 视角 ─────────────────────────

## 鼠标视角输入（由外部/场景调用，或 autonomous 模式下自动接管）。
func add_look(dx: float, dy: float) -> void:
	yaw -= dx * mouse_sensitivity
	pitch = clampf(pitch - dy * mouse_sensitivity, -PI * 0.5 + 0.01, PI * 0.5 - 0.01)
	_apply_look()


## 把 yaw/pitch 写入相机（以当前 up 为参考，球面重力下自动正确）。
func _apply_look() -> void:
	if camera == null:
		return
	var up: Vector3 = state.Up if state != null else Vector3.UP
	# 参考系：Y=up，再叠加 yaw（绕 up）与 pitch（绕 yaw 后的 right）
	var ref: Vector3 = Vector3.FORWARD
	if absf(ref.dot(up)) > 0.99:
		ref = Vector3.RIGHT
	var right: Vector3 = ref.cross(up).normalized()
	var fwd: Vector3 = up.cross(right).normalized()
	var base := Basis(right, up, -fwd)
	var q_yaw := Quaternion(up, yaw)
	var right2 := (q_yaw * right).normalized()
	var q_pitch := Quaternion(right2, pitch)
	var b := (Basis(q_pitch) * Basis(q_yaw) * base).orthonormalized()
	# 相机本地朝向（相机是角色子节点，角色基已对齐 up）
	camera.global_transform.basis = b
	camera.global_position = global_position + up * (stand_height * eye_height_ratio)


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
	state.Mantling = false
	state.MantleProgress = 0.0
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
