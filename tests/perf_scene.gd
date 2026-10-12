extends Node
func _ready() -> void:
	await get_tree().create_timer(0.8).timeout
	var times := []
	var last := Time.get_ticks_usec()
	for i in 150:
		await get_tree().process_frame
		var now := Time.get_ticks_usec()
		times.append(now-last); last = now
	times.sort()
	var med: float = times[times.size()/2]/1000.0
	var p95: float = times[int(times.size()*0.95)]/1000.0
	print("PERF bootstrap 1280x720: 中位 %.2f ms (%.0f fps), p95 %.2f ms" % [med, 1000.0/med, p95])
	print("PERF draw_calls = %d" % Performance.get_monitor(Performance.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))
	await RenderingServer.frame_post_draw
	get_viewport().get_texture().get_image().save_png("user://bootstrap_shot.png")
	print("PERF 截图已存")
	get_tree().quit()
