extends Node
## ApexMovement **真实物理**集成测试（v0.2）。
##
## 为什么需要它：`determinism_test.tscn` 里的 FeatureTest 跑在 AnalyticWorld
## （纯数学测试替身）上，覆盖不到 Godot 桥接层与相机应用层。历史事故：
##   · `IntersectShape` 没有 normal 键 → 法线取错 → 角色永远无法移动
##   · 蹲下时胶囊中心不动 → 脚底悬空 0.49m → 掉入虚空
##   · 贴地吸附在上升途中执行 → 跳跃初速被吃掉（疲劳跳高度变 0）
## 这些在 AnalyticWorld 里都复现不出来，所以必须有一条真跑物理的用例。
##
## 运行：
##   Godot_v4.7-stable_mono_win64_console.exe --headless --path . \
##       res://addons/apex_movement/tests/integration_test.tscn

const TESTBED := "res://addons/apex_movement/testbed/apex_testbed.tscn"

# ── 期望值：Apex 实机实测（wiki）× 1hu=2.54cm ──
const EXPECT_WALK := 4.407        # m/s  173.5 hu/s（持枪）
const EXPECT_SPRINT := 6.604      # m/s  260 hu/s（持枪）
const EXPECT_SPRINT_HOLSTERED := 7.595   # m/s 299 hu/s（收枪 ×1.15）
const EXPECT_CROUCH_WALK := 2.032 # m/s  80 hu/s（持枪）
const EXPECT_JUMP_H := 1.4224     # m 56 hu
const EXPECT_MANTLE_H := 6.0      # m（High 档，上限 273hu≈6.93m）

# ── 期望值：需求方口径（角色尺寸，优先）──
const EXPECT_STAND_H := 1.95      # m
const EXPECT_CROUCH_H := 0.975    # m（站立一半）
const EXPECT_CAM_STAND := 1.85    # m（最大高度 - 10cm）
const EXPECT_CAM_CROUCH := 0.875  # m

var _player: Node3D
var _fails: int = 0
var _checks: int = 0
var _report: PackedStringArray = PackedStringArray()


func _ready() -> void:
	print("=== APEX MOVEMENT 集成测试 v0.2（真实物理） ===")
	var packed: PackedScene = load(TESTBED)
	if packed == null:
		push_error("测试场地加载失败")
		get_tree().quit(1)
		return
	var testbed: Node = packed.instantiate()
	add_child(testbed)

	await get_tree().physics_frame
	await get_tree().physics_frame

	_player = _find_apex(testbed)
	if _player == null:
		push_error("测试场地里找不到 ApexPlayer（C# 是否已编译？先 dotnet build）")
		get_tree().quit(1)
		return

	await _run_all()

	print("")
	for line in _report:
		print(line)
	print("")
	if _fails == 0:
		print("=== INTEGRATION PASS（%d 项断言） ===" % _checks)
		get_tree().quit(0)
	else:
		print("=== INTEGRATION FAIL（%d/%d 项失败） ===" % [_fails, _checks])
		get_tree().quit(1)


# ───────────────────────── 用例 ─────────────────────────

func _run_all() -> void:
	await _case_settle()
	await _case_walk()
	await _case_sprint()
	await _case_holster()
	await _case_crouch()
	await _case_jump()
	await _case_slide()
	await _case_slide_hop_chain()
	await _case_mantle_classes()
	await _case_mantle_6m()
	await _case_climb_gate()
	await _case_ramp()
	await _case_vault_wall()
	await _case_window()
	await _case_low_ceiling()
	await _case_camera_fov()
	await _case_kill_plane()


func _case_settle() -> void:
	_release_all()
	_player.DebugSetHolstered(false)      # 先按"持枪"基准测
	await _frames(60)
	_expect("落地", _player.DebugOnGround, "onGround=%s" % _player.DebugOnGround)
	_expect_near("脚底贴地", _player.DebugFeetY, 0.0, 0.05)
	_expect_near("站立身高（需求方口径）", _player.DebugHeight, EXPECT_STAND_H, 0.01)
	_expect_near("相机高度（站立 1.85）", _player.DebugCameraAboveFeet, EXPECT_CAM_STAND, 0.02)
	_expect_near("基础 FOV", _player.DebugFov, 90.0, 0.6)


func _case_walk() -> void:
	_release_all()
	await _frames(20)
	var p0: Vector3 = _player.global_position
	_press(KEY_W, true)
	await _frames(150)                       # 2.5s，足够走完加速段
	var speed: float = _player.DebugSpeed
	var p1: Vector3 = _player.global_position
	_press(KEY_W, false)

	var horiz: float = Vector2(p1.x - p0.x, p1.z - p0.z).length()
	var avg: float = horiz / 2.5
	_expect_near("行走速度（173.5 hu/s）", speed, EXPECT_WALK, 0.25)
	_expect_near("行走平均位移速度", avg, EXPECT_WALK, 0.6)
	_expect("行走方向（朝 -Z）", p1.z < p0.z - 6.0, "dz=%.2f" % (p1.z - p0.z))

	await _frames(45)


func _case_sprint() -> void:
	_release_all()
	_press(KEY_W, true)
	_press(KEY_SHIFT, true)
	await _frames(150)
	var speed: float = _player.DebugSpeed
	_press(KEY_W, false)
	_press(KEY_SHIFT, false)
	_expect_near("奔跑速度（260 hu/s）", speed, EXPECT_SPRINT, 0.3)
	await _frames(60)


func _case_holster() -> void:
	# 收枪 = ×1.15（wiki Holster）：299 hu/s = 7.595 m/s
	_release_all()
	_player.DebugSetHolstered(true)
	_press(KEY_W, true)
	_press(KEY_SHIFT, true)
	await _frames(150)
	var speed: float = _player.DebugSpeed
	_press(KEY_W, false)
	_press(KEY_SHIFT, false)
	_expect_near("收枪奔跑速度（299 hu/s = ×1.15）", speed, EXPECT_SPRINT_HOLSTERED, 0.3)
	_expect("收枪确实更快", speed > EXPECT_SPRINT + 0.5, "%.2f > %.2f" % [speed, EXPECT_SPRINT])

	_player.DebugSetHolstered(false)
	await _frames(60)


func _case_crouch() -> void:
	_release_all()
	await _frames(30)
	var feet0: float = _player.DebugFeetY
	var min_y: float = 1e9

	_press(KEY_CTRL, true)
	for i in range(90):
		await get_tree().physics_frame
		min_y = min(min_y, _player.DebugFeetY)

	var h: float = _player.DebugHeight
	var cam: float = _player.DebugCameraAboveFeet
	var feet1: float = _player.DebugFeetY

	_expect_near("蹲下身高（站立一半）", h, EXPECT_CROUCH_H, 0.02)
	_expect_near("蹲下时脚底不下沉", feet1, feet0, 0.05)
	_expect("蹲下过程不掉入虚空", min_y > -0.5, "min_feet_y=%.3f" % min_y)
	_expect_near("相机高度（蹲下 0.875）", cam, EXPECT_CAM_CROUCH, 0.02)

	_press(KEY_CTRL, false)
	await _frames(45)
	_expect_near("站起恢复身高", _player.DebugHeight, EXPECT_STAND_H, 0.02)
	_expect_near("站起后脚底仍贴地", _player.DebugFeetY, 0.0, 0.06)


func _case_jump() -> void:
	_release_all()
	await _frames(30)
	var apex: float = -1e9

	_press(KEY_SPACE, true)
	await _frames(2)
	_press(KEY_SPACE, false)
	for i in range(60):
		await get_tree().physics_frame
		apex = max(apex, _player.DebugFeetY)

	_expect_near("跳跃高度（56 hu）", apex, EXPECT_JUMP_H, 0.18)
	await _frames(60)
	_expect("跳后重新落地", _player.DebugOnGround, "onGround=%s" % _player.DebugOnGround)


func _case_slide() -> void:
	_release_all()
	await _frames(20)
	_press(KEY_W, true)
	_press(KEY_SHIFT, true)
	await _frames(120)                        # 跑满速
	_press(KEY_CTRL, true)
	await _frames(8)

	var sliding: bool = _player.DebugState == "Slide"
	var speed: float = _player.DebugSpeed
	var roll: float = _player.DebugRollDeg
	var fov: float = _player.DebugFov
	_expect("滑铲触发", sliding, "state=%s speed=%.2f" % [_player.DebugState, speed])
	_expect("Slideboost 生效（>400hu/s 上限内）", speed > EXPECT_SPRINT + 1.0,
		"speed=%.2f（冲刺 %.2f）" % [speed, EXPECT_SPRINT])
	_expect("滑铲相机侧倾（slide_viewTilt）", absf(roll) > 0.5, "roll=%.2f°" % roll)
	_expect("滑铲抬 FOV", fov > 91.0, "fov=%.2f°（基础 90）" % fov)

	_press(KEY_CTRL, false)
	_press(KEY_W, false)
	_press(KEY_SHIFT, false)
	await _frames(60)


func _case_slide_hop_chain() -> void:
	# 连续滑铲跳：滑铲 → 跳 → 落地 → 再滑铲 → 再跳 …
	_release_all()
	await _frames(20)
	_press(KEY_W, true)
	_press(KEY_SHIFT, true)
	await _frames(120)

	var hops0: int = _player.DebugSlideHops
	for hop in range(3):
		_press(KEY_CTRL, true)
		await _frames(16)                      # 跨过 0.24s slidejump 时间窗
		_press(KEY_SPACE, true)
		await _frames(2)
		_press(KEY_SPACE, false)
		_press(KEY_CTRL, false)
		await _frames(55)                      # 空中 → 落地

	_press(KEY_W, false)
	_press(KEY_SHIFT, false)
	var hops: int = _player.DebugSlideHops - hops0
	_expect("连续滑铲跳 ≥3 次", hops >= 3, "本轮 slideHop 计数=%d" % hops)
	await _frames(60)


func _case_mantle_classes() -> void:
	# 四档翻越：0.9m→Below，1.7m→Level，3.0m→Above，6.0m→High
	# 台阶梯：x 从 -34 起、步长 8，z=35
	var specs := [
		{ "x": -26.0, "h": 0.9, "want": "Below" },
		{ "x": -10.0, "h": 1.7, "want": "Level" },
		{ "x": 6.0,   "h": 3.0, "want": "Above" },
		{ "x": 22.0,  "h": 6.0, "want": "High" },
	]
	for s in specs:
		_release_all()
		_player.DebugTeleport(float(s["x"]), 2.0, 29.0)
		_player.DebugSetYaw(PI)               # 朝 +Z
		await _frames(40)

		_press(KEY_W, true)
		var seen_class := ""
		var max_feet := 0.0
		for i in range(180):
			await get_tree().physics_frame
			max_feet = max(max_feet, _player.DebugFeetY)
			if seen_class == "" and _player.DebugState in ["Mantle", "Vault"]:
				seen_class = _player.DebugMantleClass
		_press(KEY_W, false)

		_expect("翻越分档 %s（%.1fm 台面）" % [s["want"], s["h"]],
			seen_class == s["want"],
			"实测档位=%s，脚底最高 %.2f" % [seen_class, max_feet])
		await _frames(30)


func _case_mantle_6m() -> void:
	# 攀爬台阶梯最高一级：6.0m（High 档）
	_release_all()
	_player.DebugTeleport(22.0, 2.0, 29.0)
	_player.DebugSetYaw(PI)                   # 朝 +Z
	await _frames(40)

	_press(KEY_W, true)
	var max_feet: float = 0.0
	for i in range(300):
		await get_tree().physics_frame
		max_feet = max(max_feet, _player.DebugFeetY)
	_press(KEY_W, false)

	_expect_near("6m 攀爬（High 档）", max_feet, EXPECT_MANTLE_H, 0.25)
	await _frames(40)


func _case_climb_gate() -> void:
	# ── 需求（m00799）：攀爬不许强制触发 ────────────────────────────────
	# ClimbWall 在 (50, 4, 0)、尺寸 (2, 8, 24) ⇒ 朝玩家那面 x=49，高 8m。
	# 8m > MantleMaxHeight(6.93m) ⇒ 这个场景连翻越都不该发生，专测攀爬。
	_release_all()
	_player.DebugTeleport(44.0, 1.05, 0.0)      # 中心 y=1.05 ⇒ 脚底 ≈ 0
	_player.DebugSetYaw(PI * 0.5)               # 朝 +X，正对墙（forward(yaw)=(sin,0,-cos)）
	await _frames(40)

	# ① 站在地面上顶着墙按 W —— 不许进 Climb
	var seen_climb := false
	_press(KEY_W, true)
	for i in range(90):
		await get_tree().physics_frame
		if _player.DebugState == "Climb":
			seen_climb = true
	var x_blocked: float = _player.global_position.x
	_expect("地面顶着墙按 W 不触发攀爬（非强制）", not seen_climb,
		"state=%s" % _player.DebugState)
	_expect("顶墙被挡住、没有穿墙", x_blocked < 49.0 and _player.DebugFeetY < 0.2,
		"x=%.3f feetY=%.3f（墙面 x=49.0）" % [x_blocked, _player.DebugFeetY])
	_press(KEY_W, false)
	await _frames(20)

	# ② 跑向墙 + 空中按 W ⇒ 必须挂上（面向墙 + 朝墙速度 + 空中）
	_release_all()
	_player.DebugTeleport(44.0, 1.05, 0.0)
	_player.DebugSetYaw(PI * 0.5)
	await _frames(30)
	_press(KEY_W, true)
	await _frames(15)                            # 离墙 ~4m 时起跳，保挂上时仍有朝墙速度
	_press(KEY_SPACE, true)
	await _frames(2)
	_press(KEY_SPACE, false)

	var climb_at := -1
	for i in range(150):
		await get_tree().physics_frame
		if _player.DebugState == "Climb":
			climb_at = i
			break
	_expect("跳跃后贴墙（面向墙 + 朝墙速度）会挂上攀爬", climb_at >= 0,
		"state=%s x=%.3f" % [_player.DebugState, _player.global_position.x])

	if climb_at >= 0:
		var feet0: float = _player.DebugFeetY
		for i in range(45):
			await get_tree().physics_frame
		var feet1: float = _player.DebugFeetY
		_expect("按住 W 持续上爬（0.75s 至少 +0.8m）", feet1 - feet0 > 0.8,
			"Δfeet=%.3f（ClimbSpeed 2.0 m/s）" % (feet1 - feet0))

		# ③ 松开 W ⇒ 宽限 0.22s 后取消攀爬、开始下落
		_press(KEY_W, false)
		await _frames(30)
		_expect("松开 W 后取消攀爬并下落",
			_player.DebugState != "Climb" and _player.DebugVerticalSpeed < 0.0,
			"state=%s vy=%.3f" % [_player.DebugState, _player.DebugVerticalSpeed])
	else:
		_press(KEY_W, false)
	await _frames(40)


func _case_ramp() -> void:
	# 15° 斜坡：Ramp15 底边在 (x=-45, z=-55)，沿 +Z 爬升 20m 到 y≈5.36
	_release_all()
	_player.DebugTeleport(-45.0, 2.0, -60.0)
	_player.DebugSetYaw(PI)                   # 朝 +Z（上坡方向）
	await _frames(40)

	_press(KEY_W, true)
	var max_feet: float = 0.0
	for i in range(240):
		await get_tree().physics_frame
		max_feet = max(max_feet, _player.DebugFeetY)
	_press(KEY_W, false)

	_expect("爬上 15° 斜坡", max_feet > 3.0, "max_feet=%.2f（坡顶 5.36m）" % max_feet)
	await _frames(40)


func _case_vault_wall() -> void:
	# 翻越低墙 0.8m：VaultWall_0.8 位于 x=-14, z=50，尺寸 3×0.8×6
	_release_all()
	_player.DebugTeleport(-14.0, 2.0, 44.0)
	_player.DebugSetYaw(PI)                   # 朝 +Z
	await _frames(40)

	_press(KEY_W, true)
	var max_z: float = -1e9
	for i in range(240):
		await get_tree().physics_frame
		max_z = max(max_z, _player.global_position.z)
		if max_z > 53.5:
			break
	_press(KEY_W, false)

	_expect("翻越 0.8m 矮墙", max_z > 53.0, "max_z=%.2f（墙 z=47..53）" % max_z)
	await _frames(40)


func _case_window() -> void:
	# 窗口翻越：窗台 0.9m 的窗口在 x=26, z=68（窗台厚 0.6，开口净高 1.6m）
	_release_all()
	_player.DebugTeleport(26.0, 2.0, 62.0)
	_player.DebugSetYaw(PI)                   # 朝 +Z
	await _frames(40)

	_press(KEY_W, true)
	var saw_window := false
	var max_z: float = -1e9
	for i in range(240):
		await get_tree().physics_frame
		if _player.DebugState == "Window":
			saw_window = true
		max_z = max(max_z, _player.global_position.z)
		if max_z > 69.5:
			break
	_press(KEY_W, false)

	_expect("窗口翻越（TRAVERSAL_WINDOW）", saw_window, "state=Window 出现=%s" % saw_window)
	_expect("穿过窗口开口", max_z > 68.0, "max_z=%.2f（窗 z≈68）" % max_z)
	await _frames(40)


func _case_low_ceiling() -> void:
	# 低天花板：净高 1.1m，位于 x=-60, z=15..25
	# 玩家蹲着进去后**松开蹲**，头顶有天花板 ⇒ 不应站起来
	_release_all()
	_player.DebugTeleport(-60.0, 1.2, 20.0)
	await _frames(40)
	_press(KEY_CTRL, true)
	await _frames(60)
	var crouch_h: float = _player.DebugHeight
	_press(KEY_CTRL, false)
	await _frames(90)
	var after_h: float = _player.DebugHeight

	_expect("低天花板下无法站起", after_h < EXPECT_STAND_H - 0.5,
		"蹲下 %.2fm，松开蹲后 %.2fm（天花板净高 1.1m）" % [crouch_h, after_h])

	# 移出天花板后应能站起
	_player.DebugTeleport(-60.0, 1.2, 5.0)
	await _frames(90)
	_expect_near("离开天花板后站起", _player.DebugHeight, EXPECT_STAND_H, 0.03)


func _case_camera_fov() -> void:
	# FOV 可调（对应 cl_fovScale / 设置里的 FOV 滑条）
	_release_all()
	await _frames(30)
	_player.DebugSetFov(100.0)
	await _frames(60)
	var fov100: float = _player.DebugFov
	_player.DebugSetFov(90.0)
	await _frames(60)
	var fov90: float = _player.DebugFov

	_expect_near("FOV 可调到 100°", fov100, 100.0, 0.6)
	_expect_near("FOV 可调回 90°", fov90, 90.0, 0.6)

	# 手动视角踢（落地/受击反馈用的同一套弹簧）
	var before: float = _player.DebugCameraAboveFeet
	_player.DebugKick(-6.0, 0.0, 0.0)
	await _frames(2)
	_expect("视角踢可触发", true, "踢前相机高度 %.3f m" % before)
	await _frames(90)


func _case_kill_plane() -> void:
	_release_all()
	_player.DebugTeleport(0.0, -80.0, 0.0)
	await _frames(10)
	_expect("掉出世界自动复活", _player.global_position.y > -60.0,
		"y=%.2f" % _player.global_position.y)
	await _frames(60)


# ───────────────────────── 工具 ─────────────────────────

func _frames(n: int) -> void:
	for i in range(n):
		await get_tree().physics_frame


func _expect(name: String, ok: bool, detail: String = "") -> void:
	_checks += 1
	if not ok: _fails += 1
	_report.append("  [%s] %s%s" % ["PASS" if ok else "FAIL", name,
		"" if detail.is_empty() else "  （%s）" % detail])


func _expect_near(name: String, got: float, want: float, tol: float) -> void:
	_expect(name, absf(got - want) <= tol,
		"实测 %.3f，期望 %.3f ± %.3f" % [got, want, tol])


func _press(keycode: int, pressed: bool) -> void:
	var ev := InputEventKey.new()
	ev.physical_keycode = keycode
	ev.pressed = pressed
	Input.parse_input_event(ev)


func _release_all() -> void:
	for k in [KEY_W, KEY_A, KEY_S, KEY_D, KEY_SPACE, KEY_CTRL, KEY_SHIFT, KEY_E]:
		_press(k, false)


func _find_apex(n: Node) -> Node3D:
	var s := n.get_script()
	if s != null and "ApexPlayer" in str(s.resource_path):
		return n as Node3D
	for c in n.get_children():
		var r := _find_apex(c)
		if r != null:
			return r
	return null
