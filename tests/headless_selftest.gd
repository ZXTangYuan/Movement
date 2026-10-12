extends Node
## 无头自检：验证 P0/P1 核心（程序化密度 / Surface Nets 网格 / 挖填差分）真的工作。
## 用法：godot --headless --path . res://tests/headless_selftest.tscn

func _ready() -> void:
	var failures := 0
	failures += _test_procedural_field()
	failures += _test_surface_nets()
	failures += _test_dig_flips_density()
	failures += _test_edit_determinism()
	print("=== SELFTEST %s (%d failures) ===" % ["PASS" if failures == 0 else "FAIL", failures])
	get_tree().quit(failures)


func _test_procedural_field() -> int:
	var def := PlanetDefinition.create_default_earthlike()
	var field := ProceduralField.new(def)
	var center := field.density(Vector3.ZERO)
	var outside := field.density(Vector3(def.radius * 3.0, 0, 0))
	print("density center=%.2f outside=%.2f" % [center, outside])
	if center >= 0.0:
		print("  FAIL: 球心应为实体"); return 1
	if outside <= 0.0:
		print("  FAIL: 远处应为空气"); return 1
	# 确定性：同 seed 两次采样必须一致。
	var field2 := ProceduralField.new(def)
	var p := Vector3(100, 50, -30)
	if absf(field.density(p) - field2.density(p)) > 1e-6:
		print("  FAIL: 程序化场不确定"); return 1
	print("  OK: 程序化场正确且确定")
	return 0


func _test_surface_nets() -> int:
	# 解析球：density = |p - center| - 4，center 在网格中心 (8,8,8)。
	var cells := 16
	var stride := cells + 1
	var center := Vector3(8, 8, 8)
	var radius := 4.0
	var density := PackedFloat32Array()
	density.resize(stride * stride * stride)
	for z in stride:
		for y in stride:
			for x in stride:
				density[x + y * stride + z * stride * stride] = (Vector3(x, y, z) - center).length() - radius
	var res := SurfaceNets.build(density, cells, 1.0, Vector3.ZERO)
	var mesh: ArrayMesh = res["mesh"]
	if mesh == null:
		print("  FAIL: 球体未生成网格"); return 1
	var verts: PackedVector3Array = mesh.surface_get_arrays(0)[Mesh.ARRAY_VERTEX]
	print("  surface nets verts=%d tris=%d" % [verts.size(), res["faces"].size() / 3])
	if verts.size() < 50:
		print("  FAIL: 顶点过少"); return 1
	# 所有顶点应落在以 center 为心、半径 4 的球面上（容差 0.5 体素）。
	var max_err := 0.0
	for v in verts:
		max_err = maxf(max_err, absf((v - center).length() - radius))
	print("  max radial error=%.3f" % max_err)
	if max_err > 0.5:
		print("  FAIL: 网格偏离球面过多"); return 1
	print("  OK: Surface Nets 生成球面正确")
	return 0


func _test_dig_flips_density() -> int:
	# 挖填的正确判据不是“顶点变少”（挖坑会增加表面积），而是：
	#   挖掘后，原本实体的中心点应变为空气。
	var def := PlanetDefinition.create_default_earthlike()
	var field := ProceduralField.new(def)

	var chunk := VoxelChunk.new()
	chunk.cells = 16
	chunk.voxel_size = 0.5
	chunk.origin = Vector3(def.radius - 4.0, -4.0, -4.0)
	chunk.rebuild(field, [])

	var dig_center := chunk.origin + Vector3(4, 4, 4)
	var d_before := field.density(dig_center)
	if d_before >= 0.0:
		print("  FAIL: 挖掘点初始应为实体 (d=%.2f)" % d_before); return 1

	var edit := TerrainEdit.new()
	edit.Type = VoxelWorld.EDIT_SUBTRACT
	edit.Shape = VoxelWorld.SHAPE_SPHERE
	edit.Position = dig_center
	edit.Radius = 3.0
	chunk.rebuild(field, [edit])
	var d_after := chunk.sample_edited_density(dig_center)
	print("  dig: density %.2f -> %.2f" % [d_before, d_after])
	if d_after <= 0.0:
		print("  FAIL: 挖掘后中心点应为空气"); return 1
	print("  OK: 挖掘把实体变为空气")
	return 0


func _test_edit_determinism() -> int:
	# 同一组编辑，任意顺序应用，结果必须一致（联机确定性的基础）。
	var def := PlanetDefinition.create_default_earthlike()
	var field := ProceduralField.new(def)
	var e1 := TerrainEdit.new()
	e1.Type = VoxelWorld.EDIT_SUBTRACT; e1.Position = Vector3(256, 0, 0); e1.Radius = 4.0
	var e2 := TerrainEdit.new()
	e2.Type = VoxelWorld.EDIT_ADD; e2.Position = Vector3(256, 2, 0); e2.Radius = 4.0

	var a := VoxelChunk.new(); a.cells = 16; a.voxel_size = 0.5
	a.origin = Vector3(252, -4, -4); a.rebuild(field, [e1, e2])
	var b := VoxelChunk.new(); b.cells = 16; b.voxel_size = 0.5
	b.origin = Vector3(252, -4, -4); b.rebuild(field, [e2, e1])

	var probe := Vector3(256, 1, 0)
	var da := a.sample_edited_density(probe)
	var db := b.sample_edited_density(probe)
	print("  order A=%.4f B=%.4f" % [da, db])
	if absf(da - db) > 1e-6:
		print("  FAIL: 编辑顺序影响了结果（非确定）"); return 1
	print("  OK: 编辑与顺序无关（确定性）")
	return 0
