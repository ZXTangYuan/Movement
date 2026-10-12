extends Node
## 验证 GLSL 基线与 C# 基线逐点一致（无限地形正确性的关键）。
## 做法：用 local RenderingDevice 跑 compute（内含同一份 noise include），
## 写 storage buffer，回读后与 C# 结果比对。

const COORDS := [
	[256.0, 0.0, 0.0],
	[250.0, 10.0, 5.0],
	[0.0, 256.0, 0.0],
	[200.0, 150.0, 100.0],
	[260.0, -30.0, 20.0],
	[12.5, 255.9, -3.25],
	[-100.0, -220.0, 88.0],
]

func _ready() -> void:
	var rd := RenderingServer.create_local_rendering_device()
	if rd == null:
		print("SKIP: 无 local RenderingDevice")
		get_tree().quit(0)
		return

	var core := VoxelTerrainCore.new()
	core.Configure(256.0, 0.02, 5, 26.0, 20261013)

	# GLSL compute：对每个坐标算基线，写 storage buffer
	var n := COORDS.size()
	var code := _build_compute_glsl(n)
	var src := RDShaderSource.new()
	src.language = RenderingDevice.SHADER_LANGUAGE_GLSL
	src.source_compute = code
	var spirv := rd.shader_compile_spirv_from_source(src, false)
	var err: String = spirv.get_stage_compile_error(RenderingDevice.SHADER_STAGE_COMPUTE)
	if not err.is_empty():
		print("GLSL 编译失败: ", err)
		get_tree().quit(1)
		return
	var shader := rd.shader_create_from_spirv(spirv)

	# 输入 buffer（坐标）+ 输出 buffer（密度）
	var in_bytes := PackedFloat32Array()
	for c in COORDS:
		in_bytes.append(c[0]); in_bytes.append(c[1]); in_bytes.append(c[2])
	in_bytes.append(0.0)  # padding 到 4 的倍数
	var in_buf := rd.storage_buffer_create(in_bytes.to_byte_array().size(), in_bytes.to_byte_array())
	var out_buf := rd.storage_buffer_create(n * 4)

	var u0 := RDUniform.new()
	u0.uniform_type = RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER
	u0.binding = 0
	u0.add_id(in_buf)
	var u1 := RDUniform.new()
	u1.uniform_type = RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER
	u1.binding = 1
	u1.add_id(out_buf)
	var uset := rd.uniform_set_create([u0, u1], shader, 0)
	var pipe := rd.compute_pipeline_create(shader)
	var cl := rd.compute_list_begin()
	rd.compute_list_bind_compute_pipeline(cl, pipe)
	rd.compute_list_bind_uniform_set(cl, uset, 0)
	rd.compute_list_dispatch(cl, n, 1, 1)
	rd.compute_list_end()
	rd.submit()
	rd.sync()

	var out := rd.buffer_get_data(out_buf).to_float32_array()
	var mismatches := 0
	print("坐标 | C# 基线 | GLSL 基线 | 差")
	for i in n:
		var p := Vector3(COORDS[i][0], COORDS[i][1], COORDS[i][2])
		var cs: float = core.Baseline(p)
		var gl: float = out[i]
		var diff: float = absf(cs - gl)
		var flag := "OK" if diff < 0.001 else "MISMATCH"
		if diff >= 0.001: mismatches += 1
		print("(%.1f,%.1f,%.1f) | %.5f | %.5f | %.5f %s" % [p.x, p.y, p.z, cs, gl, diff, flag])
	print("=== NOISE MATCH %s (%d mismatches) ===" % ["PASS" if mismatches == 0 else "FAIL", mismatches])
	get_tree().quit(mismatches)


func _build_compute_glsl(n: int) -> String:
	# 内联噪声实现（与 terrain_noise.gdshaderinc 相同），避免 include 依赖
	return """
#version 450
layout(local_size_x = 1) in;
layout(set = 0, binding = 0, std430) restrict readonly buffer InBuf { float coords[]; } inb;
layout(set = 0, binding = 1, std430) restrict writeonly buffer OutBuf { float dens[]; } outb;

uint h(uint x) { x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16; return x; }
float lat(int x, int y, int z, int seed) {
    uint hx = h(uint(x) * 0x9E3779B1u);
    uint hy = h(uint(y) * 0x85EBCA77u);
    uint hz = h(uint(z) * 0xC2B2AE3Du);
    uint hs = h(uint(seed) * 0x27D4EB2Fu);
    uint v = h(hx ^ hy ^ hz ^ hs);
    return float(v >> 8u) * (1.0 / 16777216.0);
}
float sm(float t) { return t * t * (3.0 - 2.0 * t); }
float vn(vec3 p, int seed) {
    vec3 f = floor(p); ivec3 i = ivec3(f);
    vec3 t = vec3(sm(p.x-f.x), sm(p.y-f.y), sm(p.z-f.z));
    float c000=lat(i.x,i.y,i.z, seed), c100=lat(i.x+1,i.y,i.z, seed);
    float c010=lat(i.x,i.y+1,i.z, seed), c110=lat(i.x+1,i.y+1,i.z, seed);
    float c001=lat(i.x,i.y,i.z+1, seed), c101=lat(i.x+1,i.y,i.z+1, seed);
    float c011=lat(i.x,i.y+1,i.z+1, seed), c111=lat(i.x+1,i.y+1,i.z+1, seed);
    float x00=mix(c000,c100,t.x), x10=mix(c010,c110,t.x);
    float x01=mix(c001,c101,t.x), x11=mix(c011,c111,t.x);
    return mix(mix(x00,x10,t.y), mix(x01,x11,t.y), t.z);
}
float fbm(vec3 p, int oct, int seed) {
    float a=0.5, s=0.0;
    for (int i=0;i<8;i++){ if(i>=oct) break; s+=a*vn(p, seed); p*=2.03; a*=0.5; }
    return s;
}
float baseline(vec3 p, float radius, float freq, int oct, float amp, int seed) {
    float r = max(length(p), 1e-4);
    float dy = p.y / r;
    float n = fbm(p * freq, oct, seed);
    float latf = 1.0 - 0.12 * abs(dy);
    return (r - radius) + n * amp * latf;
}
void main() {
    uint i = gl_GlobalInvocationID.x;
    if (i >= %d) return;
    vec3 p = vec3(inb.coords[i*3], inb.coords[i*3+1], inb.coords[i*3+2]);
    outb.dens[i] = baseline(p, 256.0, 0.02, 5, 26.0, 20261013);
}
""" % n
