extends Node
## 从太空俯瞰太阳系，验证恒星+八大行星+光照。
func _ready() -> void:
	var boot = preload("res://scenes/levels/bootstrap.tscn").instantiate()
	add_child(boot)
	for i in 3: await get_tree().process_frame
	var cam := Camera3D.new()
	cam.fov = 62.0
	cam.far = 200000.0
	boot.add_child(cam)
	# 从太阳系上方斜俯视
	cam.position = Vector3(0, 34000, 48000)
	cam.look_at(Vector3(0, 0, 0), Vector3.UP)
	cam.current = true
	for i in 6: await get_tree().process_frame
	await RenderingServer.frame_post_draw
	get_viewport().get_texture().get_image().save_png("user://solar.png")
	print("SOLAR saved")
	get_tree().quit()
