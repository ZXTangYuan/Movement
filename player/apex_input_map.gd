class_name ApexInputMap
extends RefCounted
## 输入动作自注册。
##
## 插件**不依赖宿主项目已有的 InputMap**：首次运行时会补齐缺失的动作。
## 这样插件可以原样拖进任何 Godot .NET 3D 项目而无需手工配置。
##
## 已存在的同名动作**不会被覆盖**（尊重宿主项目的键位自定义）。

const ACTIONS := {
	"apex_move_forward": [KEY_W],
	"apex_move_back": [KEY_S],
	"apex_move_left": [KEY_A],
	"apex_move_right": [KEY_D],
	"apex_jump": [KEY_SPACE],
	"apex_crouch": [KEY_CTRL],
	"apex_sprint": [KEY_SHIFT],
	"apex_traverse": [KEY_E],
	# ── v0.2 新增 ──
	"apex_fov_up": [KEY_BRACKETRIGHT, KEY_EQUAL],
	"apex_fov_down": [KEY_BRACKETLEFT, KEY_MINUS],
	"apex_holster": [KEY_H],
	"apex_reset": [KEY_R],
	# ── v0.4 新增 ──
	# 运行时调试菜单开关（需求：「按 "\" 键进入调试菜单……再按 "\" 关闭」）。
	# 用 physical_keycode 注册，所以换成非 US 键盘布局也仍然是"那个物理位置"。
	"apex_debug_menu": [KEY_BACKSLASH],
}


static func ensure_all() -> int:
	var added := 0
	for action_name in ACTIONS:
		if InputMap.has_action(action_name):
			continue
		InputMap.add_action(action_name, 0.2)
		for keycode in ACTIONS[action_name]:
			var ev := InputEventKey.new()
			ev.physical_keycode = keycode
			InputMap.action_add_event(action_name, ev)
		added += 1
	return added


## 返回缺失的动作名（测试用；为空表示全部就绪）。
static func missing() -> PackedStringArray:
	var out := PackedStringArray()
	for action_name in ACTIONS:
		if not InputMap.has_action(action_name):
			out.append(action_name)
	return out
