# GPU Raymarch 可行性评估（实测）

> 结论先行：**能。而且在本机实测中，GPU raymarch 明显优于当前的 CPU Surface Nets。**
> 本文所有数字都是在本机（RTX 4070 Laptop，Godot 4.7 mono，Vulkan）实际跑出来的，不是估算。

---

## 1. 结论

| 问题 | 答案 |
|---|---|
| Godot 4.7 能做 GPU raymarch 吗？ | **能。** compute shader + 3D 存储纹理 + spatial shader raymarch 全部实测可用 |
| 有现成插件吗？ | **没有**适合的。Zylann/godot_voxel 是**网格**方案（Transvoxel），不是 raymarch；Godot 4 的 SDF raymarch 插件为空缺 |
| 要自制吗？ | 需要自制，但**核心机制已验证跑通**（见 §3） |
| 值得换吗？ | **值得**——实测 raymarch 3.54 ms/帧 vs CPU meshing 111 ms/块（§4） |

---

## 2. 能力实测（本机，Godot 4.7 mono）

探测脚本 `tests/probe_gpu.gd` 实测输出：

```
RESULT: local RenderingDevice OK
  device: NVIDIA GeForce RTX 4070 Laptop GPU / NVIDIA
RESULT: compute shader compiled OK
RESULT: dispatch+readback OK  v[0]=0.0 v[63]=63.0     ← compute 派发+回读正确
RESULT: 3D texture valid = true                        ← 3D 纹理可创建
RESULT: R32F 3D storage 支持 = true                    ← 3D 存储纹理（体素页）可用
```

关键 API（4.7 实际签名，与旧文档不同）：
- `RDShaderSource.new()` → `source_compute = "<GLSL>"`（**不要**写 `#[compute]`，那是给 `.glsl` 文件用的）
- `rd.shader_compile_spirv_from_source(src, false)` → `RDShaderSPIRV`
- `spirv.get_stage_compile_error(RenderingDevice.SHADER_STAGE_COMPUTE)` → 检查编译错误
- `rd.shader_create_from_spirv(spirv)` → shader RID
- `var cl := rd.compute_list_begin()` → **返回 list id**，之后所有 `compute_list_*` 都要传它
- `rd.compute_list_dispatch(cl, x, y, z)` → 派发

**注意**：headless（`--headless`）用 dummy 渲染器，**没有** RenderingDevice。测试 GPU 必须带窗口或 `--rendering-driver vulkan`。

---

## 3. PoC：GPU raymarch 行星（已跑通）

`assets/shaders/raymarch_planet.gdshader` + `scenes/levels/raymarch_demo.tscn`：
- 一个 `cull_disabled` 的立方体当光线步进画布
- fragment shader 里对「球 + 球面 fbm 噪声位移」的 SDF 做 160 步 raymarch
- 命中后算法线、漫反射、简易 AO
- **写 `DEPTH`**，让 raymarch 表面参与 Godot 深度缓冲

实测截图 [`raymarch_poc.png`](raymarch_poc.png)：噪声位移的行星球体渲染正确，
**蓝色参照球被正确遮挡在行星后面** —— 证明深度写入生效、与普通网格正确合成。

---

## 4. 性能实测（决定性数据）

### GPU raymarch（800×600，160 步，4 octave fbm）
```
median 3.54 ms (282 fps), p95 4.55 ms, draw calls = 2
```

### CPU Surface Nets（GDScript，32³ 块）
```
32³ chunk meshing:  111.02 ms, verts=1409
rebuild after 1 edit: 132.57 ms
行星表面需要块数: ~12868 块 (16³@0.5m)
```

**对比**：GPU 每帧 3.54 ms 渲染**整颗**行星；CPU 重建**一块** 32³ 就要 111–133 ms。
差距是**两个数量级**，而且 CPU 的 meshing 还在主线程（GDScript），会造成明显卡顿。

原因很直白：
- raymarch 的着色器里 fbm 是 4 次迭代、每像素约 160 步 → GPU 数千核心并行，很轻松。
- CPU meshing 要对 32³=32768 个采样点逐个跑 GDScript 函数 + 提取等值面 + 建数组 → 单线程，慢。

---

## 5. 自制 GPU raymarch 的完整架构

要真正做成 Astroneer 那样，需要这些件（都有对应 Godot 设施）：

```
① 体素存储   ImageTexture3D (R32F / RGBA8) 稀疏分页
              ↕ 实测 R32F 3D storage 支持 = true
② 页表       texture 2D/3D 存页索引（PageTableIndirection 对应物）
③ 地形修改   compute shader 把 brush SDF 光栅化进体素页（FVoxelRasterComputeCS 对应物）
              ↕ 实测 compute dispatch+readback 正确
④ 渲染       spatial shader raymarch（PoC 已验证）
             或 CompositorEffect 做全屏 pass（Godot 4.7 有 CompositorEffect，18 处引用）
⑤ 碰撞       无网格 → 自己写 raycast（沿光线步进 SDF）+ 胶囊 vs SDF 求解
⑥ 程序化基线 UProceduralExpression 对应物（已有 ProceduralField，需搬进 shader）
```

**工作量最大的两块**：③ 稀疏分页内存管理，⑤ 无网格碰撞。渲染（④）反而是最容易的，PoC 已证明。

---

## 6. 诚实的风险清单

| 风险 | 说明 | 缓解 |
|---|---|---|
| **无网格碰撞** | 不能用 `ConcavePolygonShape3D`；角色/载具/物理都要自己写 SDF 查询 | 自己实现 raycast + 胶囊 SDF；这是最大工作量 |
| **TDR 超时** | 长 compute 会触发 Windows 显卡驱动重置 | 分多次 dispatch，别一次算太多 |
| **Mobile 支持差** | Godot 文档明说 mobile compute "generally poor" | 只做桌面 |
| **不能 RenderDoc 调试** | local RenderingDevice 无法用 RenderDoc 抓帧 | 靠回读 buffer 做数值验证 |
| **Godot 版本 API 漂移** | 4.7 的 API 与网上旧教程不一致（本文 §2 已修正） | 以本机实测签名为准 |
| **工作量** | 完整 raymarch 体素引擎远大于 Surface Nets | 分阶段：先 raymarch 渲染 + 简单编辑，再补分页/碰撞 |

---

## 7. 建议路线（推荐）

不要"全有或全无"，而是**混合**：

1. **渲染层换 raymarch**（收益最大、风险最低）
   保留现有 C# 网络核心与差分存档模型，把地形**渲染**改成 raymarch。
   程序化基线（`ProceduralField`）搬进 shader；玩家编辑走 compute shader 写进体素页。
2. **编辑层用 compute**（对应 `FVoxelRasterComputeCS`）
   挖掘/填土 = 一次 compute dispatch 写页，**不重建任何网格** → 丝滑。
3. **碰撞层最后做**（无网格，自己写 SDF raycast）
   这是最难的，但可以先用"近似高度场 + 射线步进"顶着，逐步完善。
4. **保留 Surface Nets 作为回退**
   已有的 CPU 管线不要删；低端机 / 调试期可切换。

**什么情况下不换**：如果目标是尽快做出可联机、可建造的**可玩原型**，
Surface Nets 仍是更稳的起点（碰撞免费、易调试）。raymarch 的收益主要在"大地图 + 高频丝滑挖填"的观感。

---

## 8. 相关文件

- 能力探测：`tests/probe_gpu.gd` / `.tscn`
- raymarch PoC shader：`assets/shaders/raymarch_planet.gdshader`
- PoC 场景：`scenes/levels/raymarch_demo.gd` / `.tscn`
- PoC 截图：`docs/reference/raymarch_poc.png`
- 性能实测：`tests/perf.gd`（GPU）、`tests/perf_cpu.gd`（CPU）
