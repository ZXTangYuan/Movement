extends Node
## 无头自检：无限地形核心（C# VoxelTerrainCore）。
## 用法：godot --path . --rendering-driver vulkan res://tests/headless_selftest_gpu.tscn

func _ready() -> void:
	var failures := 0
	failures += _test_baseline_sane()
	failures += _test_edit_changes_density()
	failures += _test_determinism()
	failures += _test_raycast_surface()
	failures += _test_save_roundtrip()
	print("=== CORE SELFTEST %s (%d failures) ===" % ["PASS" if failures == 0 else "FAIL", failures])
	get_tree().quit(failures)


func _mk() -> VoxelTerrainCore:
	var c := VoxelTerrainCore.new()
	c.Configure(256.0, 0.018, 5, 26.0, 20261013)
	return c


func _test_baseline_sane() -> int:
	var c := _mk()
	var center := c.Baseline(Vector3.ZERO)
	var far := c.Baseline(Vector3(900, 0, 0))
	print("  baseline 中心=%.2f 远处=%.2f" % [center, far])
	if center >= 0.0:
		print("  FAIL: 球心应为实体"); return 1
	if far <= 0.0:
		print("  FAIL: 远处应为空气"); return 1
	var surf := c.Baseline(Vector3(256, 0, 0))
	if absf(surf) > 30.0:
		print("  FAIL: 表面密度偏离过大 (%.2f)" % surf); return 1
	print("  OK: 基线合理")
	return 0


func _test_edit_changes_density() -> int:
	var c := _mk()
	var p := Vector3(256, 0, 0)
	var before := c.SampleDensity(p)
	c.AddEdit(p, 5.0, 1, 0)   # 挖
	var after := c.SampleDensity(p)
	print("  挖掘: %.2f -> %.2f" % [before, after])
	if after <= before:
		print("  FAIL: 挖掘未增加密度"); return 1
	c.ClearEdits()
	c.AddEdit(p, 5.0, 0, 0)   # 填
	var filled := c.SampleDensity(p)
	if filled >= before:
		print("  FAIL: 填充未减少密度"); return 1
	print("  OK: 编辑生效（挖/填方向正确）")
	return 0


func _test_determinism() -> int:
	var a := _mk()
	var b := _mk()
	var pa := Vector3(200, 150, 100)
	if absf(a.Baseline(pa) - b.Baseline(pa)) > 1e-6:
		print("  FAIL: 同 seed 不确定"); return 1
	var c := VoxelTerrainCore.new()
	c.Configure(256.0, 0.018, 5, 26.0, 999)
	if absf(a.Baseline(pa) - c.Baseline(pa)) < 1e-6:
		print("  FAIL: 不同 seed 产生了相同地形"); return 1
	print("  OK: 确定性 + seed 生效")
	return 0


func _test_raycast_surface() -> int:
	var c := _mk()
	var hit = _raycast(c, Vector3(400, 0, 0), Vector3(-1, 0, 0))
	if hit == null:
		print("  FAIL: 射线未命中表面"); return 1
	var hp: Vector3 = hit
	var r := hp.length()
	print("  射线命中 r=%.1f (半径 256)" % r)
	if r < 200.0 or r > 300.0:
		print("  FAIL: 命中点不在表面附近"); return 1
	print("  OK: 射线求交命中表面")
	return 0


func _test_save_roundtrip() -> int:
	var c := _mk()
	c.AddEdit(Vector3(256, 0, 0), 4.0, 1, 2)
	c.AddEdit(Vector3(250, 10, 5), 3.0, 0, 1)
	var data := c.GetEdits()
	var c2 := _mk()
	c2.SetEdits(data)
	if c2.EditCount != 2:
		print("  FAIL: 存档往返编辑数不符 (%d)" % c2.EditCount); return 1
	var p := Vector3(256, 0, 0)
	if absf(c.SampleDensity(p) - c2.SampleDensity(p)) > 1e-6:
		print("  FAIL: 存档往返后密度不一致"); return 1
	print("  OK: 编辑存档往返一致 (%d 条)" % c2.EditCount)
	return 0


func _raycast(c: VoxelTerrainCore, from: Vector3, dir: Vector3) -> Variant:
	var d := dir.normalized()
	var t := 0.0
	var limit := 800.0
	var steps := 0
	while t < limit and steps < 2048:
		var p := from + d * t
		var den := c.SampleDensity(p)
		if den < 0.05:
			return p
		t += maxf(den, 0.1 + t * 0.002)
		steps += 1
	return null
