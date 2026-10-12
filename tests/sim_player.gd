extends Node
## 模拟玩家：验证球面重力（落地）、移动、挖掘。
func _ready() -> void:
	var boot = preload("res://scenes/levels/bootstrap.tscn").instantiate()
	add_child(boot)
	await get_tree().process_frame
	var p = boot.player
	var pl = boot.solar_system.detail_planet
	var start_r: float = (p.global_position - pl.global_position).length()
	print("初始: r=%.2f 密度=%.2f 飞行=%s" % [start_r, pl.volume.sample_density(p.global_position), p.flying])

	# 模拟 2 秒重力下落（步行模式）
	var phys := Engine.get_physics_frames()
	for i in 120:
		p._physics_process(1.0/60.0)
	var end_r: float = (p.global_position - pl.global_position).length()
	print("重力 2 秒后: r=%.2f (应停在表面附近)" % end_r)
	var den: float = pl.sample_density(p.global_position)
	print("落地密度=%.3f (应 >= 0，即在地表外)" % den)

	# 挖掘测试
	var down: Vector3 = -(p.global_position - pl.global_position).normalized()
	var hit = pl.raycast(p.camera.global_position, down, 300.0)
	print("向下射线命中 = %s" % ["有" if hit != null else "无"])
	if hit != null:
		var before: float = pl.sample_density(hit)
		pl.dig(hit, 4.0)
		var after: float = pl.sample_density(hit)
		print("挖掘: 密度 %.2f -> %.2f (应增大→变空气)" % [before, after])

	# 球形重力方向检查：在表面不同位置，重力应指向质心
	for dir in [Vector3(1,0,0), Vector3(0,1,0), Vector3(0,0,1)]:
		var pos: Vector3 = pl.global_position + dir * pl.definition.radius
		var info: Dictionary = pl.gravity_info_at(pos)
		var toward: Vector3 = info["toward_center"]
		var align: float = toward.dot(-dir)
		print("在 %s: 重力指向质心对齐度 = %.3f (期望1.0)" % [dir, align])
	get_tree().quit()
