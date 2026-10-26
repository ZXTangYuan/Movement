extends Node
## 临时探针：实测走路/奔跑时相机（眼位 + 俯仰 + 侧倾）的真实峰到峰值。
## 用法：godot --headless --path . res://addons/apex_movement/tests/probe_gait.tscn

const TESTBED := "res://addons/apex_movement/testbed/apex_testbed.tscn"

var _player: Node3D
var _cam: Camera3D

func _ready() -> void:
	var scene: Node = load(TESTBED).instantiate()
	add_child(scene)
	await get_tree().process_frame
	await get_tree().create_timer(0.4).timeout
	_player = _find_apex(scene)
	_cam = get_tree().get_first_node_in_group("player_cameras") as Camera3D
	if _player == null or _cam == null:
		print("PROBE ERROR player=%s cam=%s" % [_player, _cam])
		get_tree().quit(1)
		return

	await _measure("walk", false)
	await _measure("sprint", true)
	await _measure("crouchwalk", false, true)
	get_tree().quit(0)

func _measure(tag: String, sprint: bool, crouch: bool = false) -> void:
	_player.DebugTeleport(0.0, 1.2, 0.0)
	_player.DebugSetYaw(0.0)
	await _frames(30)
	if crouch:
		Input.action_press("apex_crouch")
		await _frames(20)
	if sprint:
		Input.action_press("apex_sprint")
	Input.action_press("apex_move_forward")
	await _frames(120)

	var min_y := 1e9
	var max_y := -1e9
	var min_p := 1e9
	var max_p := -1e9
	var min_r := 1e9
	var max_r := -1e9
	var n := 0
	var t0 := Time.get_ticks_msec()
	while Time.get_ticks_msec() - t0 < 2500:
		await get_tree().process_frame
		var b := _cam.global_transform.basis
		var fwd := -b.z
		var right := b.x
		var y := _cam.global_position.y
		var pit := asin(clampf(fwd.y, -1.0, 1.0))
		var rol := asin(clampf(right.y, -1.0, 1.0))
		min_y = minf(min_y, y)
		max_y = maxf(max_y, y)
		min_p = minf(min_p, pit)
		max_p = maxf(max_p, pit)
		min_r = minf(min_r, rol)
		max_r = maxf(max_r, rol)
		n += 1

	var secs := float(Time.get_ticks_msec() - t0) / 1000.0
	var hud: String = _player.DebugHudLine
	var gait_line := ""
	for ln in hud.split("\n"):
		if "gait=" in ln:
			gait_line = ln.strip_edges()
	print("GAIT %-10s frames=%d secs=%.2f speed=%.2f  camY p2p=%.1fmm  pitch p2p=%.3fdeg  roll p2p=%.3fdeg  fps=%.0f" % [
		tag, n, secs, _player.DebugSpeed,
		(max_y - min_y) * 1000.0,
		rad_to_deg(max_p - min_p),
		rad_to_deg(max_r - min_r),
		float(n) / maxf(secs, 0.001)])
	print("           HUD %s" % gait_line)

	Input.action_release("apex_move_forward")
	if sprint:
		Input.action_release("apex_sprint")
	if crouch:
		Input.action_release("apex_crouch")

func _frames(n: int) -> void:
	for _i in n:
		await get_tree().process_frame

func _find_apex(n: Node) -> Node3D:
	var s := n.get_script()
	if s != null and "ApexPlayer" in str(s.resource_path):
		return n as Node3D
	for c in n.get_children():
		var r := _find_apex(c)
		if r != null:
			return r
	return null
