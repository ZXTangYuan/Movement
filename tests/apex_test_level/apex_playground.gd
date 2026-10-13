extends Node3D
## Apex 移动**可玩测试场景**：独立关卡 + ApexCharacter 玩家。
##
## 用法（有窗口，可实际游玩）：
##   "<godot>" --path . --rendering-driver vulkan res://tests/apex_test_level/apex_playground.tscn
##
## 控制：WASD 移动 | 鼠标视角 | Space 跳/双跳/翻越时跳离 | Ctrl 蹲/滑铲
##       Shift 冲刺 | Esc 释放鼠标
##
## 本场景**不加载虚拟世界系统**，只用关卡自带的碰撞体，专注验证移动插件。

const LEVEL := preload("res://tests/apex_test_level/test_level.gd")

var player: ApexCharacter
var _hud: Label
var _mouse_captured := true


func _ready() -> void:
	# 关卡
	var level := LEVEL.new()
	level.name = "TestLevel"
	add_child(level)

	# 玩家
	player = ApexCharacter.new()
	player.name = "Player"
	player.autonomous_input = true
	add_child(player)
	# 注意：ApexCharacter 的 state 在其 _ready 中创建，故 add_child 之后才能设置位置
	player.teleport_to(Vector3(0, 1.5, 0))
	player.state.Up = Vector3.UP
	# 使用世界 Y 作为 up（关卡是平的，非球面）——gravity_up 不设置即默认 Y

	_build_hud()
	Input.mouse_mode = Input.MOUSE_MODE_CAPTURED
	print("[playground] 就绪。WASD 移动 / Space 跳 / Ctrl 蹲滑 / Shift 冲刺")


func _build_hud() -> void:
	var layer := CanvasLayer.new()
	add_child(layer)
	_hud = Label.new()
	_hud.position = Vector2(16, 12)
	_hud.add_theme_font_size_override("font_size", 16)
	_hud.add_theme_color_override("font_color", Color(0.95, 0.95, 0.95))
	_hud.add_theme_color_override("font_outline_color", Color(0, 0, 0))
	_hud.add_theme_constant_override("outline_size", 4)
	layer.add_child(_hud)

	# 准星
	var cross := Label.new()
	cross.text = "+"
	cross.add_theme_font_size_override("font_size", 22)
	cross.add_theme_color_override("font_color", Color(1, 1, 1, 0.85))
	cross.set_anchors_preset(Control.PRESET_CENTER)
	layer.add_child(cross)


func _process(_delta: float) -> void:
	if player == null or player.state == null:
		return
	var st: ApexMoveState = player.state
	var flags: Array[String] = []
	if st.OnGround: flags.append("地面")
	if st.Crouching: flags.append("蹲")
	if st.Sliding: flags.append("滑铲")
	if st.Wallrunning: flags.append("蹬墙")
	if st.Mantling: flags.append("攀爬" if st.MantleIsClimb else "翻越")
	if not st.OnGround and not st.Mantling: flags.append("空中")
	_hud.text = "速度 %.1f m/s  高 %.1f m\n%s\n位置 (%.1f, %.1f, %.1f)" % [
		st.Velocity.length(), st.Position.y,
		" ".join(flags) if flags.size() > 0 else "-",
		st.Position.x, st.Position.y, st.Position.z]


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseMotion and _mouse_captured:
		_look(event.relative.x, event.relative.y)
	elif event.is_action_pressed("toggle_mouse"):
		_mouse_captured = not _mouse_captured
		Input.mouse_mode = Input.MOUSE_MODE_CAPTURED if _mouse_captured else Input.MOUSE_MODE_VISIBLE
	elif event.is_action_pressed("teleport_next"):
		# 复用 G 键：回到出生点
		player.teleport_to(Vector3(0, 1.5, 0))


func _look(dx: float, dy: float) -> void:
	if player == null:
		return
	player.add_look(dx, dy)
