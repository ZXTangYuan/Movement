extends Node
## 玩家在地球表面的第一人称视角（验证地形+光照+重力姿态）。
func _ready() -> void:
	var boot = preload("res://scenes/levels/bootstrap.tscn").instantiate()
	add_child(boot)
	for i in 8: await get_tree().process_frame
	var p = boot.player
	var pl = boot.solar_system.detail_planet
	print("SURFACE 玩家 r=%.1f 星球半径=%.1f 密度=%.2f" % [
		(p.global_position - pl.global_position).length(), pl.definition.radius,
		pl.sample_density(p.global_position)])
	print("  _up = %s" % p._up)
	print("  forward = %s" % p.get_forward())
	print("  绑定星球 = %s" % (p.bound_planet.definition.display_name if p.bound_planet else "无"))
	print("  视角模式 = %s (权重 %.2f)" % [p.view_mode, p._surface_weight])
	await RenderingServer.frame_post_draw
	get_viewport().get_texture().get_image().save_png("user://surface.png")
	print("SURFACE saved")
	get_tree().quit()
