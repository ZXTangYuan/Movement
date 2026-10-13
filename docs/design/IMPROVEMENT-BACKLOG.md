# 改进清单 / 已知技术债（阶段二待办）

> 记录时间：2026-10-13。本文汇总**已确认存在、但本轮未修（或只修了简单部分）**的问题，
> 供后续排期。每条都给出：现状 / 证据（文件:行）/ 影响 / 建议修法 / 工作量。
>
> 定位与 `BLOCKERS.md` 的区别：`BLOCKERS.md` 是**需要你拍板**的决策项；
> 本文是**已定性、可直接开工**的工程债（含功能性缺口）。
>
> 本轮已修的小问题见文末 §6「本轮已修」。

---

## I1. raymarch 地形的光照方向是硬编码的 🔴（功能性缺口，优先级最高）

- **现状**：`raymarch_terrain.gdshader` 用 `uniform vec3 sun_dir = vec3(0.45, 0.8, 0.35)`
  做漫反射，但该 uniform **全项目无人写入**——`RaymarchTerrain._push_uniforms()` /
  `sync_to_planet()` 都只设 `planet_radius` / `noise_*` / `world_seed` / `delta_*`，
  **没有** `sun_dir`。而且该 shader 是 `render_mode unshaded`，
  `DirectionalLight3D`（恒星）**完全不参与**它的着色。
- **证据**：
  - `addons/unreal_world/shaders/raymarch_terrain.gdshader:32`（uniform 定义）、`:145`（`L = normalize(sun_dir)`）
  - `addons/unreal_world/voxel/raymarch_terrain.gd:35-44`（`_push_uniforms` 无 sun_dir）
  - 全仓 grep `sun_dir` → 仅上述 2 处，**无任何 `set_shader_parameter("sun_dir", …)`**
- **影响**：README / 阶段总结宣称的「恒星真实方向光照」**只作用于代理球与 MESH 网格**；
  玩家脚下真正踩的 raymarch 地形光照是**固定方向**，**不随太阳公转 / 昼夜变化**。
  这是「宣称已实现但实际未生效」的功能缺口，最值得优先修。
- **建议修法**（二选一）：
  1. **保留 unshaded + 手动传光**：在 `Star.aim_at()` 求出方向后，由 `Planet`/
     `bootstrap` 把 `star.get_sun_direction()` 推进 `RaymarchTerrain`，再 `set_shader_parameter("sun_dir", dir)`。
     改动最小，且与现有 `unshaded` 结构一致。
  2. **改用受光材质**：去掉 `unshaded`，让 shader 输出 `NORMAL`/`ALBEDO`，
     由 `DirectionalLight3D` 参与光照。观感更统一，但要处理 raymarch 写 `DEPTH`
     与延迟/前向光照的交互，风险较大。
- **工作量**：方案 1 ≈ 30 分钟（含一个新增回归测试：转 90° 后地表亮度/方向变化）；
  方案 2 ≈ 半天，且需回归画面。
- **验收**：新增 `tests/verify_sun_light.tscn`——改变恒星方向，断言 raymarch 像素
  的入射光方向随之改变（或截图对比）。

---

## I2. 兴趣网格（InterestGrid）是死代码，未接入 🟡

- **现状**：`NetworkCore` 暴露了 `ReportPeerPosition` / `GetInterestedPeers` 两个 API，
  但**全项目无任何调用者**；`RpcTerrainEdit` 是**无差别广播给所有 peer**，
  没有按兴趣裁剪。
- **证据**：
  - `scripts/net/NetworkCore.cs:152-162`（两个 API 定义）
  - `scripts/net/InterestGrid.cs`（实现完整，单元可测）
  - 全仓 grep `ReportPeerPosition` / `GetInterestedPeers` → **只有定义处，无调用**
  - `NetworkCore.cs:134-147`（`BroadcastEdit` 走 `Rpc(...)` 无差别广播）
- **影响**：设计文档 §6.2 与 README「可扩展性根本来源」的描述目前**只是「代码就绪」**，
  可扩展性**尚未接线**。当前对少量玩家无影响（广播量小），
  但「只同步附近玩家的编辑」这一核心卖点**未生效**。
- **建议修法**：
  1. 玩法侧（`bootstrap` / 玩家）周期性调用 `ReportPeerPosition(peerId, planetLocalPos)`。
  2. 服务器权威分支里，`Validate` 通过后**不再** `Rpc` 全体，而是
     `GetInterestedPeers(edit.Position)` 得到 peer 列表，逐个 `RpcId` 定向发送。
  3. 加入/退出时维护网格（`RemovePeer` 已接，`SetPeerPosition` 待接）。
- **工作量**：半天（含协议侧定向发送 + 一个双进程/模拟测试）。
- **备注**：需要「兴趣半径」与「编辑半径」的合并策略（当前 `radiusCells=1`、
  网格 256m，对 3m 的挖掘过粗，建议按 `edit.Radius` 动态扩格）。

---

## I3. 存在两套互不相同的地形实现，「同一密度场」只对其中一套成立 🟡

- **现状**：
  - **RAYMARCH 路径**：自研 `voxel/TerrainNoise.cs`，与 `shaders/terrain_noise.gdshaderinc`
    **逐位一致**（有 `tests/probe_noise_match` 验证）——渲染与碰撞共用同一密度场。
  - **MESH 回退路径**：`voxel/procedural_field.gd` 用 **`FastNoiseLite`（`TYPE_SIMPLEX_SMOOTH`）**，
    是**第三套、数值不同**的噪声。
- **证据**：
  - `addons/unreal_world/voxel/procedural_field.gd:18-24`（FastNoiseLite）
  - `addons/unreal_world/voxel/TerrainNoise.cs:12`（自研，明确「不使用任何库噪声」）
  - `tests/headless_selftest.gd`（测 `ProceduralField`）与
    `tests/headless_selftest_gpu.gd`（测 `VoxelTerrainCore`）**测的是两种不同地形**。
- **影响**：作为**回退**可用（无 RD 时 MESH 自洽，玩家不与 RAYMARCH 混用），
  但 README / 设计文档「渲染与碰撞共用同一密度场」这句话
  **在有 RenderingDevice 时成立、无 RD 时不成立**。
- **建议修法**：
  1. **短期（推荐，文档澄清）**：在 `raymarch-implementation.md` / `README.md` 注明
     「两套密度实现」：RAYMARCH=自研噪声（CPU/GPU 一致）；MESH=FastNoiseLite（仅回退，独立自洽）。
  2. **长期（可选）**：让 `ProceduralField` 也改用 `TerrainNoise`，消除第三套噪声，
     使 MESH 与 RAYMARCH 同场。代价是丢掉 FastNoiseLite 的细节，MESH 观感会变。
- **工作量**：澄清文档 ≈ 10 分钟；统一实现 ≈ 半天（含更新 `headless_selftest` 的预期值）。

---

## I4. ~~默认渲染驱动是 d3d12，但一致性结论只在 Vulkan 下验证过~~ ✅ 已澄清（2026-10-13 补测）

- **现状**：`project.godot` 默认 `rendering_device/driver.windows="d3d12"`，
  而所有噪声一致性 / GPU 测试命令都显式带 `--rendering-driver vulkan`。
- **证据**：`project.godot:106`；`README.md` 与 `DEV-STAGE-1-SUMMARY.md` 的验证命令全部 `--rendering-driver vulkan`。
- **影响**：`probe_noise_match` 的「逐位一致」结论**只在 Vulkan 下验证过**；
  玩家若用 d3d12 运行（默认），未验证 C# 与 GLSL 是否仍逐位一致。
- **本轮已补测（实测通过）**：
  ```
  --rendering-driver d3d12  probe_noise_match.tscn     → NOISE MATCH PASS (0 mismatches)
  --rendering-driver d3d12  headless_selftest_gpu.tscn → CORE SELFTEST PASS (0 failures)
  --rendering-driver d3d12  verify_dig_visible.tscn    → DIG VISIBLE PASS (0 failures)
  ```
  结论：**Vulkan 与 d3d12 下均逐位一致**，默认驱动 d3d12 无需更改。
  若未来换 GPU/驱动再出现分歧，按同法补测。

---

## 5. 其他低优先（观察项，暂不修）

| # | 现象 | 位置 | 说明 |
|---|---|---|---|
| 5.1 | ~~`_up_target` 只写不读（死变量）~~ | `first_person_player.gd` | **本轮已删** |
| 5.2 | ~~`delta_origin` 坐标空间错位~~ | `raymarch_terrain.gd` | **本轮已修**，且比原报告严重（见 §6） |
| 5.3 | `create_default_earthlike()` 仍是旧值 | `planet_definition.gd:47-49` | 遗留兜底工厂：`display_name="Terran"`、`radius=256`、`amplitude=26`。**不影响**太阳系（`SolarSystem` 不用它），但名字与现状不符，后续可改名为 `create_default()` 或同步为新值 |
| 5.4 | 材质单一 / 无群系贴图 | shader `terrain_color` | 按高度/坡度 4 色混合（已列于 `BLOCKERS.md §D`） |
| 5.5 | 无 UI / 挖掘无粒子反馈 | — | 已列于 `BLOCKERS.md §D` |

---

## 6. 本轮已修（含实际严重度高于报告的一处）

### 6.1 ★ `delta_origin` 坐标空间错位 —— 挖填在 raymarch 里完全不可见 🔴
- **报告原文**：仅描述为「recenter 后、sync 前渲染会错位**一帧**」。
- **实测真相（严重得多）**：`sync_to_planet()` **每帧被 `bootstrap` 调用**，
  它把 `delta_origin` 写成**世界坐标**（`planet_center + volume.delta_origin()`），
  但 shader 的 `density_at()` 先算 `p = p_world - planet_center`（行星局部），
  再算 `uv = (p - delta_origin) / size`——**delta_origin 必须是行星局部坐标**。
  - 后果：`uv.z ≈ -226`（远在 `[0,1]` 之外）→ 条件 `all(greaterThan(uv,0))` 恒假
    → **delta 纹理永远采样不到** → 挖/填在 raymarch 里**完全看不见**，
    而碰撞（C# 稀疏列表）**看得到**。即「挖了没反应、但人会掉进自己挖的洞」。
- **证据（实测探针，修前）**：
  ```
  uniform delta_origin (set)  = (368.078, -32.0, 14468.0)   # 世界坐标（错）
  shader uv (CURRENT uniform) = (0.496353, 0.5, -226.0625)  in_range=false
  => shader sees delta = 0.0000 (RENDER)
  => collision sees delta = 6.0000
  => RESULT: RENDER != COLLISION (dig invisible in raymarch)
  ```
- **修法**：`addons/unreal_world/voxel/raymarch_terrain.gd` 的 `sync_to_planet()` 改回传
  **行星局部坐标** `volume.delta_origin()`（与 `_push_uniforms()` / `_on_recenter()` 一致）。
- **修后实测**：
  ```
  shader uv (CURRENT uniform) = (0.496353, 0.5, 0.5)  in_range=true
  => shader sees delta   = 6.0000
  => collision sees delta = 6.0000
  => RESULT: consistent
  ```
- **影响面**：这是**玩家能直接感知的核心 bug**（「挖不动」的错觉），
  本轮一并修掉。回归方式：正式化一个 `tests/verify_dig_visible.tscn`（见 §7 待办）。

### 6.2 死变量清理
- `scripts/player/first_person_player.gd`：删除只写不读的 `_up_target`（原 L43，及两处赋值）。

### 6.3 文档过期引用
- `addons/unreal_world/voxel/TerrainNoise.cs:9`：`assets/shaders/…` → `addons/unreal_world/shaders/…`
- `addons/unreal_world/shaders/terrain_noise.gdshaderinc:1`：`scripts/voxel/TerrainNoise.cs` → `addons/unreal_world/voxel/TerrainNoise.cs`
- `README.md`：插件「README」死链 → 改为指向插件仓库 + 说明「源码即文档」
- `docs/design/BLOCKERS.md`：B2（4 行星→8 行星、Terran→Earth/index 2）、
  C1（半径 256m→320–1050、轨道 2600–5000→7000–54000）、F2/F4（原点/幅度数值）已同步

---

## 7. 建议的下一步（按性价比）

1. **I1 sun_dir**（半小时，直接兑现「恒星光照」卖点 + 一个回归测试）。
2. **I2 兴趣网格接线**（半天，联机可扩展性的前提）。
3. **I3 统一 MESH 噪声到 TerrainNoise**（半天，如需要 MESH/RAYMARCH 同场才做；
   否则文档澄清即可，已在本轮 `raymarch-implementation.md` 标注）。

> 已完成：6.1 delta 可见性（已修 + 固化为 `tests/verify_dig_visible.tscn`）、
> I4 d3d12 一致性补测（Vulkan 与 d3d12 均逐位一致）。
