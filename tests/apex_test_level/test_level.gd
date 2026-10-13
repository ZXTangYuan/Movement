extends Node3D
## Apex 移动测试关卡（**独立**，不使用虚拟世界/raymarch 系统）。
##
## 全部用 Godot 原生 StaticBody3D + CollisionShape3D 搭建，专门验证 ApexMovement 插件的：
##   平地 / 上坡 / 下坡 / 台阶 / 障碍翻越(mantle) / 高墙攀爬(climb) / 地洞 /
##   蹬墙跑 / 滑铲坡。
##
## 为什么独立：虚拟世界（raymarch 密度场）目前不完善，会干扰移动测试。
## 本关卡提供**确定的碰撞体几何**，让移动逻辑可被精确验证。
##
## 约定：up = 世界 +Y。所有构件以「几何事实」描述（两点定坡、顶面高度定平台）。

const GROUND_SIZE := 120.0
const WALL_COLOR := Color(0.45, 0.47, 0.52)

## 关卡物件清单（供测试读取出生点与断言目标）。
var features: Dictionary = {}


func _ready() -> void:
	_build_environment()
	_build_ground()
	_build_ramps()
	_build_steps()
	_build_mantle_obstacles()
	_build_pit()
	_build_wallrun_corridor()
	_build_slide_ramp()
	print("[test_level] 关卡就绪")


func _build_environment() -> void:
	var we := WorldEnvironment.new()
	var env := Environment.new()
	env.background_mode = Environment.BG_SKY
	var sky := Sky.new()
	var mat := ProceduralSkyMaterial.new()
	mat.sky_top_color = Color(0.18, 0.30, 0.52)
	mat.sky_horizon_color = Color(0.55, 0.62, 0.70)
	mat.ground_bottom_color = Color(0.14, 0.15, 0.17)
	mat.ground_horizon_color = Color(0.35, 0.36, 0.38)
	sky.sky_material = mat
	env.sky = sky
	env.ambient_light_source = Environment.AMBIENT_SOURCE_SKY
	env.ambient_light_energy = 0.6
	env.tonemap_mode = Environment.TONE_MAPPER_FILMIC
	we.environment = env
	add_child(we)

	var sun := DirectionalLight3D.new()
	sun.name = "Sun"
	sun.rotation_degrees = Vector3(-52, -125, 0)
	sun.light_energy = 1.15
	sun.shadow_enabled = true
	add_child(sun)


func _build_ground() -> void:
	# 地面用四块围出坑口（坑中心 (30,0)，6x6，开 6.4x6.4 洞）。
	var cx := 30.0
	var hole := 3.2
	var half := GROUND_SIZE * 0.5
	_add_ground_slab("Ground_N", -half, -hole, -half, half)
	_add_ground_slab("Ground_S", hole, half, -half, half)
	_add_ground_slab("Ground_W", -hole, hole, -half, cx - hole)
	_add_ground_slab("Ground_E", -hole, hole, cx + hole, half)
	features["ground"] = { "size": GROUND_SIZE, "top_y": 0.0 }


func _add_ground_slab(node_name: String, z0: float, z1: float, x0: float, x1: float) -> void:
	var sx := x1 - x0
	var sz := z1 - z0
	if sx <= 0.01 or sz <= 0.01:
		return
	add_child(_make_box(node_name,
		Vector3((x0 + x1) * 0.5, -0.5, (z0 + z1) * 0.5),
		Vector3(sx, 1.0, sz),
		Color(0.30, 0.36, 0.28)))


func _build_ramps() -> void:
	# 上坡：从 (-30,0,-4) 升到 (-30, 3.6, -18)（约 14°），顶端接平台
	var up_a := Vector3(-30, 0, -4)
	var up_b := Vector3(-30, 3.6, -18)
	add_child(_make_ramp_between("RampUp", up_a, up_b, 10.0, Color(0.42, 0.40, 0.30)))
	# 顶端平台（顶面 3.6）
	add_child(_make_platform("Plateau", Vector2(-30, -25), 12.0, 10.0, 3.6, Color(0.34, 0.38, 0.32)))
	features["ramp_up"] = { "bottom": up_a, "top": up_b, "top_y": 3.6 }

	# 下坡：从 (-30, 3.6, 4) 降到 (-30, 0, 18)
	var dn_a := Vector3(-30, 3.6, 4)
	var dn_b := Vector3(-30, 0, 18)
	add_child(_make_ramp_between("RampDown", dn_a, dn_b, 10.0, Color(0.42, 0.40, 0.30)))
	add_child(_make_platform("RampDownTop", Vector2(-30, 0), 12.0, 6.0, 3.6, Color(0.34, 0.38, 0.32)))
	features["ramp_down"] = { "top": dn_a, "bottom": dn_b }


func _build_steps() -> void:
	# 台阶：0.18 / 0.32 / 0.5 三级，位于 x 从 10 起、z=-20
	var base := Vector3(10, 0, -20)
	var heights := [0.18, 0.32, 0.5]
	for i in heights.size():
		var h: float = heights[i]
		add_child(_make_box("Step%d" % i,
			base + Vector3(i * 2.5, h * 0.5, 0),
			Vector3(2.5, h, 4.0),
			Color(0.40, 0.36, 0.34)))
	features["steps"] = { "base": base, "heights": heights }


func _build_mantle_obstacles() -> void:
	# 翻越墙（1.2m）：墙沿 z=0，面向 -Z 从 z>0 走近。
	# 墙体做**厚**（向 -Z 延伸），翻上去后顶面即平台，不会掉下去。
	add_child(_make_box("MantleWall", Vector3(10, 0.6, -1.375), Vector3(10.0, 1.2, 3.25), Color(0.55, 0.42, 0.32)))
	features["mantle_low"] = { "wall_pos": Vector3(10, 0.6, 0), "height": 1.2, "spawn": Vector3(10, 1.5, 3) }

	# 攀爬墙（2.6m）：墙沿 z=10，同样做厚
	add_child(_make_box("ClimbWall", Vector3(10, 1.3, 11.875), Vector3(10.0, 2.6, 3.25), Color(0.52, 0.40, 0.30)))
	features["climb_high"] = { "wall_pos": Vector3(10, 1.3, 10), "height": 2.6, "spawn": Vector3(10, 1.5, 16) }


func _build_pit() -> void:
	var cx := 30.0
	var cz := 0.0
	var half := 3.0
	var depth := 6.0
	var t := 0.5
	add_child(_make_box("PitFloor", Vector3(cx, -depth, cz), Vector3(half * 2, 0.5, half * 2), Color(0.22, 0.20, 0.18)))
	add_child(_make_box("PitWall_N", Vector3(cx, -depth * 0.5, cz - half - t * 0.5), Vector3(half * 2 + t * 2, depth, t), WALL_COLOR))
	add_child(_make_box("PitWall_S", Vector3(cx, -depth * 0.5, cz + half + t * 0.5), Vector3(half * 2 + t * 2, depth, t), WALL_COLOR))
	add_child(_make_box("PitWall_W", Vector3(cx - half - t * 0.5, -depth * 0.5, cz), Vector3(t, depth, half * 2), WALL_COLOR))
	add_child(_make_box("PitWall_E", Vector3(cx + half + t * 0.5, -depth * 0.5, cz), Vector3(t, depth, half * 2), WALL_COLOR))
	features["pit"] = { "center": Vector3(cx, 0, cz), "depth": depth, "spawn": Vector3(cx, 1.5, cz) }


func _build_wallrun_corridor() -> void:
	var z := -40.0
	var gap := 3.0
	var h := 8.0
	add_child(_make_box("Wallrun_L", Vector3(-gap * 0.5 - 0.25, h * 0.5, z), Vector3(0.5, h, 30.0), WALL_COLOR))
	add_child(_make_box("Wallrun_R", Vector3(gap * 0.5 + 0.25, h * 0.5, z), Vector3(0.5, h, 30.0), WALL_COLOR))
	# 入口起跳台（顶面 2.0）
	add_child(_make_platform("Wallrun_Ramp", Vector2(0, z + 18), gap, 4.0, 2.0, Color(0.38, 0.36, 0.34)))
	features["wallrun"] = { "z": z, "gap": gap, "spawn": Vector3(-1.2, 4.0, z + 14) }


func _build_slide_ramp() -> void:
	# 滑铲长坡：从 (30, 3.0, 18) 降到 (30, 0, 40)（约 8.5°）
	var a := Vector3(30, 3.0, 18)
	var b := Vector3(30, 0, 40)
	add_child(_make_ramp_between("SlideRamp", a, b, 10.0, Color(0.36, 0.40, 0.34)))
	features["slide_ramp"] = { "top": a, "bottom": b, "spawn": Vector3(30, 4.5, 16) }


# ───────────────────────── 构件工厂 ─────────────────────────

## 盒体（StaticBody3D + BoxMesh + BoxShape3D），pos 为中心。
func _make_box(node_name: String, pos: Vector3, size: Vector3, color: Color) -> StaticBody3D:
	var body := StaticBody3D.new()
	body.name = node_name
	body.position = pos

	var mesh_inst := MeshInstance3D.new()
	var mesh := BoxMesh.new()
	mesh.size = size
	mesh_inst.mesh = mesh
	mesh_inst.material_override = _mat(color)
	body.add_child(mesh_inst)

	var shape := CollisionShape3D.new()
	var box := BoxShape3D.new()
	box.size = size
	shape.shape = box
	body.add_child(shape)
	return body


## 顶面高度为 top_y、水平范围 (cx±sx/2, cz±sz/2) 的平台。
func _make_platform(node_name: String, center_xz: Vector2, sx: float, sz: float,
		top_y: float, color: Color) -> StaticBody3D:
	var h := top_y
	return _make_box(node_name,
		Vector3(center_xz.x, top_y - h * 0.5, center_xz.y),
		Vector3(sx, h, sz), color)

## 两点定坡：从 a 到 b 的斜面板（宽度 width）。几何直白，不易出错。
func _make_ramp_between(node_name: String, a: Vector3, b: Vector3,
		width: float, color: Color) -> StaticBody3D:
	var body := StaticBody3D.new()
	body.name = node_name

	var delta := b - a
	var length := delta.length()
	var thickness := 0.5
	var center := (a + b) * 0.5

	var mesh_inst := MeshInstance3D.new()
	var mesh := BoxMesh.new()
	mesh.size = Vector3(width, thickness, length)
	mesh_inst.mesh = mesh
	mesh_inst.material_override = _mat(color)
	body.add_child(mesh_inst)

	var shape := CollisionShape3D.new()
	var box := BoxShape3D.new()
	box.size = Vector3(width, thickness, length)
	shape.shape = box
	body.add_child(shape)

	# 朝向：-Z 指向 a→b 的水平方向，Y 轴倾斜
	var horiz := Vector3(delta.x, 0, delta.z)
	var hlen := horiz.length()
	var pitch := atan2(delta.y, hlen)
	var yaw := atan2(horiz.x, horiz.z)   # 让 -Z 对准 a→b
	body.rotation = Vector3(-pitch, yaw, 0)
	# 坡面下移半个厚度（板中心在坡面下方）
	var normal := Vector3(0, cos(pitch), 0)
	body.position = center - Vector3.UP * (thickness * 0.5)
	return body


func _mat(color: Color) -> StandardMaterial3D:
	var m := StandardMaterial3D.new()
	m.albedo_color = color
	m.roughness = 0.9
	m.metallic = 0.0
	return m
