@tool
extends EditorPlugin
## ApexMovement 插件入口。
##
## 本插件主要提供**类与场景**（ApexMovementCore / ApexPlayer / 测试场地），
## 不需要在编辑器里挂工具面板，因此 _enter_tree / _exit_tree 保持为空——
## 类注册由 Godot 的 class_name 自动完成。
##
## 未来若需要工具面板（例如参数调试器），在此注册并**务必**在 _exit_tree 里对称清理。

func _enter_tree() -> void:
	pass


func _exit_tree() -> void:
	pass
