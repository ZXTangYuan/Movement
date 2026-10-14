extends Node
class_name ApexInteractableTest

## 阶段 2 自检：场地互动物。
##
## 验证每个互动物在其触发范围内确实改变了玩家速度（且方向/量级符合参数）。
##
## 运行：res://tests/apex_interactable_test.tscn
## 期望：APEX INTERACTABLE TEST PASS

const Geyser := preload("res://addons/apex_movement/interactables/apex_geyser.gd")
const JumpPad := preload("res://addons/apex_movement/interactables/apex_jump_pad.gd")
const Launcher := preload("res://addons/apex_movement/interactables/apex_launcher.gd")
const ConfigScript := preload("res://addons/apex_movement/core/ApexMoveConfig.cs")
const StateScript := preload("res://addons/apex_movement/core/ApexMoveState.cs")

const CharacterScript := preload("res://addons/apex_movement/character/apex_character.gd")

var _pass := 0
var _fail := 0
var _msgs: Array[String] = []


func _ready() -> void:
	print("=== ApexMovement 互动物自检 ===")
	_test_geyser()
	_test_jump_pad()
	_test_launcher_height()
	_test_zipline_path()

	print("")
	for m in _msgs:
		print(m)
	print("")
	if _fail == 0:
		print("APEX INTERACTABLE TEST PASS  (%d 项)" % _pass)
	else:
		print("APEX INTERACTABLE TEST FAIL  (通过 %d，失败 %d)" % [_pass, _fail])
	get_tree().quit(0 if _fail == 0 else 1)


func _make_player(pos: Vector3) -> CharacterBody3D:
	var p := CharacterBody3D.new()
	p.set_script(CharacterScript)
	p.position = pos
	p.config = ConfigScript.new()
	var col := CollisionShape3D.new()
	col.name = "CollisionShape3D"
	var cap := CapsuleShape3D.new()
	cap.radius = 0.4
	cap.height = 1.8
	col.shape = cap
	p.add_child(col)
	add_child(p)
	p._state = StateScript.new()
	p._state.Reset(pos, Vector3.UP)
	return p


func _test_geyser() -> void:
	var g = Geyser.new()
	g.position = Vector3(0, 0, 0)
	add_child(g)
	var p := _make_player(Vector3(0, 0.5, 0))
	# 手动调用 enter 回调（避开物理帧时序）
	g._on_enter(p)
	# 模拟几帧升力
	for i in 30:
		g._physics_process(1.0 / 60.0)
	var vy: float = p._state.Velocity.dot(Vector3.UP)
	_check("喷泉托起（获得向上速度）", vy > 0.5, "vy=%.2f m/s" % vy)
	_check("喷泉速度不超 liftSpeed",
		vy <= g.lift_speed + 0.01, "vy=%.2f 上限=%.2f" % [vy, g.lift_speed])
	g.queue_free()
	p.queue_free()


func _test_jump_pad() -> void:
	var jp = JumpPad.new()
	add_child(jp)
	var p := _make_player(Vector3(0, 0.5, 0))
	jp._on_enter(p)
	var vy: float = p._state.Velocity.dot(Vector3.UP)
	_check("跳板上抛", vy >= jp.launch_up_speed - 0.01,
		"vy=%.2f 期望≥%.2f" % [vy, jp.launch_up_speed])
	_check("跳板授予二段跳", p.config.AirJumpsEnabled, "AirJumpsEnabled=%s" % str(p.config.AirJumpsEnabled))
	jp.queue_free()
	p.queue_free()


func _test_launcher_height() -> void:
	var la = Launcher.new()
	add_child(la)
	var p := _make_player(Vector3(0, 0.5, 0))
	la.launch_height = 20.0
	la._on_enter(p)
	var vy: float = p._state.Velocity.dot(Vector3.UP)
	var g: float = p.config.Gravity
	# 由初速反推高度，应接近设定值
	var h: float = (vy * vy) / (2.0 * g)
	_check("弹射台高度 ≈ 设定值", absf(h - 20.0) < 1.0,
		"反推高度 %.2f m / 设定 20.00 m" % h)

	# 可调高度
	la.set_launch_height(40.0)
	var p2 := _make_player(Vector3(0, 0.5, 0))
	la._on_enter(p2)
	var vy2: float = p2._state.Velocity.dot(Vector3.UP)
	var h2: float = (vy2 * vy2) / (2.0 * g)
	_check("弹射台高度可调（20→40）", absf(h2 - 40.0) < 2.0,
		"反推高度 %.2f m / 设定 40.00 m" % h2)
	la.queue_free()
	p.queue_free()
	p2.queue_free()


func _test_zipline_path() -> void:
	var z = preload("res://addons/apex_movement/interactables/apex_zipline.gd").new()
	z.start_point = Vector3(0, 8, 0)
	z.end_point = Vector3(0, 8, 40)
	z.is_curved = false
	add_child(z)
	# 端点采样
	var a: Vector3 = z._sample(0.0)
	var b: Vector3 = z._sample(1.0)
	_check("滑索端点采样正确",
		a.distance_to(z.start_point) < 0.01 and b.distance_to(z.end_point) < 0.01,
		"t=0→%s  t=1→%s" % [str(a), str(b)])

	# S 型：路径点应被纳入
	var z2 = preload("res://addons/apex_movement/interactables/apex_zipline.gd").new()
	z2.start_point = Vector3(0, 8, 0)
	z2.waypoints = PackedVector3Array([Vector3(-8, 12, 10), Vector3(8, 16, 20)])
	z2.end_point = Vector3(0, 8, 40)
	z2.is_curved = true
	add_child(z2)
	var mid: Vector3 = z2._sample(0.5)
	_check("S 型滑索包含弯曲路径", absf(mid.x) > 1.0 or mid.y > 10.0,
		"中点 %s（非直线）" % str(mid))
	z.queue_free()
	z2.queue_free()


func _check(name: String, ok: bool, detail: String) -> void:
	if ok:
		_pass += 1
		_msgs.append("  [PASS] %s  (%s)" % [name, detail])
	else:
		_fail += 1
		_msgs.append("  [FAIL] %s  (%s)" % [name, detail])