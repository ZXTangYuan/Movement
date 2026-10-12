extends Node
## 镜头回归测试（断言已修正）。
func _ready() -> void:
	var boot = preload("res://scenes/levels/bootstrap.tscn").instantiate()
	add_child(boot)
	for i in 3: await get_tree().process_frame
	var p = boot.player
	var fails := 0

	# 1) yaw 只改朝向，不产生俯仰
	p._up = Vector3(1, 0, 0); p._yaw = 0.0; p._pitch = 0.0; p._surface_weight = 1.0
	p._apply_view()
	var f0: Vector3 = p.get_forward()
	p._yaw = deg_to_rad(30.0); p._apply_view()
	var f1: Vector3 = p.get_forward()
	var d_ang := rad_to_deg(f0.angle_to(f1))
	var d_pitch := absf(rad_to_deg(asin(clampf(f1.dot(p._up),-1,1)) - asin(clampf(f0.dot(p._up),-1,1))))
	print("[1] yaw=30° → 朝向变化 %.1f° (期望30), 俯仰泄漏 %.2f° (期望0)" % [d_ang, d_pitch])
	if absf(d_ang-30.0) > 2.0 or d_pitch > 1.0: print("  FAIL"); fails += 1

	# 2) pitch 只改俯仰（用绝对变化量比较）
	p._yaw = 0.0; p._pitch = 0.0; p._apply_view()
	var g0: Vector3 = p.get_forward()
	p._pitch = deg_to_rad(30.0); p._apply_view()
	var g1: Vector3 = p.get_forward()
	var pa0 := rad_to_deg(asin(clampf(g0.dot(p._up),-1,1)))
	var pa1 := rad_to_deg(asin(clampf(g1.dot(p._up),-1,1)))
	var dp := absf(pa1 - pa0)
	print("[2] pitch=30° → 俯仰 %.1f°→%.1f°，变化 %.1f° (期望30)" % [pa0, pa1, dp])
	if absf(dp-30.0) > 2.0: print("  FAIL"); fails += 1
	# 朝向不应变（水平分量方向不变）
	var t0: Vector3 = (g0 - p._up*g0.dot(p._up)).normalized()
	var t1: Vector3 = (g1 - p._up*g1.dot(p._up)).normalized()
	var yaw_leak := rad_to_deg(t0.angle_to(t1))
	print("     朝向泄漏 %.2f° (期望0)" % yaw_leak)
	if yaw_leak > 1.0: print("  FAIL"); fails += 1

	# 3) 鼠标向下（dy>0）应向下看（forward·up 变小）
	p._yaw = 0.0; p._pitch = 0.0; p._surface_weight = 1.0; p._apply_view()
	var before_dot: float = p.get_forward().dot(p._up)
	p._look(0, 100)
	var after_dot: float = p.get_forward().dot(p._up)
	print("[3] 鼠标下移: forward·up %.3f → %.3f (期望变小=向下看)" % [before_dot, after_dot])
	if after_dot >= before_dot: print("  FAIL 上下方向反了"); fails += 1

	# 4) 鼠标向右（dx>0）应向右转
	p._yaw = 0.0; p._pitch = 0.0; p._apply_view()
	var r0: Vector3 = p.get_forward()
	p._look(100, 0)
	var r1: Vector3 = p.get_forward()
	var right: Vector3 = p._up.cross(Vector3(0,0,-1)).normalized()
	print("[4] 鼠标右移: forward 从 %s → %s" % [r0.snapped(Vector3(0.01,0.01,0.01)), r1.snapped(Vector3(0.01,0.01,0.01))])
	if r0.angle_to(r1) < 0.01: print("  FAIL 左右没动"); fails += 1

	# 5) pitch 夹紧 [-90,+90]
	p._pitch = deg_to_rad(-200.0); p._look(0,0)
	var lo := rad_to_deg(p._pitch)
	p._pitch = deg_to_rad(200.0); p._look(0,0)
	var hi := rad_to_deg(p._pitch)
	print("[5] pitch 夹紧: 下限 %.1f° (期望-90), 上限 %.1f° (期望+90)" % [lo, hi])
	if absf(lo+90.0) > 1.0 or absf(hi-90.0) > 1.0: print("  FAIL"); fails += 1

	# 6) 星球另一面 up=-X：forward 必须在切平面内
	p._up = Vector3(-1,0,0); p._yaw = deg_to_rad(45.0); p._pitch = 0.0; p._apply_view()
	var q: Vector3 = p.get_forward()
	print("[6] up=-X, yaw=45°: forward·up = %.4f (期望0)" % q.dot(p._up))
	if absf(q.dot(p._up)) > 0.01: print("  FAIL 不在切平面"); fails += 1
	# 极点 up=+Y 也不能退化
	p._up = Vector3(0,1,0); p._yaw = deg_to_rad(45.0); p._pitch = 0.0; p._apply_view()
	var q2: Vector3 = p.get_forward()
	print("     极点 up=+Y: forward·up = %.4f (期望0)" % q2.dot(p._up))
	if absf(q2.dot(p._up)) > 0.01: print("  FAIL 极点退化"); fails += 1

	# 7) FREE 模式：必须能越过 ±90°（证明无限制）
	p._surface_weight = 0.0
	p._free_rot = Quaternion.IDENTITY
	var max_abs_dot := 0.0
	var exceeded := false
	for i in 40:
		p._look(0, 200)
		var d: float = p.get_forward().dot(Vector3.UP)
		max_abs_dot = maxf(max_abs_dot, absf(d))
		if d > 0.5: exceeded = true   # 翻过极点，看向"上方"
	print("[7] FREE 模式连续下转: 最大|forward·UP| = %.3f, 越过极点 = %s" % [max_abs_dot, exceeded])
	if not exceeded: print("  FAIL FREE 模式被限制了"); fails += 1

	print("=== 镜头回归 %s (%d fails) ===" % ["PASS" if fails==0 else "FAIL", fails])
	get_tree().quit(fails)
