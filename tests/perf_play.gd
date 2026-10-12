extends Node
## 玩家场景端到端性能：渲染 + 物理 + 跟随窗口。
func _ready() -> void:
	var boot = preload("res://scenes/levels/bootstrap.tscn").instantiate()
	add_child(boot)
	await get_tree().create_timer(1.0).timeout
	var times := []
	var last := Time.get_ticks_usec()
	for i in 180:
		await get_tree().process_frame
		var now := Time.get_ticks_usec()
		times.append(now - last); last = now
	times.sort()
	var med: float = times[times.size()/2] / 1000.0
	var p95: float = times[int(times.size()*0.95)] / 1000.0
	print("PERF 玩家场景 1280x720: 中位 %.2f ms (%.0f fps), p95 %.2f ms" % [med, 1000.0/med, p95])
	print("PERF draw_calls = %d" % Performance.get_monitor(Performance.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))
	# 连续挖掘吞吐
	var pl = boot.solar_system.detail_planet
	var p = boot.player
	var t0 := Time.get_ticks_usec()
	for i in 15:
		var down: Vector3 = -(p.global_position - pl.global_position).normalized()
		var hit = pl.raycast(p.camera.global_position, down, 300.0)
		if hit != null:
			pl.dig(hit, 3.0)
	var t1 := Time.get_ticks_usec()
	print("PERF 15 次挖掘 = %.1f ms (%.2f ms/次)" % [(t1-t0)/1000.0, (t1-t0)/1000.0/15.0])
	get_tree().quit()
