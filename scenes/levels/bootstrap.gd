extends Node3D
## 主场景：太阳系（恒星 + 八大行星）+ 第一人称玩家 + 环境。
##
## 玩法侧（GDScript）通过 C# NetworkCore 边界通信；地形渲染走 GPU raymarch。
##
## 控制：
##   WASD 移动 | 鼠标视角 | Space 上升/Ctrl 下落 | F 切飞行 | Shift 加速
##   左键挖 | 右键填 | Esc 释放鼠标 | G 传送到下颗行星
##   宇宙中：镜头完整三维自由旋转（无限制）
##   进入星球重力场：镜头限制为 [看向星球中心, 抬头 90°]

@export var time_scale: float = 1.0
@export var auto_host: bool = false

var solar_system: SolarSystem
var player: FirstPersonPlayer
var _world_env: WorldEnvironment
var _current_planet_index: int = 0


func _ready() -> void:
	if auto_host:
		NetworkCore.Host(NetworkCore.AuthorityMode.ClientAuthoritative, 34567, 8)

	_setup_environment()

	solar_system = SolarSystem.new()
	solar_system.name = "SolarSystem"
	solar_system.time_scale = time_scale
	add_child(solar_system)

	# 玩家：出生在地球（细节行星）表面
	var planet := solar_system.detail_planet
	var spawn := _find_spawn(planet)
	player = FirstPersonPlayer.new()
	player.name = "Player"
	player.solar_system = solar_system
	player.position = spawn
	add_child(player)
	# 初始朝切平面方向看（望向地平线，而不是低头看地）
	var planet_center := planet.global_position
	var up: Vector3 = (spawn - planet_center).normalized()
	var tangent: Vector3 = up.cross(Vector3.UP)
	if tangent.length() < 0.1:
		tangent = up.cross(Vector3.RIGHT)
	tangent = tangent.normalized()
	# 略微俯视（-10°）以便看清地面，但主要是水平看远方
	player.look_towards((tangent - up * 0.18).normalized())

	# 网络桥接：世界系统只发信号，由本场景（ProjectA）决定是否广播。
	if planet.has_signal("edit_created"):
		planet.edit_created.connect(_on_planet_edit_created)
	NetworkCore.EditReceived.connect(_on_remote_edit)

	# 打印星系概览
	var names := []
	for p in solar_system.planets:
		names.append("%s(r=%.0f,g=%.1f,orb=%.0f)" % [
			p.definition.display_name, p.definition.radius,
			p.definition.gravity_strength, p.orbit_radius])
	print("[bootstrap] 太阳系就绪：恒星 + %d 行星" % solar_system.planets.size())
	print("  " + " | ".join(names))
	print("[bootstrap] 细节行星 = %s，玩家出生 r=%.1f" % [
		planet.definition.display_name, spawn.length()])


func _process(_delta: float) -> void:
	# 每帧同步细节行星的地形画布（公转/位置变化）
	if solar_system != null and solar_system.detail_planet != null:
		var p := solar_system.detail_planet
		if p.terrain != null:
			p.terrain.sync_to_planet()


func _setup_environment() -> void:
	_world_env = WorldEnvironment.new()
	_world_env.name = "WorldEnvironment"
	var env := Environment.new()
	# 深空背景
	env.background_mode = Environment.BG_COLOR
	env.background_color = Color(0.004, 0.006, 0.014)
	env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.ambient_light_color = Color(0.10, 0.12, 0.18)
	env.ambient_light_energy = 0.28
	# 极淡的星际雾（太空不该有浓雾）
	env.fog_enabled = false
	env.tonemap_mode = Environment.TONE_MAPPER_FILMIC
	env.tonemap_exposure = 1.0
	_world_env.environment = env
	add_child(_world_env)


func _unhandled_input(event: InputEvent) -> void:
	if event.is_action_pressed("teleport_next"):
		_teleport_to_next_planet()


## 传送到下一颗行星表面（验证多行星重力场）。
func _teleport_to_next_planet() -> void:
	if solar_system == null or player == null:
		return
	_current_planet_index = (_current_planet_index + 1) % solar_system.planets.size()
	var p := solar_system.planets[_current_planet_index]
	var spawn := _find_spawn(p)
	player.global_position = spawn
	player.bind_to_planet(p)
	print("[bootstrap] 传送到 %s（重力 %.1f m/s²）" % [
		p.definition.display_name, p.definition.gravity_strength])


func _on_remote_edit(planet_id: int, edit) -> void:
	if solar_system != null and solar_system.detail_planet != null:
		solar_system.detail_planet.apply_edit(edit)


## 世界系统产生本地编辑 → 交给网络层广播（解耦桥接）。
func _on_planet_edit_created(edit) -> void:
	NetworkCore.BroadcastEdit(edit)


## 从行星外沿 -X 向质心打射线，命中表面后抬高作为出生点。
## （射线法比"找空气点"可靠：直接落在真实地形表面上）
func _find_spawn(planet: Planet) -> Vector3:
	var def := planet.definition
	var center := planet.global_position
	var dir := Vector3(1, 0, 0)
	var from := center + dir * (def.radius + def.terrain_amplitude * 3.0)
	var hit = planet.raycast(from, -dir, def.terrain_amplitude * 8.0 + 50.0)
	if hit != null:
		var hp: Vector3 = hit
		return hp + dir * 4.0
	# 兜底：表面 + 一点高度
	return center + dir * (def.radius + 5.0)
