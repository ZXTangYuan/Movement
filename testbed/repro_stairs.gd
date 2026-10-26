extends Node
## 上台阶顿挫的**真机**测量（v0.5.3 需求 m02395 第 3 点）。
##
## 逐渲染帧记录相机眼位，输出：
##   · max|Δ²p|（相机位置的二阶差分，越大越"顿"）
##   · 上台阶前后水平速度（看有没有"踩刹车"）
##
## 用法：
##   godot --path . res://addons/apex_movement/testbed/repro_stairs.tscn

const TESTBED := "res://addons/apex_movement/testbed/apex_testbed.tscn"

var _player: Node3D


func _ready() -> void:
	var scene: Node = load(TESTBED).instantiate()
	add_child(scene)
	await get_tree().process_frame
	await get_tree().process_frame
	await get_tree().create_timer(0.4).timeout

	_player = _find_apex(scene)
	if _player == null:
		push_error("没找到 ApexPlayer")
		get_tree().quit()
		return

	# testbed 的连续台阶（testbed_builder.gd:_build_step_stairs）：
	#   StairFlight_0..5，x=10，z = -60 - 2i，宽 10 × 深 2，高 0.20*(i+1)
	#   沿 **-Z** 方向逐级升高 ⇒ 玩家朝 -Z（yaw=0）走上去。
	var base := Vector3(10.0, 0.0, -60.0)
	print("STAIRS at ", base)

	# 站到第一级前 3 m（z 更大 = 更靠起点），朝 -Z
	_player.DebugTeleport(base.x, 1.2, base.z + 3.0)
	_player.DebugSetYaw(0.0)
	await get_tree().create_timer(0.4).timeout

	# 先测"关掉平滑"的基线，再测"开平滑"，同一场景同一输入
	for smooth in [0.0, 4.0]:
		await _run_once(scene, base, smooth)

	get_tree().quit()


func _run_once(scene: Node, base: Vector3, smooth: float) -> void:
	_player.StairEyeSmoothSpeed = smooth
	_player.DebugTeleport(base.x, 1.2, base.z + 3.0)
	_player.DebugSetYaw(0.0)
	await get_tree().create_timer(0.35).timeout

	_press_key(KEY_W, true)
	_press_key(KEY_SHIFT, true)

	var prev := _cam_pos()
	var prev2 := prev
	var max_d2 := 0.0
	var max_dy := 0.0
	var min_hspd := 999.0
	var samples := 0
	# 只统计"正在爬台阶"的窗口：站地且脚底在台阶高度区间内。
	# 最后一级之后玩家会从 1.2m 高的台面掉下去，那段的位移本来就应该大，
	# 混进来会把结论完全糊掉（实测 falls 贡献了 max_cam_dy 的绝大部分）。
	var max_d2_stair := 0.0
	var max_dy_stair := 0.0
	var _rows: Array[String] = []

	# ★ 必须等 **physics_frame**：等 process_frame 的话循环 180 帧只过了 0.17 s
	#   （渲染帧远快于物理 tick），玩家还没走到台阶就结束了。
	for i in 180:
		await get_tree().physics_frame
		if i % 30 == 0:
			print("t=%d pos=%s feet=%.3f state=%s hspd=%.2f tick=%d" % [
				i, str(_player.global_position), _player.DebugFeetY,
				str(_player.DebugState), _player.DebugSpeed, _player.DebugTick])
		var p := _cam_pos()
		var d := p - prev
		var d2 := (p - prev) - (prev - prev2)
		max_d2 = max(max_d2, d2.length())
		max_dy = max(max_dy, absf(d.y))
		# 记录每一次"相机竖直位移"以及当时的脚底高度/状态，事后看它到底发生在哪
		_rows.append("s=%.1f t=%d dy=%.4f feet=%.3f state=%s onG=%s" % [
			smooth, i, d.y, _player.DebugFeetY, str(_player.DebugState),
			str(_player.DebugOnGround)])
		var hs: float = _player.DebugSpeed
		if hs > 0.5:
			min_hspd = min(min_hspd, hs)
			samples += 1
		prev2 = prev
		prev = p

	_press_key(KEY_W, false)
	_press_key(KEY_SHIFT, false)

	print("STAIR_REPRO smooth=%.1f max_cam_d2=%.4f max_cam_dy=%.4f min_hspd=%.3f samples=%d" % [
		smooth, max_d2, max_dy, min_hspd, samples])
	print("STAIR_DBG smooth=%.1f calls=%d clamps=%d" % [
		smooth, _player.EyeSmoothCalls, _player.EyeSmoothClamps])

	# 把逐帧记录写成 CSV，交给外部脚本分析（只看最大的几次）
	var f := FileAccess.open("user://stair_smooth_%.1f.txt" % smooth, FileAccess.WRITE)
	for row in _rows:
		f.store_line(row)
	f.close()
	print("STAIR_END   pos=%s feet=%.3f state=%s" % [
		str(_player.global_position), _player.DebugFeetY, str(_player.DebugState)])
	await get_tree().create_timer(0.3).timeout


func _cam_pos() -> Vector3:
	var cam: Camera3D = _player.DebugCamera
	return cam.global_position


func _find_apex(node: Node) -> Node3D:
	if node.get_script() != null and "DebugTeleport" in node:
		return node
	for child in node.get_children():
		var found := _find_apex(child)
		if found != null:
			return found
	return null


func _find_named(node: Node, needle: String) -> Node3D:
	if node is Node3D and node.name.to_lower().contains(needle):
		return node
	for child in node.get_children():
		var found := _find_named(child, needle)
		if found != null:
			return found
	return null


func _press_key(code: Key, pressed: bool) -> void:
	var ev := InputEventKey.new()
	ev.physical_keycode = code
	ev.keycode = code
	ev.pressed = pressed
	Input.parse_input_event(ev)
