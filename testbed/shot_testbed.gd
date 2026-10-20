extends Node
## 测试场地截图工具（v0.2）：加载场地 → 多视角出图 → 退出。
##
## 用法（**必须有窗口/真实渲染**，headless 拍不出画面）：
##   godot --path . --rendering-driver vulkan --resolution 1920x1080 \
##       res://addons/apex_movement/testbed/shot_testbed.tscn
##
## 出图分两类：
##   1. 自由机位（_shots）—— 俯视/侧视看场地构建对不对
##   2. 玩家第一人称（_player_shots）—— 用 ApexPlayer 自己的相机，
##      覆盖 **FOV 调节 / 走路摇晃 / 滑铲侧倾 / 攀爬相机滞后** 四个 v0.2 新增项，
##      每张图的 HUD 都会打印当时的 fov / roll / state，作为可读的视觉证据。
##
## 输出到 user://（Godot 用户目录），路径会打印在控制台。

const TESTBED := "res://addons/apex_movement/testbed/apex_testbed.tscn"

var _cam: Camera3D
var _player: Node3D
## 第三人称截图用的"替代身体"（ApexPlayer 是第一人称，本身没有可见网格）。
var _proxy: Node3D

## 机位按 v0.2 场地布局设定：
##   斜坡区     x∈[-48,-2]   z∈[-55,-35]
##   攀爬阶梯   x∈[-34,22]   z=35   （4 档：Below/Level/Above/High）
##   翻越低墙   x∈[-20,-8]   z=50
##   窗口框架   x∈[20,32]    z=68
##   贴墙爬     x=50         z∈[-12,12]
##   低天花板   x=-60        z∈[15,25]
##   滑铲长道   x=-70        z∈[-40,40]
##   打滑体积   x=80         z∈[-7,7]     （v0.3 新增）
##   落差平台   x∈[20,44]    z=55
var _shots := [
	{ "name": "overview",     "pos": Vector3(60, 90, 130), "look": Vector3(-10, 8, 10),  "fov": 70.0 },
	{ "name": "ramps",        "pos": Vector3(-25, 30, 5),  "look": Vector3(-25, 8, -45), "fov": 60.0 },
	{ "name": "mantle_steps", "pos": Vector3(-6, 18, 76),  "look": Vector3(-6, 3, 35),   "fov": 72.0 },
	{ "name": "vault_walls",  "pos": Vector3(-14, 10, 70), "look": Vector3(-14, 1, 50),  "fov": 60.0 },
	{ "name": "window_frames","pos": Vector3(26, 9, 58),   "look": Vector3(26, 2, 68),   "fov": 62.0 },
	{ "name": "climb_wall",   "pos": Vector3(40, 12, 22),  "look": Vector3(50, 4, 0),    "fov": 62.0 },
	{ "name": "slide_lane",   "pos": Vector3(-70, 22, 55), "look": Vector3(-70, 0, 0),   "fov": 65.0 },
	{ "name": "slip_zone",    "pos": Vector3(96, 20, -26), "look": Vector3(80, 2, 4),    "fov": 62.0 },
	{ "name": "drop_plats",   "pos": Vector3(32, 26, 82),  "look": Vector3(32, 8, 55),   "fov": 60.0 },
]


func _ready() -> void:
	var scene: Node = load(TESTBED).instantiate()
	add_child(scene)

	# 等几帧让场景构建与着色器编译完成
	await get_tree().process_frame
	await get_tree().process_frame
	await get_tree().create_timer(0.4).timeout

	_player = _find_apex(scene)

	_cam = Camera3D.new()
	_cam.name = "ShotCam"
	_cam.keep_aspect = Camera3D.KEEP_WIDTH       # fov 按**水平角**解释
	add_child(_cam)

	_proxy = _build_proxy()
	add_child(_proxy)
	_proxy.visible = false

	for shot in _shots:
		_cam.global_position = shot["pos"]
		_cam.look_at(shot["look"], Vector3.UP)
		_cam.fov = shot["fov"]
		_cam.current = true

		await get_tree().process_frame
		await get_tree().process_frame
		await get_tree().create_timer(0.15).timeout

		await _save("shot_%s" % shot["name"])

	await _player_shots()

	print("SHOTS DONE")
	get_tree().quit()


## 用玩家自己的相机出图，覆盖 v0.2 的四个相机特性。
func _player_shots() -> void:
	var cams := get_tree().get_nodes_in_group("player_cameras")
	if cams.is_empty():
		print("PLAYER SHOTS: 没找到 player_cameras 组，跳过")
		return
	if _player == null:
		print("PLAYER SHOTS: 没找到 ApexPlayer，跳过")
		return

	var pcam: Camera3D = cams[0]
	_cam.current = false
	pcam.current = true

	# ── ① 站立静止：FOV 90，无 bob ──
	await _settle()
	await _save("shot_player_fp")

	# ── ② FOV 110：直观对比视野宽度（对应 cl_fovScale 滑条）──
	_player.DebugSetFov(110.0)
	await _wait(0.6)
	await _save("shot_player_fov110")

	# ── ③ FOV 70：另一侧对比 ──
	_player.DebugSetFov(70.0)
	await _wait(0.6)
	await _save("shot_player_fov70")

	_player.DebugSetFov(90.0)
	await _wait(0.5)

	# ── ④ 走路：走路摇晃（bob）+ 轻微冲刺侧倾 ──
	_player.DebugTeleport(-70.0, 1.2, 30.0)
	_player.DebugSetYaw(PI)                       # 朝 +Z
	await _settle()
	_press_key(KEY_W, true)
	await _wait_frames(90)
	await _save("shot_player_walking")
	_press_key(KEY_W, false)
	await _wait(0.4)

	# ── ⑤ 滑铲：相机侧倾 + FOV 抬升 ──
	_player.DebugTeleport(-70.0, 1.2, 30.0)
	_player.DebugSetYaw(PI)
	await _settle()
	_press_key(KEY_W, true)
	_press_key(KEY_SHIFT, true)
	await _wait_frames(150)                       # 跑满速
	_press_key(KEY_CTRL, true)
	await _wait_frames(12)
	await _save("shot_player_slide")
	_press_key(KEY_CTRL, false)
	_press_key(KEY_W, false)
	_press_key(KEY_SHIFT, false)
	await _wait(0.4)

	# ── ⑥ 攀爬途中：相机滞后 + 高度上限 ──
	# 注意取景时机：Grab 阶段相机贴着 6m 墙面（实测距墙 1.30m、眼高 2.83m），
	# 画面会被墙面填满（紫色 = High 档台阶本色），看不出任何相机行为。
	# 因此等到 **Pull 阶段、视线已越过台沿** 再取图 —— 此时能看到台面与地平线，
	# 而 HUD 里 xBlend 仍 > 0，正是"相机滞后"的可读证据。
	_player.DebugTeleport(22.0, 2.0, 29.0)        # 6m 高台前
	_player.DebugSetYaw(PI)
	await _settle()
	_press_key(KEY_W, true)
	for i in 400:
		await get_tree().physics_frame
		if _player.DebugState in ["Mantle", "Vault", "Window"]:
			break
	for i in 400:                                  # 等视线越过台沿（眼高 > 台面 6m）
		await get_tree().physics_frame
		var feet: float = _player.global_position.y - 0.975   # 节点原点在胶囊中心
		if feet > 4.30:                            # 眼高 = 脚 + 1.85 > 6.15 → 已过台沿
			break
		if _player.DebugState not in ["Mantle", "Vault", "Window"]:
			break
	await _save("shot_player_mantle")
	_press_key(KEY_W, false)

	# ── ⑦⑧⑨ 攀爬 / 扒墙 / 上墙（v0.3 新增）──────────────────────
	# 用 3.0m 台阶（> AutoMantleMaxHeight 2.032 ⇒ 地面贴墙不会自动翻越，只能走攀爬）：台阶够矮，攀爬时眼睛（脚底 +1.85m）已在台沿之上，画面里能看到
	# 台面与地平线；又能验证 Hang → Mantle 的完整链路。
	# 硬证据在 HUD：state=Climb 且 vy=+2.00（只有按住 W 才上爬）。
	_player.DebugTeleport(6.0, 1.2, 29.5)
	_player.DebugSetYaw(PI)                       # 朝 +Z，正对 3.0m 台阶（Above 档）
	await _settle()
	_press_key(KEY_W, true)
	await _wait_frames(12)                        # 留出朝墙速度再起跳
	_press_key(KEY_SPACE, true)
	await _wait_frames(2)
	_press_key(KEY_SPACE, false)

	# ⑦ 攀爬中：一进 Climb 就取图（这个台阶只够爬 ~25 tick，等太久会进 Hang）
	for i in 240:
		await get_tree().physics_frame
		if _player.DebugState == "Climb":
			break
	await _wait_frames(4)
	await _save("shot_player_climb")

	# ⑧ 扒墙：一进 Hang 立刻松开 W（不松开会当 tick 就转 Mantle），下一帧取图
	for i in 240:
		await get_tree().physics_frame
		if _player.DebugState in ["Hang", "Mantle"]:
			break
	_press_key(KEY_W, false)
	await get_tree().process_frame
	await _save("shot_player_hang")

	# ⑨ 挂边按 W 上墙：站到台面上（Hang → Mantle → Ground）
	_press_key(KEY_W, true)
	for i in 240:
		await get_tree().physics_frame
		if _player.DebugState == "Ground" and _player.DebugOnGround and _player.DebugFeetY > 2.5:
			break
	_press_key(KEY_W, false)                      # 立刻松手，别跑出 4m 台面的远端
	await _wait_frames(15)
	print("CLIMB TOP feet=%.2f state=%s" % [_player.DebugFeetY, _player.DebugState])
	await _save("shot_player_climb_top")

	# ── ⑩ 打滑体积（v0.3 新增）──────────────────────────────────
	# 滑区是**世界体积**（C_TriggerSlipSphere），不是地面材质：站进去就被往外推。
	# 硬证据在 HUD：state=Slip，speed 被拉到 slip_speed(8.00)，方向 +Z。
	_player.DebugTeleport(80.0, 1.2, -6.5)        # 从近端进圈，留出 1 秒的滑行距离
	_player.DebugSetYaw(PI)                       # 朝 +Z，与推出方向一致
	await _settle()
	# 等真正被加速到目标速度再取图，否则 HUD 上是半路的 3.00 m/s
	for i in 240:
		await get_tree().physics_frame
		if _player.DebugState == "Slip" and _player.DebugSpeed > 7.95:
			break
	# ★ 再多滑满 0.5 秒（30 tick）再截。
	#   Vulkan 下 get_texture().get_image() 抓到的可能是落后若干帧的缓冲，
	#   曾出现"控制台读到 speed=8.00、图上却是 3.00（同一 tick）"的错位。
	#   速度 8.00 是终值会一直保持，只要在这里多待一会儿，落后的帧也一定是 8.00。
	#   注意别待太久：8 m/s × 1 s = 8 m，圈半径只有 7，会滑出去变回 Ground。
	await _wait_frames(30)
	print("SLIP state=%s speed=%.2f onGround=%s feet=%.2f" %
		[_player.DebugState, _player.DebugSpeed, _player.DebugOnGround, _player.DebugFeetY])
	await _save("shot_player_slip")
	print("SLIP HUD: " + _player.DebugHudLine.replace("\n", " | "))

	# ── ⑪ 扒墙 / 悬垂（v0.3 新增）────────────────────────────────
	# 挂在攀爬墙（x=49 那面）的**近端角落**，这样画面右半边是空地，
	# 不会整屏都是墙色；硬证据在 HUD：state=WallHang + grip。
	# 注意 y 必须 ≤ 8 - AutoMantleMaxHeight(2.032)，否则上方探针打空、
	# automantle_disable_hang 会拒绝挂住。
	_player.DebugTeleport(48.1, 5.0, 11.4)
	# ★ 视角必须在**挂住之前**设好：DebugSetYaw/DebugSetPitch 内部是 core.Reset()，
	#   会把状态打回 Air、清掉 _wallHangNormal —— 挂住之后再调就等于把挂点毁了。
	_player.DebugSetYaw(PI * 0.5)                 # 朝 +X，正对墙面 x=49
	await _wait_frames(2)
	_press_key(KEY_CTRL, true)
	for i in 90:
		await get_tree().physics_frame
		if _player.DebugState == "WallHang":
			break
	await _wait_frames(10)
	print("WALLHANG state=%s speed=%.2f vy=%.2f feet=%.2f pos=%s" %
		[_player.DebugState, _player.DebugSpeed, _player.DebugVerticalSpeed,
		 _player.DebugFeetY, str(_player.global_position)])

	# ★ 这一张必须用**第三人称**机位。
	#   第一人称挂在墙上时，脸离墙面只有 0.43 m（WallHangReach 0.9 减去半身厚），
	#   而 WallHangMaxViewAngle=45.57° 又限制视线不能偏离墙面法线超过 45.57°，
	#   ⇒ 无论怎么调视角，画面 100% 是墙面纯色，看不出角色挂在哪里、挂得多高。
	#   所以这里放一个替代身体的胶囊、把机位挪到侧后方；HUD 是 CanvasLayer，
	#   换机位照样在，HUD 上的 state=WallHang / grip 仍是同一时刻的物理真值。
	_proxy.global_position = _player.global_position
	_proxy.rotation = Vector3(0, PI * 0.5, 0)     # 胶囊的"鼻子"朝 +X（贴墙方向）
	_proxy.visible = true
	_cam.global_position = Vector3(44.2, 5.6, 6.4)
	_cam.look_at(_player.global_position + Vector3(0.4, 0.1, 0), Vector3.UP)
	_cam.fov = 58.0
	_cam.current = true
	await _wait_frames(4)
	await _save("shot_player_wallhang")
	print("WALLHANG HUD: " + _player.DebugHudLine.replace("\n", " | "))
	_proxy.visible = false
	_press_key(KEY_CTRL, false)
	await _wait_frames(10)


# ───────────────────────── 工具 ─────────────────────────

func _settle() -> void:
	for k in [KEY_W, KEY_A, KEY_S, KEY_D, KEY_SPACE, KEY_CTRL, KEY_SHIFT]:
		_press_key(k, false)
	await _wait_frames(40)


func _wait(seconds: float) -> void:
	await get_tree().create_timer(seconds).timeout


func _wait_frames(n: int) -> void:
	for i in n:
		await get_tree().physics_frame
	await get_tree().process_frame
	await get_tree().process_frame


func _save(name: String) -> void:
	# ★ 抓帧前先冻结时间（实测必要）。
	#   Vulkan 下 get_viewport().get_texture().get_image() 拿到的可能是十几帧之前的帧
	#   （呈现队列落后），而 HUD 上的 speed/tick 每帧都在变 ⇒ 直接抓会截到
	#   "半路"的旧数字：实测见过 HUD 写 speed=8.00、PNG 上却是 speed=3.00。
	#   Engine.time_scale=0 后物理 delta 归零（tick 不再推进）、渲染照常，
	#   多等几帧让呈现队列追平，抓到的就一定是"冻结那一刻"的画面。
	Engine.time_scale = 0.0
	for _i in 24:
		await get_tree().process_frame
	await RenderingServer.frame_post_draw
	var before: String = _player.DebugHudLine
	var img: Image = get_viewport().get_texture().get_image()
	var after: String = _player.DebugHudLine
	Engine.time_scale = 1.0
	var path := "user://%s.png" % name
	img.save_png(path)
	print("SHOT %s -> %s (%dx%d)" % [name, ProjectSettings.globalize_path(path),
		img.get_width(), img.get_height()])
	print("  HUD@capture: " + before.replace("\n", " | "))
	print("  HUD@after  : " + after.replace("\n", " | "))

func _press_key(keycode: Key, pressed: bool) -> void:
	var ev := InputEventKey.new()
	ev.physical_keycode = keycode
	ev.pressed = pressed
	Input.parse_input_event(ev)


func _find_apex(n: Node) -> Node3D:
	var s := n.get_script()
	if s != null and "ApexPlayer" in str(s.resource_path):
		return n as Node3D
	for c in n.get_children():
		var r := _find_apex(c)
		if r != null:
			return r
	return null

## 第三人称截图用的替代身体。
##
## ApexPlayer 是第一人称实现，场景里**没有可见网格** —— 相机一挪到第三人称，
## 画面里就只有环境。这个胶囊只用于截图：位置/朝向由调用方按玩家真实状态同步，
## 不参与任何移动或碰撞计算（没有碰撞体、不在物理层里）。
func _build_proxy() -> Node3D:
	var root := Node3D.new()
	root.name = "ShotProxy"

	var body := MeshInstance3D.new()
	var capsule := CapsuleMesh.new()
	capsule.radius = 0.34
	capsule.height = 1.70
	body.mesh = capsule
	body.material_override = _proxy_mat(Color(0.95, 0.55, 0.15))
	root.add_child(body)

	var head := MeshInstance3D.new()
	var sphere := SphereMesh.new()
	sphere.radius = 0.22
	sphere.height = 0.44
	head.mesh = sphere
	head.position = Vector3(0, 0.95, 0)
	head.material_override = _proxy_mat(Color(0.98, 0.80, 0.62))
	root.add_child(head)

	# 朝向标记：放在局部 +Z。调用方用 rotation.y = PI/2 让 +Z 指向世界 +X（贴墙方向）。
	var nose := MeshInstance3D.new()
	var box := BoxMesh.new()
	box.size = Vector3(0.18, 0.18, 0.34)
	nose.mesh = box
	nose.position = Vector3(0, 0.95, 0.26)
	nose.material_override = _proxy_mat(Color(0.25, 0.85, 0.95))
	root.add_child(nose)

	return root


func _proxy_mat(c: Color) -> StandardMaterial3D:
	var m := StandardMaterial3D.new()
	m.albedo_color = c
	m.roughness = 0.6
	return m

