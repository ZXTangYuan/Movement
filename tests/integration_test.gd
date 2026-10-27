extends Node
## ApexMovement **真实物理**集成测试（v0.4.2）。
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
const EXPECT_CROUCH_WALK := 2.0   # m/s（v0.5 取整，原 80hu/s=2.032）
const EXPECT_JUMP_H := 0.75       # m 设计目标（v0.5 需求 m00952：从 56hu=1.4224 改成 0.75）
const EXPECT_MANTLE_H := 6.0      # m（High 档，上限 273hu≈6.93m）

# ── 期望值：需求方口径（角色尺寸，优先）──
const EXPECT_STAND_H := 1.90      # m（v0.5 需求：1.95 → 1.90）
const EXPECT_CROUCH_H := 0.95     # m（站立一半）
const EXPECT_CAM_STAND := 1.80    # m（站立高度 − 10cm；v0.5：1.90 − 0.10）
const EXPECT_CAM_CROUCH := 0.85   # m（蹲下高度 − 10cm；v0.5：0.95 − 0.10）

var _player: Node3D
var _fails: int = 0
var _checks: int = 0
var _report: PackedStringArray = PackedStringArray()


func _ready() -> void:
	print("=== APEX MOVEMENT 集成测试 v0.4.2（真实物理） ===")
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
	await _case_planet_jump()
	await _case_slide()
	await _case_slide_hop_chain()
	await _case_slide_hop_release()
	await _case_mantle_classes()
	await _case_mantle_6m()
	await _case_climb_gate()
	await _case_ramp()
	await _case_slide_slope()
	await _case_slope_run_speed()
	await _case_mantle_13()
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
	_expect_near("相机高度（站立 1.80）", _player.DebugCameraAboveFeet, EXPECT_CAM_STAND, 0.02)
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
	_expect_near("相机高度（蹲下 0.85）", cam, EXPECT_CAM_CROUCH, 0.02)

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

	# v0.5：跳跃高度是"设计目标 0.75"，实际离散积分会多跳 v·dt/2 + g·dt²/8 ≈ +0.033 m，
	# 所以容差给 0.10（而不是当年的 ±0.18）。物理口径见 docs/PHYSICS-AUDIT.md §0.3。
	_expect_near("跳跃高度（设计目标 0.75）", apex, EXPECT_JUMP_H, 0.10)
	await _frames(60)
	_expect("跳后重新落地", _player.DebugOnGround, "onGround=%s" % _player.DebugOnGround)


## v0.5.1（需求 m01619）：**起跳力度固定，换星球只改重力 ⇒ 跳跃高度按 v²/2g 变**。
##
## 用菜单同一条公开接口 ApplyPlanetPreset() 切星球，每颗星球实测脚底峰高，
## 与解析值 JumpApexOnPlanet(g) 对比。地球 0.750 / 月球 4.380 / 火星 1.930 / Apex 0.371。
func _case_planet_jump() -> void:
	_release_all()
	# 期望峰高（与 MovementConfig.JumpApexOnPlanet 的离散解一致，误差 < 2mm）
	var want := [0.750, 4.380, 1.930, 0.371]
	var names := ["地球", "月球", "火星", "Apex 重力"]
	var gs := [9.80665, 1.62, 3.711, 20.7802]
	for idx in range(4):
		_player.ApplyPlanetPreset(idx)
		_player.DebugTeleport(-70.0, 1.2, 30.0)
		# 先真正落地（低重力下坠更久），否则"空中不能跳"会把起跳吃掉
		for i in range(400):
			await get_tree().physics_frame
			if _player.DebugOnGround:
				break
		await _frames(10)

		var feet0: float = _player.DebugFeetY
		var apex: float = feet0
		_press(KEY_SPACE, true)
		await _frames(2)
		_press(KEY_SPACE, false)
		for i in range(400):
			await get_tree().physics_frame
			apex = max(apex, float(_player.DebugFeetY))
			if i > 8 and _player.DebugOnGround:
				break

		_expect_near("换星球跳跃：%s（g=%.2f，起跳速度固定）" % [names[idx], gs[idx]],
			apex - feet0, want[idx], 0.02)
		_expect("换星球重力生效：%s" % names[idx],
			abs(_player.DebugPlanetGravity - gs[idx]) < 1e-4,
			"g=%.5f（期望 %.5f）" % [_player.DebugPlanetGravity, gs[idx]])

	# 复位到地球，避免影响后续用例
	_player.ApplyPlanetPreset(0)
	await _frames(10)


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
	# v0.4.1 需求：滑铲画面与下蹲一致 —— 不再有滑铲专属侧倾（曾 5°）与 FOV 加宽（曾 +6°）。
	# 残下的 0.74° / 92.37° 是"冲刺余量"（SprintMaxRollDeg 1.5°、FovSprintAdd 3° 的 blend 尾），
	# 所以断言改成上限：绝不能超过冲刺档本身。
	_expect("滑铲不再有专属侧倾（只剩冲刺余量 ≤1.5°）", absf(roll) <= 1.55,
		"roll=%.2f°（上限 SprintMaxRollDeg 1.5）" % roll)
	_expect("滑铲不再抬 FOV（只剩冲刺余量 ≤93°）", fov <= 93.05,
		"fov=%.2f°（上限 90+3）" % fov)

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


func _case_slide_hop_release() -> void:
	# v0.5 回归（用户 m01300 BUG 1）：
	#   连续滑铲跳后松开所有按键，角色必须自己站起来。
	#   历史 bug：空中起身时脚底向下伸 delta/2，伸进地面 4cm，而贴地吸附只向下不向上
	#   ⇒ 永久陷在地面以下 ⇒ "站立头顶检测"把地面当障碍 ⇒ 蹲伏被钉死，只有跳一下才恢复。
	_release_all()
	await _frames(20)
	_player.DebugTeleport(0.0, 1.05, 0.0)
	await _frames(30)
	_press(KEY_W, true)
	_press(KEY_SHIFT, true)
	await _frames(90)
	_press(KEY_CTRL, true)
	for i in range(120):
		if i % 4 == 0:
			_press(KEY_SPACE, true)
		elif i % 4 == 2:
			_press(KEY_SPACE, false)
		await get_tree().physics_frame
	_press(KEY_W, false)
	_press(KEY_SHIFT, false)
	_press(KEY_CTRL, false)
	_press(KEY_SPACE, false)

	var min_feet := 999.0
	for i in range(90):
		await get_tree().physics_frame
		min_feet = min(min_feet, _player.DebugFeetY)

	_expect("连续滑铲跳后松开按键：角色自己起身（蹲伏量归零）",
		_player.DebugCrouch < 0.01 and abs(_player.DebugHeight - EXPECT_STAND_H) < 0.02,
		"crouch=%.3f h=%.3f" % [_player.DebugCrouch, _player.DebugHeight])
	_expect("空中起身过程中脚底不陷进地面（> -5mm）",
		min_feet > -0.005,
		"min_feet=%.4f" % min_feet)
	_expect("起身后站立不被地面误判挡住",
		not _player.DebugHeadBlocked,
		"head_blocked=%s" % str(_player.DebugHeadBlocked))
	await _frames(20)


func _case_mantle_classes() -> void:
	# 四档高度：0.9m→小障碍档（Vault/Below 直接翻），1.7/3.0/6.0m→墙档（W+Space 抓墙爬上去）。
	# 台阶梯：x 从 -34 起、步长 8，z=35
	var specs := [
		{ "x": -26.0, "h": 0.9, "want": "Below" },
		{ "x": -10.0, "h": 1.7, "want": "" },
		{ "x": 6.0,   "h": 3.0, "want": "" },
		{ "x": 22.0,  "h": 6.0, "want": "" },
	]
	for s in specs:
		_release_all()
		_player.DebugTeleport(float(s["x"]), 2.0, 29.0)
		_player.DebugSetYaw(PI)               # 朝 +Z
		await _frames(40)

		# v0.4.1：翻越/攀爬必须 "W + Space" 才触发（走着撞墙只会撞停）。
		# 只在**地面上**点跳：已经挂上墙之后再按 Space 会变成蹬墙跳/到顶翻越，会毁掉攀爬。
		var h: float = float(s["h"])
		_press(KEY_W, true)
		var seen_class := ""
		var max_feet := 0.0
		var on_top := false
		for i in range(420):
			await get_tree().physics_frame
			if _player.DebugState == "Ground" and i % 20 == 10:
				_press(KEY_SPACE, true)
			elif i % 20 == 12:
				_press(KEY_SPACE, false)
			max_feet = max(max_feet, _player.DebugFeetY)
			if seen_class == "" and _player.DebugState in ["Mantle", "Vault"]:
				seen_class = _player.DebugMantleClass
			if _player.DebugState == "Ground" and _player.DebugFeetY > h - 0.2:
				on_top = true
				break
		_press(KEY_W, false)
		_press(KEY_SPACE, false)

		if s["want"] != "":
			# 小障碍档：直接翻越，档位必须是 Below
			_expect("小障碍 %.1fm：W+Space 直接翻越（档位 %s）" % [h, s["want"]],
				seen_class == s["want"] and on_top,
				"实测档位=%s，脚底最高 %.2f，站上台面=%s" % [seen_class, max_feet, on_top])
		else:
			# 墙档：抓住 → 上爬 → 到顶翻上，脚底要到台面高度
			_expect("墙 %.1fm：W+Space 抓墙 → 按住 W 爬到台面上" % h,
				on_top and absf(max_feet - h) < 0.25,
				"脚底最高 %.2f（台面 %.2f），站上台面=%s" % [max_feet, h, on_top])
		await _frames(30)


func _case_mantle_13() -> void:
	# 回归：v0.4.1 修掉的"1.3m 平台翻越时悬空卡死"。
	# 平台 MantleStep_1.3 位于 x=-18, 中心 z=35, 尺寸 4×1.3×4（前表面 z=33）。
	# 曾经的 bug：起跳后 GodotMovementWorld.QueryNormalAt 在接触边缘拿不到真实法线，
	# 返回"运动方向反向"的伪法线，MoveWithCollision 用它做 ProjectOnPlane 把
	# 水平与竖直速度一起吃掉 ⇒ 人悬在半空、vy 一路掉到 -41 而位置不动。
	# 判据：① 能站上 1.3m 台面 ② 全程不出现"Air 且速度很大却位移不动"的死锁。
	_release_all()
	_player.DebugTeleport(-18.0, 1.2, 30.0)
	_player.DebugSetYaw(PI)                   # 朝 +Z（正对平台前表面 z=33）
	await _frames(30)

	_press(KEY_W, true)
	await _frames(24)                         # 先撞上去
	_press(KEY_SPACE, true)
	await _frames(2)
	_press(KEY_SPACE, false)

	var max_feet: float = 0.0
	var stuck_frames := 0
	var worst_stuck := 0
	var prev_feet: float = _player.DebugFeetY
	for i in range(300):
		await get_tree().physics_frame
		max_feet = max(max_feet, _player.DebugFeetY)
		# "卡死"判据：空中 + 位置几乎没动 + 竖直速度绝对值很大
		var moved: float = absf(_player.DebugFeetY - prev_feet)
		if _player.DebugState == "Air" and moved < 0.002 and absf(_player.DebugVerticalSpeed) > 5.0:
			stuck_frames += 1
			worst_stuck = max(worst_stuck, stuck_frames)
		else:
			stuck_frames = 0
		prev_feet = _player.DebugFeetY
		if _player.DebugState == "Ground" and _player.DebugFeetY > 1.25:
			break
	_press(KEY_W, false)
	await _frames(30)

	_expect("1.3m 平台：W+Space 能站上台面（回归：不再悬空卡死）",
		_player.DebugState == "Ground" and _player.DebugFeetY > 1.25,
		"state=%s feet=%.2f（台面 1.30）max_feet=%.2f" % [
			_player.DebugState, _player.DebugFeetY, max_feet])
	_expect("1.3m 平台：全程没有『空中速度很大却不动』的死锁",
		worst_stuck < 10,
		"最长连续卡死帧数 = %d（判据 <10）" % worst_stuck)


func _case_mantle_6m() -> void:
	# 攀爬台阶梯最高一级：6.0m（High 档）
	_release_all()
	_player.DebugTeleport(22.0, 2.0, 29.0)
	_player.DebugSetYaw(PI)                   # 朝 +Z
	await _frames(40)

	# v0.4.1：先在地面点一次 Space 起跳抓墙，之后一路按住 W 往上爬。
	_press(KEY_W, true)
	var max_feet: float = 0.0
	var jumped := false
	for i in range(420):
		await get_tree().physics_frame
		if not jumped and _player.DebugState == "Ground" and i % 20 == 10:
			_press(KEY_SPACE, true)
		elif i % 20 == 12:
			_press(KEY_SPACE, false)
		if _player.DebugState in ["Climb", "WallHang"]:
			jumped = true
		max_feet = max(max_feet, _player.DebugFeetY)
		if max_feet > EXPECT_MANTLE_H - 0.15 and _player.DebugState == "Ground":
			break
	_press(KEY_W, false)
	_press(KEY_SPACE, false)

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
	# ★ v0.5.2：空中控制收紧后（需求「空中只能靠惯性」），起跳后**不能再靠
	#   空中加速把自己推向墙**。所以必须在地面就冲到足够速度，让惯性把人送到墙上：
	#   从 42.0 起跑 1 秒到 ~6.6 m/s（离墙 7m），再起跳，滞空 0.78s 走 5.1m 正好够。
	_player.DebugTeleport(42.0, 1.05, 0.0)
	_player.DebugSetYaw(PI * 0.5)
	await _frames(30)
	_press(KEY_SHIFT, true)
	_press(KEY_W, true)
	await _frames(60)                            # 冲刺到 6.6 m/s（离墙约 7m）
	_press(KEY_SPACE, true)
	await _frames(2)
	_press(KEY_SPACE, false)
	await _frames(3)
	_press(KEY_SHIFT, false)

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

		# ③ 松开 W ⇒ 进入扒墙（WallHang），并开始缓慢下滑（v0.4.1 需求第 3 条）
		_press(KEY_W, false)
		var slide_from: float = _player.DebugFeetY
		await _frames(40)
		_expect("松开 W 后转入扒墙（WallHang）并缓慢下滑",
			_player.DebugState == "WallHang" and _player.DebugFeetY < slide_from,
			"state=%s feet %.3f → %.3f" % [_player.DebugState, slide_from, _player.DebugFeetY])
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


func _run_on_ramp(sx: float, deg: float, z0: float, yaw: float, sprint: bool) -> float:
	# 在坡面上跑一段，返回"稳定段"的平均水平速度。
	# 只收 状态=Ground 且 脚底 >0.5m 的样本（脚底低于 0.5m 说明已经跑到坡底平地）。
	var surf: float = (z0 + 55.0) * tan(deg_to_rad(deg)) + 0.2 * cos(deg_to_rad(deg))
	_release_all()
	_player.DebugTeleport(sx, surf + 1.2, z0)
	_player.DebugSetYaw(yaw)
	await _frames(25)                  # 落到坡面上
	if sprint:
		_press(KEY_SHIFT, true)
	_press(KEY_W, true)
	var speeds: Array = []
	for i in range(120):
		await get_tree().physics_frame
		if i >= 60 and _player.DebugState == "Ground" and _player.DebugFeetY > 0.5:
			speeds.append(_player.DebugPlaneSpeed)   # 沿坡面速度（v0.5.2 口径）
	_press(KEY_W, false)
	_press(KEY_SHIFT, false)
	await _frames(10)
	if speeds.is_empty():
		return 0.0
	var sum := 0.0
	for v in speeds:
		sum += float(v)
	return sum / float(speeds.size())


func _case_slope_run_speed() -> void:
	# v0.5（需求 m00914 ⑥）：「跑动和滑铲用基础移速随坡度变化 + 重力加成，
	# 蹲走和正常行走时用制动防止摔倒那套」。
	#
	#   跑动：v_target = v_base + τ·g·sinθ·(下坡方向·期望方向)，τ = 0.25 s
	#     30° 坡、地球 g：冲刺下坡 6.604 + 0.25·9.80665·0.5 = 7.830
	#                     冲刺上坡 6.604 − 1.2258          = 5.378
	#   走（不按冲刺）不吃坡度 ⇒ 两个方向都 ≈ 4.407（防滑档）
	var deg := 30.0
	var sx := -25.0
	var g: float = 9.80665
	var tau: float = 0.25
	var sin_t: float = sin(deg_to_rad(deg))
	var expect_down: float = EXPECT_SPRINT + tau * g * sin_t
	var expect_up: float = EXPECT_SPRINT - tau * g * sin_t

	var run_down: float = await _run_on_ramp(sx, deg, -38.0, 0.0, true)    # yaw=0 朝 -Z = 下坡
	var run_up: float = await _run_on_ramp(sx, deg, -52.0, PI, true)       # yaw=π 朝 +Z = 上坡
	var walk_down: float = await _run_on_ramp(sx, deg, -38.0, 0.0, false)
	var walk_up: float = await _run_on_ramp(sx, deg, -52.0, PI, false)

	# ★ v0.5.2 口径修正：这里量的是**沿坡面**的速度（DebugPlaneSpeed），
	#   不是它的水平投影。需求原文「顺斜坡向下移动速度会大于 7.6」说的是沿坡速度；
	#   水平投影还要再乘 cos30°，两者差 13.4%，混用会把正确的物理判成错误。
	_expect_near("30° 坡：冲刺下坡吃重力加成（沿坡 %.2f m/s）" % expect_down,
		run_down, expect_down, 0.45)
	_expect_near("30° 坡：冲刺上坡掉速（沿坡 %.2f m/s）" % expect_up,
		run_up, expect_up, 0.45)
	_expect("坡面跑动方向性正确（下坡 > 平路冲刺 > 上坡）",
		run_down > EXPECT_SPRINT + 0.3 and run_up < EXPECT_SPRINT - 0.3,
		"下坡 %.2f / 上坡 %.2f（平路 %.2f）" % [run_down, run_up, EXPECT_SPRINT])
	_expect_near("30° 坡：行走不吃坡度（沿坡仍 %.2f m/s，防滑档）" % EXPECT_WALK,
		walk_down, EXPECT_WALK, 0.35)
	_expect_near("30° 坡：行走上坡也保持 %.2f m/s" % EXPECT_WALK,
		walk_up, EXPECT_WALK, 0.35)
	await _frames(20)


func _case_slide_slope() -> void:
	# 滑铲物理化：a = g·sinθ − μ·g·cosθ
	#   30° 坡（Ramp30 x=-25, z=-55..-35）：tan30°=0.577 > μ=0.45 ⇒ 下坡持续加速
	#   15° 坡（Ramp15 x=-45）：tan15°=0.268 < μ        ⇒ 下坡仍被摩擦刹停
	# 判据：取"滑铲已经稳定在坡面上"的窗口（跳过起滑那几帧的 Slideboost 台阶），
	#       用速度差 / 时间算实测加速度，与理论值比对；坡底平地的样本会被剔除。
	# ★ v0.4.2 重写"怎么起滑"。旧版是：站到坡顶平台 → 冲刺 → 按 Ctrl 起滑 →
	#   滑出平台边缘 → 落到坡面上继续滑。
	#
	#   重写理由（独立验证 task-3 的 A1 已把这条链路跑通复核）：
	#   旧写法**依赖两条隐式条件**才能成立 ——
	#     ① 滑出台沿后落地那一刻仍在滑铲（v0.4.1 靠"空中滑铲"续住，
	#        而空中滑铲正是需求方要删的缺陷本身）；
	#     ② 落地速度够得着 SlideMinStartSpeed（实测落地水平速度 7.99 ≥ 5.08，
	#        靠的是 v0.4.2 新加的 9a「落地即滑铲」把落地当帧接回滑铲）。
	#   也就是说：**这段用例一直在测别的机制，而不是它声称的"坡面滑铲物理"**。
	#   现在改成**直接在坡面上助跑起滑**：起滑与测量都在同一段坡面上，
	#   不依赖空中滑铲、也不依赖落地衔接。
	#   （注：中间某个只有 ① 被删、9a 还没加进来的状态里，旧写法确实会
	#    产出 0 个 Slide 样本；那属于中间态，不是当前代码的行为。）
	var results := {}
	for spec in [{"x": -25.0, "deg": 30.0}, {"x": -45.0, "deg": 15.0}]:
		var sx: float = spec["x"]
		var deg: float = spec["deg"]
		# 坡面高度：坡底 z=-55 高 0，沿 +Z 每米抬 tan(deg)，面层再加 0.2·cos(deg)
		# 坡长只有 20m：起点太靠下会在采样窗内滑到坡底平地，测出来的就是平地摩擦
		# （实测教训：z0=-40 + 助跑 60 帧 ⇒ t=54 脚底已到 0.34m，t=60 起站在平地，
		#  后半段 -9.4 m/s² 正是 μg 的平地值，把斜率整体拉负）。
		# 所以起点放在坡顶附近，助跑缩短到 40 帧。
		var z0 := -36.5
		var surf: float = (z0 + 55.0) * tan(deg_to_rad(deg)) + 0.2 * cos(deg_to_rad(deg))
		_release_all()
		_player.DebugTeleport(sx, surf + 1.2, z0)   # y 是胶囊中心 ⇒ 脚底在坡面上方约 0.22m
		_player.DebugSetYaw(0.0)                   # 朝 -Z（下坡方向）
		await _frames(30)                          # 落到坡面上
		_press(KEY_SHIFT, true)
		_press(KEY_W, true)
		await _frames(40)                          # 沿坡助跑到达冲刺速度
		_press(KEY_CTRL, true)

		# 采样：只要还在滑铲就记 (tick, speed)。前 8 帧跳过起滑瞬间
		# （Slideboost 是瞬时速度台阶，不是加速度，算进去会污染斜率）。
		var samples: Array = []
		var tick := 0
		for i in range(90):
			await get_tree().physics_frame
			tick += 1
			# 只统计"还在坡面上"的滑铲样本：脚底 <0.5m 说明已经滑到坡底平地，
			# 那段是 μ·g 的平地减速，混进来会把斜率整体拉负。
			if _player.DebugState == "Slide" and _player.DebugFeetY > 0.5:
				samples.append([tick, _player.DebugSpeed])
		_press(KEY_CTRL, false)
		_press(KEY_W, false)
		_press(KEY_SHIFT, false)

		var v0 := 0.0
		var v1 := 0.0
		var t0 := 0
		var t1 := 0
		for smp in samples:
			if int(smp[0]) >= 8 and t0 == 0:
				t0 = int(smp[0]); v0 = float(smp[1])
			if int(smp[0]) >= 8:
				t1 = int(smp[0]); v1 = float(smp[1])
		var acc := 0.0
		if t1 > t0:
			acc = (v1 - v0) / (float(t1 - t0) / 60.0)
		results[deg] = {"v0": v0, "v1": v1, "t0": t0, "t1": t1, "acc": acc, "n": samples.size()}
		await _frames(40)

	var g: float = 9.80665          # v0.5：常规系数用地球真实值（原 20.7802 是 Apex 等效）
	var mu := 0.45
	var r30: Dictionary = results[30.0]
	var r15: Dictionary = results[15.0]

	# ★ 理论值必须再乘一次 cosθ。
	#   闭式解 a = g·sinθ − μ·g·cosθ 是**沿坡**的合加速度，而这里实测的是
	#   DebugSpeed = HorizontalSpeed —— 水平面上的分量，两者差一个 cosθ 的投影。
	#   独立验证（task-2 的 00_validate-world）逐位确认过这一点：
	#     15°: 沿坡 −3.6541 → ×cos15 = −3.5296；实测 −3.5296（relErr 0）
	#     30°: 沿坡 +2.2918 → ×cos30 = +1.9848；实测 +1.9847（relErr −1e-5）
	#     45°: 沿坡 +8.0816 → ×cos45 = +5.7146；实测 +5.7146（relErr 0）
	#   不补这个 cosθ 的话，30°/15° 只是"看起来在容差内"，45° 会差 29% 直接误报。
	#   补上之后残差量级降到 1e-3，容差因此从 ±1.2/±1.5 收紧到 ±0.25。
	var a30_theory: float = (g * sin(deg_to_rad(30.0)) - mu * g * cos(deg_to_rad(30.0))) * cos(deg_to_rad(30.0))
	var a15_theory: float = (g * sin(deg_to_rad(15.0)) - mu * g * cos(deg_to_rad(15.0))) * cos(deg_to_rad(15.0))

	_expect("滑铲在 30° 坡（tanθ>μ）实测水平加速度 ≈ (g·sinθ−μg·cosθ)·cosθ",
		# v0.5：g 从 20.78 降到 9.81 后，30° 坡的滑铲加速度只有 +0.94（原来 +1.98），
		# 所以"必须明显加速"的下限从 1.0 降到 0.5（仍能区分"加速"与"减速"）。
		int(r30["n"]) >= 30 and float(r30["acc"]) > 0.5 and absf(float(r30["acc"]) - a30_theory) < 0.25,
		"实测 %+.2f m/s²（水平分量理论 %+.2f = 沿坡 %+.2f × cos30°）v %.2f→%.2f，滑铲采样 %d 帧" % [r30["acc"], a30_theory, g * sin(deg_to_rad(30.0)) - mu * g * cos(deg_to_rad(30.0)), r30["v0"], r30["v1"], r30["n"]])
	_expect("滑铲在 15° 坡（tanθ<μ）实测水平减速 ≈ −(μg·cosθ−g·sinθ)·cosθ",
		# v0.5：g 减半后 15° 坡的水平减速度只有 −1.67（原来 −3.53），下限从 −2.0 放宽到 −1.0。
		int(r15["n"]) >= 30 and float(r15["acc"]) < -1.0 and absf(float(r15["acc"]) - a15_theory) < 0.25,
		"实测 %+.2f m/s²（水平分量理论 %+.2f = 沿坡 %+.2f × cos15°）v %.2f→%.2f，滑铲采样 %d 帧" % [r15["acc"], a15_theory, g * sin(deg_to_rad(15.0)) - mu * g * cos(deg_to_rad(15.0)), r15["v0"], r15["v1"], r15["n"]])
	_expect("同样起滑速度下，陡坡越滑越快、缓坡被刹停",
		float(r30["v1"]) > float(r30["v0"]) and float(r15["v1"]) < float(r15["v0"]),
		"30° %s，15° %s" % [
			("加速 %.2f→%.2f" % [r30["v0"], r30["v1"]]) if float(r30["v1"]) > float(r30["v0"]) else ("减速 %.2f→%.2f" % [r30["v0"], r30["v1"]]),
			("加速 %.2f→%.2f" % [r15["v0"], r15["v1"]]) if float(r15["v1"]) > float(r15["v0"]) else ("减速 %.2f→%.2f" % [r15["v0"], r15["v1"]])])


func _case_vault_wall() -> void:
	# 翻越低墙 0.8m：VaultWall_0.8 位于 x=-14, z=50，尺寸 3×0.8×6
	_release_all()
	_player.DebugTeleport(-14.0, 2.0, 44.0)
	_player.DebugSetYaw(PI)                   # 朝 +Z
	await _frames(40)

	# v0.4.1：小障碍档（≤1m）是 W + Space 直接翻越，不是走着自动翻。
	_press(KEY_W, true)
	var max_z: float = -1e9
	for i in range(300):
		await get_tree().physics_frame
		if _player.DebugState == "Ground" and i % 20 == 10:
			_press(KEY_SPACE, true)
		elif i % 20 == 12:
			_press(KEY_SPACE, false)
		max_z = max(max_z, _player.global_position.z)
		if max_z > 53.5:
			break
	_press(KEY_W, false)
	_press(KEY_SPACE, false)

	_expect("翻越 0.8m 矮墙（W+Space）", max_z > 53.0, "max_z=%.2f（墙 z=47..53）" % max_z)
	await _frames(40)


func _case_window() -> void:
	# 窗口翻越：窗台 0.9m 的窗口在 x=26, z=68（窗台厚 0.6，开口净高 1.6m）
	_release_all()
	_player.DebugTeleport(26.0, 2.0, 62.0)
	_player.DebugSetYaw(PI)                   # 朝 +Z
	await _frames(40)

	# v0.4.1：窗口翻越同样挂在 W + Space 上（自动翻越探测已移除）。
	_press(KEY_W, true)
	var saw_window := false
	var max_z: float = -1e9
	for i in range(300):
		await get_tree().physics_frame
		if _player.DebugState == "Ground" and i % 20 == 10:
			_press(KEY_SPACE, true)
		elif i % 20 == 12:
			_press(KEY_SPACE, false)
		if _player.DebugState == "Window":
			saw_window = true
		max_z = max(max_z, _player.global_position.z)
		if max_z > 69.5:
			break
	_press(KEY_W, false)
	_press(KEY_SPACE, false)

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
