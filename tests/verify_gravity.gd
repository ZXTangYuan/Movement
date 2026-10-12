extends Node
## 精确验证：每颗行星在多个方向上的重力场。
func _ready() -> void:
	var boot = preload("res://scenes/levels/bootstrap.tscn").instantiate()
	add_child(boot)
	for i in 3: await get_tree().process_frame
	var ss = boot.solar_system
	var fails := 0
	print("=== 行星重力场精确验证 ===")
	for pl in ss.planets:
		var c: Vector3 = pl.global_position
		var r: float = pl.definition.radius
		var line := "%-8s " % pl.definition.display_name
		for dir in [Vector3(1,0,0), Vector3(-1,0,0), Vector3(0,1,0), Vector3(0,0,1), Vector3(0.577,0.577,0.577)]:
			var probe: Vector3 = c + dir.normalized() * r
			var info: Dictionary = pl.gravity_info_at(probe)
			var up: Vector3 = info["up"]
			var toward: Vector3 = info["toward_center"]
			var s: float = info["strength"]
			# up 必须 = 背离质心方向；toward 必须 = 指向质心
			var up_err: float = up.distance_to(dir.normalized())
			var toward_err: float = toward.distance_to(-dir.normalized())
			var s_err: float = absf(s - pl.definition.gravity_strength)
			var ok: bool = up_err < 0.01 and toward_err < 0.01 and s_err < 0.1
			if not ok:
				fails += 1
				line += "[%s: up_err=%.3f t_err=%.3f s=%.2f] " % [dir, up_err, toward_err, s]
		print(line + ("OK" if not line.contains("[") else ""))
	print("=== 重力场验证 %s (%d fails) ===" % ["PASS" if fails==0 else "FAIL", fails])
	get_tree().quit(fails)
