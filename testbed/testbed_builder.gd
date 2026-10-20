extends Node3D
## ApexMovement 测试场地（v0.2）—— 程序化生成，无需手工摆场景。
##
## 场地内容：
##   · 大平地（200×200m）                —— 基础移动 / 加速 / 摩擦 / 收枪加速
##   · 三条斜坡（15° / 30° / 45°）        —— 爬坡 / 下坡贴地 / 坡上滑铲 / 可行走坡角
##   · 攀爬台阶梯（4 档全覆盖）           —— Below / Level / Above / High 四档翻越
##        0.6 / 0.9  → Below（跨过去）
##        1.3 / 1.7  → Level
##        2.4 / 3.0  → Above
##        4.5 / 6.0  → High（接近 273hu≈6.93m 上限）
##   · 翻越低墙（0.4 / 0.8 / 1.0m）       —— Below 档的"翻越"外观
##   · 窗口翻越（3 个开口）               —— TRAVERSAL_WINDOW：0.9m 窗台 + 2.5m 窗楣
##   · 贴墙爬（8m 高墙）                  —— Climb 状态：attach offset / end boost
##   · 低天花板通道                       —— 蹲下才能进 / 站起被头顶挡住
##   · 滑铲长道（80m）                    —— 滑铲距离 / 滑铲跳 / 连续滑铲跳
##   · 打滑体积（Slip，v0.3 新增）        —— C_TriggerSlipSphere：走进球里被往外推
##   · 落差平台（3 / 6 / 10 / 22m）       —— 落地分档 Low/Medium/High/Hard + Fallstun
##
## 全部几何为 StaticBody3D（静态），保证物理查询在固定 tick 下确定性。

const GROUND_SIZE := 200.0

var _player: Node3D

## 材质缓存（同色复用，减少 draw call）
var _mat_cache: Dictionary = {}


func _ready() -> void:
	# 输入动作自注册（脚本在 app 层，避免 C# class_name 扫描时序问题）
	var input_map_script: Script = load("res://addons/apex_movement/player/apex_input_map.gd")
	if input_map_script:
		input_map_script.ensure_all()

	_build_environment()
	_build_ground()
	_build_ramps()
	_build_mantle_steps()
	_build_vault_walls()
	_build_window_frames()
	_build_climb_wall()
	_build_low_ceiling()
	_build_slide_lane()
	_build_slip_zone()
	_build_drop_platforms()
	_spawn_player()


# ───────────────────────── 环境 ─────────────────────────

func _build_environment() -> void:
	var sun := DirectionalLight3D.new()
	sun.name = "Sun"
	sun.rotation_degrees = Vector3(-50, -35, 0)
	sun.light_energy = 1.2
	sun.shadow_enabled = true
	add_child(sun)

	var env := Environment.new()
	env.background_mode = Environment.BG_SKY
	var sky := Sky.new()
	sky.sky_material = ProceduralSkyMaterial.new()
	env.sky = sky
	env.ambient_light_source = Environment.AMBIENT_SOURCE_SKY
	env.ambient_light_energy = 0.8
	env.tonemap_mode = Environment.TONE_MAPPER_FILMIC

	var we := WorldEnvironment.new()
	we.name = "WorldEnvironment"
	we.environment = env
	add_child(we)


# ───────────────────────── 几何工具 ─────────────────────────

func _get_material(color: Color) -> StandardMaterial3D:
	var key := color.to_html()
	if _mat_cache.has(key):
		return _mat_cache[key]
	var m := StandardMaterial3D.new()
	m.albedo_color = color
	m.roughness = 0.85
	m.metallic = 0.0
	_mat_cache[key] = m
	return m


## 生成一个静态长方体（碰撞 + 网格）。
func _box(node_name: String, pos: Vector3, size: Vector3, color: Color,
		rot_deg: Vector3 = Vector3.ZERO) -> StaticBody3D:
	var body := StaticBody3D.new()
	body.name = node_name
	body.position = pos
	body.rotation_degrees = rot_deg

	var shape := CollisionShape3D.new()
	var box_shape := BoxShape3D.new()
	box_shape.size = size
	shape.shape = box_shape
	body.add_child(shape)

	var mesh := MeshInstance3D.new()
	var box_mesh := BoxMesh.new()
	box_mesh.size = size
	mesh.mesh = box_mesh
	mesh.material_override = _get_material(color)
	body.add_child(mesh)

	add_child(body)
	return body


## 生成一个**纯装饰**长方体（只有网格，没有碰撞体）。
##
## 为什么需要它：地面网格线 / 刻度 / 标签这类东西如果带碰撞，会变成
## 1~2cm 高的"台阶"，把角色卡住（实测：走两步撞一条网格线，速度掉到 3.1 m/s）。
## 静态碰撞只留给真正参与移动判定的几何。
func _deco_box(node_name: String, pos: Vector3, size: Vector3, color: Color) -> MeshInstance3D:
	var mesh := MeshInstance3D.new()
	mesh.name = node_name
	mesh.position = pos
	var box_mesh := BoxMesh.new()
	box_mesh.size = size
	mesh.mesh = box_mesh
	mesh.material_override = _get_material(color)
	# 装饰几何不参与碰撞，也不参与物理查询
	mesh.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	add_child(mesh)
	return mesh


## 生成一条斜坡（沿 +Z 方向上升）。
func _ramp(node_name: String, base: Vector3, length: float, width: float,
		height: float, color: Color) -> void:
	var angle := atan2(height, length)
	var slope_len := sqrt(length * length + height * height)
	var center := base + Vector3(0, height * 0.5, length * 0.5)
	_box(node_name, center, Vector3(width, 0.4, slope_len), color,
		Vector3(-rad_to_deg(angle), 0, 0))


# ───────────────────────── 场地构建 ─────────────────────────

func _build_ground() -> void:
	_box("Ground", Vector3(0, -0.5, 0), Vector3(GROUND_SIZE, 1.0, GROUND_SIZE),
		Color(0.32, 0.36, 0.30))

	# 网格参考线（每 10m 一条），便于目测速度与距离
	for i in range(-10, 11):
		if i == 0:
			continue
		var line_color := Color(0.45, 0.50, 0.42)
		_deco_box("GridX_%d" % i, Vector3(i * 10.0, 0.02, 0), Vector3(0.15, 0.02, GROUND_SIZE), line_color)
		_deco_box("GridZ_%d" % i, Vector3(0, 0.02, i * 10.0), Vector3(GROUND_SIZE, 0.02, 0.15), line_color)


func _build_ramps() -> void:
	# 三条不同角度的斜坡，验证爬坡与滑铲。
	# 15° 是 Apex 的"可行走坡"内（上限 arccos(0.7)=45.57°），45° 接近上限。
	var specs := [
		{ "name": "Ramp15", "x": -45.0, "deg": 15.0, "color": Color(0.55, 0.45, 0.30) },
		{ "name": "Ramp30", "x": -25.0, "deg": 30.0, "color": Color(0.60, 0.40, 0.25) },
		{ "name": "Ramp45", "x": -5.0,  "deg": 45.0, "color": Color(0.65, 0.35, 0.20) },
	]

	const RAMP_LEN := 20.0
	const RAMP_W := 6.0
	const BASE_Z := -55.0

	for s in specs:
		var h: float = RAMP_LEN * tan(deg_to_rad(s["deg"]))
		var base := Vector3(s["x"], 0.0, BASE_Z)
		_ramp(s["name"], base, RAMP_LEN, RAMP_W, h, s["color"])

		# 顶部平台：贴在坡顶，与坡顶等高
		_box("%sTop" % s["name"],
			Vector3(s["x"], h + 0.2, BASE_Z + RAMP_LEN + 2.0),
			Vector3(RAMP_W, 0.4, 5.0),
			Color(0.5, 0.5, 0.5))

		# 底部台阶标记（便于目测起点）
		_deco_box("%sBase" % s["name"],
			Vector3(s["x"], 0.03, BASE_Z - 1.0),
			Vector3(RAMP_W, 0.06, 1.5),
			Color(0.8, 0.8, 0.3))


func _build_mantle_steps() -> void:
	# 攀爬台阶梯：覆盖 Apex 的 4 档翻越分类（对应二进制
	# mantleDurationBelow / Level / Above / High）
	#   ≤0.975m → Below（跨过去，旧称 Vault）
	#   ≤1.95m  → Level
	#   ≤3.45m  → Above
	#   其余    → High（上限 273hu ≈ 6.93m）
	var specs := [
		{ "h": 0.6, "cls": "Below" },
		{ "h": 0.9, "cls": "Below" },
		{ "h": 1.3, "cls": "Level" },
		{ "h": 1.7, "cls": "Level" },
		{ "h": 2.4, "cls": "Above" },
		{ "h": 3.0, "cls": "Above" },
		{ "h": 4.5, "cls": "High" },
		{ "h": 6.0, "cls": "High" },
	]
	var colors := {
		"Below": Color(0.35, 0.45, 0.65),
		"Level": Color(0.30, 0.55, 0.60),
		"Above": Color(0.35, 0.50, 0.35),
		"High": Color(0.55, 0.35, 0.55),
	}
	var x := -34.0
	for s in specs:
		var h: float = s["h"]
		var tag: String = s["cls"]
		_box("MantleStep_%.1f" % h, Vector3(x, h * 0.5, 35.0), Vector3(4, h, 4),
			colors[tag])
		# 顶部黄色标记，便于在截图里识别台面高度
		_deco_box("MantleLabel_%.1f" % h, Vector3(x, h + 0.06, 35.0), Vector3(1.0, 0.12, 1.0),
			Color(1.0, 0.9, 0.2))
		x += 8.0


func _build_vault_walls() -> void:
	# 低墙：Below 档的"翻越"外观。独立分区，互不干扰。
	var heights := [0.4, 0.8, 1.0]
	var x := -20.0
	for h in heights:
		_box("VaultWall_%.1f" % h, Vector3(x, h * 0.5, 50.0), Vector3(3, h, 6),
			Color(0.75, 0.35, 0.35))
		x += 6.0


func _build_window_frames() -> void:
	# 窗口翻越（TRAVERSAL_WINDOW）：窗台 + 窗楣，中间留 1.6m 净高开口。
	# 对应二进制字段 traversal_window_{forward_offset, sideways_offset,
	# hand_vertical_offset, finish_angle, duration, enable}。
	# 三个不同窗台高度：0.6 / 0.9 / 1.2m
	var specs := [
		{ "x": 20.0, "sill": 0.6 },
		{ "x": 26.0, "sill": 0.9 },
		{ "x": 32.0, "sill": 1.2 },
	]
	const Z := 68.0
	const WALL_W := 4.0
	const THICK := 0.6

	for s in specs:
		var x: float = s["x"]
		var sill: float = s["sill"]
		var lintel_bottom := sill + 1.6

		# 窗台（下半段墙）
		_box("WindowSill_%.1f" % sill,
			Vector3(x, sill * 0.5, Z), Vector3(WALL_W, sill, THICK),
			Color(0.62, 0.55, 0.40))
		# 窗楣（上半段墙），一直延伸到 4.5m
		var lintel_h := 4.5 - lintel_bottom
		_box("WindowLintel_%.1f" % sill,
			Vector3(x, lintel_bottom + lintel_h * 0.5, Z),
			Vector3(WALL_W, lintel_h, THICK),
			Color(0.62, 0.55, 0.40))
		# 开口两侧的短翼，让"开口宽度"有实际边界（否则是无限宽）
		_box("WindowWingL_%.1f" % sill,
			Vector3(x - 2.6, 1.6, Z), Vector3(1.2, 3.2, THICK),
			Color(0.55, 0.48, 0.36))
		_box("WindowWingR_%.1f" % sill,
			Vector3(x + 2.6, 1.6, Z), Vector3(1.2, 3.2, THICK),
			Color(0.55, 0.48, 0.36))


func _build_climb_wall() -> void:
	# 贴墙爬（Climb）：一面 8m 高、24m 长的墙。
	# Apex 上限 273hu ≈ 6.93m（climb space cutoff），另有 100hu≈2.54m 的 attach offset。
	_box("ClimbWall", Vector3(50, 4.0, 0), Vector3(2.0, 8.0, 24.0),
		Color(0.45, 0.42, 0.55))
	# 每 1m 一道水平刻度，便于目测爬升高度
	for i in range(1, 8):
		_deco_box("ClimbMark_%d" % i, Vector3(49.05, float(i), 0),
			Vector3(0.1, 0.06, 24.0), Color(1.0, 0.95, 0.5))


func _build_low_ceiling() -> void:
	# 低天花板通道：净高 1.1m —— 站着(1.95m)进不去，蹲着(0.975m)可以。
	# 用于验证「空中/地面蹲下收缩方向」与「头顶检测阻止站起」。
	var x := -60.0
	var z := 20.0
	# 两侧墙
	_box("LowCeilWallL", Vector3(x - 1.5, 1.2, z), Vector3(1.0, 2.4, 10.0),
		Color(0.40, 0.40, 0.45))
	_box("LowCeilWallR", Vector3(x + 1.5, 1.2, z), Vector3(1.0, 2.4, 10.0),
		Color(0.40, 0.40, 0.45))
	# 天花板（底面在 1.1m）
	_box("LowCeilRoof", Vector3(x, 1.1 + 0.25, z), Vector3(2.0, 0.5, 10.0),
		Color(0.50, 0.45, 0.40))


func _build_slide_lane() -> void:
	# 滑铲长道：80m 无障碍，测量滑铲距离与连续滑铲跳。
	_box("SlideLane", Vector3(-70, 0.02, 0), Vector3(12, 0.04, 80),
		Color(0.25, 0.30, 0.42))
	# 每 10m 一个刻度（从起点算）
	for i in range(1, 8):
		_deco_box("SlideMark_%d" % i, Vector3(-70, 0.06, -40.0 + i * 10.0), Vector3(12, 0.05, 0.4),
			Color(0.95, 0.95, 0.6))


## 打滑体积（Slip）：对应逆向里的 C_TriggerSlipSphere。
##
## 实现要点（别改成"地面材质"）：它是**世界里的体积**，玩家走进去会被往外推。
## 节点的 meta 就是配置：
##   slip_speed      推出的目标水平速度（m/s，Apex 默认 400hu/s ≈ 10.16，这里取 8）
##   slip_accel      加速到目标速度的加速度（m/s²）
##   slip_dir        推出方向；留空 = 从体积中心往外推（m_defaultSlipDirection）
##   slip_lock_plane 是否把速度锁在水平面（贴地滑道 = true）
## 必须在 "slip_volume" 组里 —— ApexPlayer 按组名收集（见 RefreshSlipVolumes）。
func _build_slip_zone() -> void:
	const X := 80.0
	const Z := 0.0
	const RADIUS := 7.0

	var vol := Node3D.new()
	vol.name = "SlipZone"
	vol.position = Vector3(X, RADIUS * 0.5, Z)
	vol.add_to_group("slip_volume")
	vol.set_meta("slip_speed", 8.0)
	vol.set_meta("slip_accel", 40.0)
	vol.set_meta("slip_dir", Vector3(0, 0, 1))     # 往 +Z 推
	vol.set_meta("slip_lock_plane", true)

	# 体积本身（球）——只用碰撞形状，不参与移动扫掠
	var shape := CollisionShape3D.new()
	var sphere := SphereShape3D.new()
	sphere.radius = RADIUS
	shape.shape = sphere
	vol.add_child(shape)

	# 可视化：地面上的一片半透明"水洼"，**不要**画整颗球。
	# 画球壳会导致第一人称截图整屏泛绿（相机在球内，看到的都是半透明面），
	# 既看不清场地也看不出角色在动。功能判定用的是上面的 SphereShape3D，
	# 视觉只需要标出"踩进这个圈就会被推"。
	var disc := MeshInstance3D.new()
	var disc_mesh := CylinderMesh.new()
	disc_mesh.top_radius = RADIUS
	disc_mesh.bottom_radius = RADIUS
	disc_mesh.height = 0.04
	disc_mesh.radial_segments = 48
	disc.position = Vector3(X, 0.03, Z)
	disc.mesh = disc_mesh
	var mat := StandardMaterial3D.new()
	mat.albedo_color = Color(0.25, 0.85, 0.45, 0.45)
	mat.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	mat.cull_mode = BaseMaterial3D.CULL_DISABLED
	disc.material_override = mat
	disc.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	add_child(disc)

	add_child(vol)

	# 地面上标一圈参考色，方便看清"推进方向"
	_deco_box("SlipMarkIn", Vector3(X, 0.03, Z - RADIUS - 2.0),
		Vector3(6, 0.06, 1.0), Color(0.95, 0.95, 0.4))
	_deco_box("SlipMarkOut", Vector3(X, 0.03, Z + RADIUS + 2.0),
		Vector3(6, 0.06, 1.0), Color(0.95, 0.4, 0.4))


func _build_drop_platforms() -> void:
	# 落差平台：验证落地分档与 Fallstun。
	#   landDistLow/Medium/High + hardFallDist（逆向字段）
	#   3m → Medium，6m → High，10m → High，22m → Hard（>20.32m 会触发 Fallstun）
	var drops := [3.0, 6.0, 10.0, 22.0]
	var x := 20.0
	for h in drops:
		_box("DropPlatform_%.0f" % h,
			Vector3(x, h * 0.5, 55.0),
			Vector3(5, h, 5),
			Color(0.4, 0.55, 0.4))
		# 顶面亮色标记
		_deco_box("DropTop_%.0f" % h, Vector3(x, h + 0.06, 55.0), Vector3(1.5, 0.12, 1.5),
			Color(0.9, 1.0, 0.7))
		x += 8.0


# ───────────────────────── 玩家 ─────────────────────────

func _spawn_player() -> void:
	# 用 C# 脚本路径加载，避免依赖 class_name 的扫描时序
	var player_script: Script = load("res://addons/apex_movement/player/ApexPlayer.cs")
	if player_script == null:
		push_error("ApexPlayer.cs 未能加载（C# 是否已编译？先运行 dotnet build）")
		return

	_player = Node3D.new()
	_player.set_script(player_script)
	_player.name = "Player"
	_player.position = Vector3(0, 1.5, 20)
	add_child(_player)
