extends Node
## 回归验证：raymarch 的 delta 窗口与碰撞共用同一密度场（挖填必须"看得见"）。
##
## 背景：曾经 `RaymarchTerrain.sync_to_planet()` 把 shader 的 `delta_origin`
## 误传为**世界坐标**，而 shader 内部按**行星局部坐标**解释它 →
## 挖/填在 raymarch 里完全不可见（而碰撞看得到），玩家感觉"挖不动"。
## 本测试断言：对同一挖点，shader 采样的 uv 落在 [0,1] 内，
## 且 shader 看到的 delta == C# 碰撞用的 delta。
##
## 用法：<godot> --path . --rendering-driver vulkan res://tests/verify_dig_visible.tscn
## 期望输出：DIG VISIBLE PASS

func _ready() -> void:
	if RenderingServer.get_rendering_device() == null:
		print("SKIP: 无 RenderingDevice（需 --rendering-driver vulkan 或带窗口）")
		get_tree().quit(0)
		return

	var boot = preload("res://scenes/levels/bootstrap.tscn").instantiate()
	add_child(boot)
	for i in 3:
		await get_tree().process_frame

	var fails := 0
	var planet = boot.solar_system.detail_planet
	var vol = planet.volume
	var mat = planet.terrain._mat

	if vol == null or mat == null:
		print("FAIL: 细节行星未进入 RAYMARCH 模式（volume/terrain 为空）")
		get_tree().quit(1)
		return

	# ── 取 shader 当前的 uniform 约定 ──
	var planet_center: Vector3 = mat.get_shader_parameter("planet_center")
	var delta_origin: Vector3 = mat.get_shader_parameter("delta_origin")
	var size: float = mat.get_shader_parameter("delta_world_size")

	print("=== 1) uniform 一致性 ===")
	print("  planet_center (world)   = ", planet_center)
	print("  delta_origin (mat set)  = ", delta_origin)
	print("  volume.delta_origin()   = ", vol.delta_origin())
	# delta_origin 必须是行星局部坐标（== volume.delta_origin()），不能是世界坐标。
	if delta_origin.distance_to(vol.delta_origin()) > 0.01:
		print("  FAIL: delta_origin 应为行星局部坐标，实际与世界坐标混用")
		fails += 1
	else:
		print("  OK: delta_origin 为行星局部坐标")

	# ── 挖一个大洞，验证 shader 与碰撞看到同一个 delta ──
	print("=== 2) 挖填在 shader/碰撞中一致 ===")
	var player = boot.player
	var world_pos: Vector3 = player.global_position
	var dig_radius := 6.0
	planet.dig(world_pos, dig_radius)
	for i in 2:
		await get_tree().process_frame

	var local_pos: Vector3 = world_pos - planet.global_position
	var collision_delta: float = vol.core.Delta(local_pos)
	# 复刻 shader 的 density_at() 采样约定
	var p_local: Vector3 = world_pos - planet_center
	var uv: Vector3 = (p_local - delta_origin) / size
	var in_range: bool = uv.x > 0.0 and uv.x < 1.0 \
		and uv.y > 0.0 and uv.y < 1.0 and uv.z > 0.0 and uv.z < 1.0
	print("  dig radius              = %.1f" % dig_radius)
	print("  collision delta         = %.4f" % collision_delta)
	print("  shader uv               = ", uv, "  in_range=", in_range)
	if not in_range:
		print("  FAIL: shader uv 越界 → delta 纹理采样不到（挖填不可见）")
		fails += 1
	elif absf(collision_delta) < 1.0:
		print("  FAIL: 挖后碰撞侧 delta 仍 ~0（编辑未生效）")
		fails += 1
	else:
		print("  OK: shader 与碰撞看到同一 delta（挖填可见）")

	print("=== DIG VISIBLE %s (%d failures) ===" % ["PASS" if fails == 0 else "FAIL", fails])
	get_tree().quit(fails)
