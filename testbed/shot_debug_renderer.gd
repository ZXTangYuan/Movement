extends Node
## 三维速度/加速度调试渲染器 + 第三人称的截图验证（v0.5，需求 m01621）。
##
## 用法（**必须有窗口/真实渲染**，headless 拍不出画面）：
##   godot --path . --rendering-driver vulkan --resolution 1600x900 \
##       res://addons/apex_movement/testbed/shot_debug_renderer.tscn
##
## 出图：
##   shot_dbg_idle      站立静止（加速度只有重力 + 贴地投影）
##   shot_dbg_walk      走路（输入推进 + 摩擦 + 重力）
##   shot_dbg_walk_diag 斜向跑（证明速度箭头是**三维矢量**，不是竖直的）
##   shot_dbg_sprint    冲刺
##   shot_dbg_slide     滑铲（坡面重力分量）
##   shot_dbg_jump      空中（纯重力，没有摩擦）
##   shot_dbg_menu      调试菜单里的三个新开关
##
## 每张图左上角都有彩色图例，与三维箭头颜色一一对应。

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

	# 打开第三人称 + 三维调试器（走的是菜单同一条回调链）
	_player.SetThirdPerson(true)
	_player.SetDebugRenderer(true)

	# 站在滑铲长道上（x=-70），朝向 +Z
	_player.DebugTeleport(-70.0, 1.2, 30.0)
	_player.DebugSetYaw(PI)
	await _settle()
	await _save("shot_dbg_idle")

	_press_key(KEY_W, true)
	await _wait_frames(60)
	await _save("shot_dbg_walk")

	# ★ v0.5.3 新增：**斜向跑**（W+D）。
	#   速度箭头在"正前方"时会正对第三人称机位，被透视压成一个点，
	#   看不出它是不是三维矢量。斜向跑让速度与视线成约 45°，箭头方向一目了然。
	_press_key(KEY_D, true)
	await _wait_frames(45)
	await _save("shot_dbg_walk_diag")
	_press_key(KEY_D, false)
	await _wait_frames(30)

	_press_key(KEY_SHIFT, true)
	await _wait_frames(90)
	await _save("shot_dbg_sprint")

	_press_key(KEY_CTRL, true)
	await _wait_frames(20)
	await _save("shot_dbg_slide")
	_press_key(KEY_CTRL, false)
	_press_key(KEY_W, false)
	_press_key(KEY_SHIFT, false)
	await _wait(0.5)

	# 空中：跳起来在下落段取图（此时只有重力）
	_player.DebugTeleport(-70.0, 1.2, 30.0)
	await _settle()
	_press_key(KEY_SPACE, true)
	await _wait_frames(2)
	_press_key(KEY_SPACE, false)
	await _wait_frames(22)
	await _save("shot_dbg_jump")

	# 调试菜单（三个新开关都在下半部分）
	_player.ToggleDebugMenu()
	await _wait(0.4)
	await _save("shot_dbg_menu")

	print("DEBUG RENDERER SHOTS DONE")
	get_tree().quit()


func _find_apex(node: Node) -> Node3D:
	if node.get_script() != null and "DebugTeleport" in node:
		return node
	for child in node.get_children():
		var found := _find_apex(child)
		if found != null:
			return found
	return null


func _settle() -> void:
	await get_tree().create_timer(0.35).timeout


func _wait(s: float) -> void:
	await get_tree().create_timer(s).timeout


func _wait_frames(n: int) -> void:
	for i in n:
		await get_tree().physics_frame


func _press_key(code: Key, pressed: bool) -> void:
	var ev := InputEventKey.new()
	ev.physical_keycode = code
	ev.keycode = code
	ev.pressed = pressed
	Input.parse_input_event(ev)


func _save(name: String) -> void:
	# Vulkan 呈现队列会落后十几帧：等 24 帧 + 一次 frame_post_draw 才取图
	for i in 24:
		await get_tree().process_frame
	await RenderingServer.frame_post_draw
	var img := get_viewport().get_texture().get_image()
	var path := "user://%s.png" % name
	img.save_png(path)
	print("SAVED %s -> %s" % [name, ProjectSettings.globalize_path(path)])
