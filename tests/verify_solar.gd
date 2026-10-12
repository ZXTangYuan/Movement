extends Node
## 验证：八大行星 / 太阳光照 / 重力场切换 / 姿态平滑。
func _ready() -> void:
	var boot = preload("res://scenes/levels/bootstrap.tscn").instantiate()
	add_child(boot)
	for i in 3: await get_tree().process_frame
	var ss = boot.solar_system
	var p = boot.player
	var fails := 0

	# ── 1) 八大行星 ──
	print("=== 1) 行星清单 ===")
	if ss.planets.size() != 8:
		print("  FAIL 行星数 = %d (期望 8)" % ss.planets.size()); fails += 1
	for pl in ss.planets:
		print("  %-8s r=%.0f 重力=%.1f 轨道=%.0f 倾角=%.2f" % [
			pl.definition.display_name, pl.definition.radius,
			pl.definition.gravity_strength, pl.orbit_radius, pl.orbit_tilt])
	# 检查尺寸/轨道递增合理（不挤成一坨）
	var radii := []
	var orbits := []
	for pl in ss.planets: radii.append(pl.definition.radius)
	for pl in ss.planets: orbits.append(pl.orbit_radius)
	print("  半径范围 %.0f..%.0f (木星应最大)" % [radii.min(), radii.max()])
	print("  轨道范围 %.0f..%.0f" % [orbits.min(), orbits.max()])
	if orbits[7] <= orbits[0] * 2.0:
		print("  FAIL 轨道太挤"); fails += 1

	# ── 2) 太阳真实光照 ──
	print("=== 2) 太阳光照 ===")
	var star = ss.star
	if star == null or star.light == null:
		print("  FAIL 无恒星/光源"); fails += 1
	else:
		# 光照方向应指向玩家所在行星
		star.aim_at(p.global_position)
		var ldir: Vector3 = -star.light.global_transform.basis.z
		var to_player: Vector3 = (p.global_position - star.global_position).normalized()
		var align: float = ldir.dot(to_player)
		print("  阳光方向·指向玩家 = %.4f (期望≈1.0)" % align)
		if align < 0.99: print("  FAIL 光照方向不对"); fails += 1
		print("  光能量 = %.2f, 颜色 = %s" % [star.light.light_energy, star.light.light_color])
		# 太阳必须移动光照方向（不是固定方向）
		var before: Vector3 = -star.light.global_transform.basis.z
		star.aim_at(Vector3(0, 5000, 0))
		var after: Vector3 = -star.light.global_transform.basis.z
		var moved := rad_to_deg(before.angle_to(after))
		print("  改目标后光照方向变化 %.1f° (应>0，证明是真方向光)" % moved)
		if moved < 1.0: print("  FAIL 光照方向固定不动"); fails += 1

	# ── 3) 每颗行星的重力场 ──
	print("=== 3) 行星重力场 ===")
	for pl in ss.planets:
		var surf: Vector3 = pl.global_position + Vector3(pl.definition.radius, 0, 0)
		var info: Dictionary = pl.gravity_info_at(surf)
		var up: Vector3 = info["up"]
		var toward: Vector3 = info["toward_center"]
		var s: float = info["strength"]
		# up 应指向背离质心（+X），toward 指向质心（-X）
		var up_ok := up.dot(Vector3(1,0,0)) > 0.99
		var toward_ok := toward.dot(Vector3(-1,0,0)) > 0.99
		var s_ok := absf(s - pl.definition.gravity_strength) < 0.5
		print("  %-8s 表面重力=%.2f (期望%.1f) up·+X=%.3f toward·-X=%.3f %s" % [
			pl.definition.display_name, s, pl.definition.gravity_strength,
			up.dot(Vector3(1,0,0)), toward.dot(Vector3(-1,0,0)),
			"OK" if (up_ok and toward_ok and s_ok) else "FAIL"])
		if not (up_ok and toward_ok and s_ok): fails += 1
		# 远处应为 0（影响半径外）
		var far_info: Dictionary = pl.gravity_info_at(pl.global_position + Vector3(pl.definition.radius * 20, 0, 0))
		if far_info["strength"] > 0.01:
			print("    FAIL 影响半径外仍有重力 %.3f" % far_info["strength"]); fails += 1

	# ── 4) 主导星球选择（地球表面应选地球） ──
	print("=== 4) 主导星球 ===")
	var dom: Dictionary = ss.dominant_planet_at(p.global_position)
	var domp: Planet = dom["planet"]
	print("  玩家处主导星球 = %s (期望 Earth)" % (domp.definition.display_name if domp != null else "无"))
	if domp == null or domp.definition.display_name != "Earth":
		print("  FAIL 主导星球不对"); fails += 1

	# ── 5) 姿态平滑修正（不能瞬间拧麻花） ──
	print("=== 5) 姿态平滑 ===")
	var up_before: Vector3 = p._up
	# 传送到火星
	boot._current_planet_index = 2
	boot._teleport_to_next_planet()
	var mars: Planet = ss.planets[3]
	print("  传送到 %s，位置 r=%.1f" % [mars.definition.display_name, (p.global_position - mars.global_position).length()])
	# 连续跑物理帧，观察 up 是否平滑过渡（每帧变化量应有限）
	var max_step := 0.0
	var prev: Vector3 = p._up
	for i in 60:
		p._physics_process(1.0/60.0)
		var step := rad_to_deg(prev.angle_to(p._up))
		max_step = maxf(max_step, step)
		prev = p._up
	print("  60 帧内 up 单帧最大变化 %.2f° (应 < 20°，避免瞬移拧麻花)" % max_step)
	if max_step > 20.0: print("  FAIL 姿态跳变过大"); fails += 1
	# 最终应对齐到火星的重力方向
	var expect_up: Vector3 = (p.global_position - mars.global_position).normalized()
	var final_align: float = p._up.dot(expect_up)
	print("  最终 up·火星法线 = %.4f (期望≈1.0)" % final_align)
	if final_align < 0.98: print("  FAIL 未对齐到新星球重力"); fails += 1

	print("=== 太阳系验证 %s (%d fails) ===" % ["PASS" if fails==0 else "FAIL", fails])
	get_tree().quit(fails)
