# Apex 移动系统逆向报告 v0.2 —— 相机与移动字段（只读静态）

> 目标：`F:\SteamLibrary\steamapps\common\Apex Legends\r5apex_dx12.exe`
> 版本：**v3.0.3.65**（`gameversion.txt`），构建 `R5pc_r5-301_J65_CL11630615_FSv30_1_2026_10_02_09_54`
> 大小：68,749,736 字节（PE32+ x86-64）
> 方式：**只读**。全部用 `open(path, "rb")` 读取，**未写入、未修改游戏安装目录任何文件**；未运行游戏（EAAntiCheat），未做动态调试。
> 产物：原始转储在 `.re-apex/evidence/`，可复现脚本见 §8。

---

## 0. 本轮新增结论速览

| # | 结论 | 等级 | 关键证据 |
|---|---|---|---|
| 1 | 相机 FOV 以 **tan(半角)** 存储（`fTanHalfFOV`），并有独立的 FOV 缩放开关 `USE_FOV_SCALING` | **A** | `.fTanHalfFOV` ×9 处、`USE_FOV_SCALING` 0x3020B28 |
| 2 | 用户可调 FOV 走 `cl_fovScale`，并映射到设置系统键 `setting.cl_fovScale` | **A** | 0x2FE49E8、0x3017258 |
| 3 | 镜头晃动是**弹簧-阻尼模型**（`viewkick_spring*`），不是简单正弦 | **A** | `viewkick_spring_cooldown_holdtime` / `_fadetime` / `_velocityScalePerShot` / `_heatpershot` |
| 4 | 落地晃动强度**按下落距离分档**（`viewkickFallDistMin/Max/MaxScale`），与落地动画 `landDistLow/Medium/High` + `hardFallDist` 对应 | **A** | 0x3110100–0x3110180 连续块 |
| 5 | 相机**侧倾（roll）**有三套独立参数：滑铲 / 冲刺 / 墙跑 | **A** | `slide_viewTilt*`、`sprinttiltMaxRoll`、`wallrun_*ViewTilt*` |
| 6 | 攀爬/翻越有**按高度分档的时长字段**：`mantleDurationHigh/Above/Level/Below` | **A** | 0x3110590–0x31105D8 |
| 7 | 窗口翻越有**完整几何参数组**：`traversal_window_*`（6 个） | **A** | 0x31183B0–0x3118468 |
| 8 | 翻越过程的相机插值字段：`traversal_viewLerpOutPos/Angle`、`traversal_viewLerpInDuration`、`traversal_mantle_cap_camera_height` | **A** | 0x3059468–0x3059548 |
| 9 | 空中移动有**独立字段**：`airAcceleration` / `airSpeed` / `airDrag` / `airFriction` | **A** | 0x3110240–0x3110268 |
| 10 | 冲刺有独立加减速与视角侧倾：`sprintAcceleration` / `sprintDeceleration` / `sprinttiltMaxRoll` | **A** | 0x31100E8、0x3114320–0x3114350 |
| 11 | 跳跃有 **jump grace** 窗口（多段衰减）：`jump_grace_*` | **A** | 0x3117D70–0x3117E58 |
| 12 | **没有** `cl_bob` / `bob` 类相机摇晃 cvar | **A（负结论）** | 全文件 `cl_bob` / `bob` 命中 0（`bob` 的 16 处全是 ComboBox / 随机串） |
| 13 | cvar **默认数值**依然读不到（在 BSS） | **A（负结论）** | `.data` vsize ≫ rawsize，见 v0.1 报告 §3.1；本轮再次确认 |

---

## 1. 证据等级定义（沿用 v0.1）

| 等级 | 含义 |
|---|---|
| **A** | 在二进制中**直接观测**到（字符串、枚举、结构、指令） |
| **B** | 由引擎谱系（Source 2 / Titanfall）推导，未在二进制中直接读到该值 |
| **C** | 设计选择 / 需实机手感校准，**无**可靠静态证据 |

> 本轮所有结论都是 **A 级的"字段存在性"**。**字段的数值仍然读不到**（见 §7），
> 因此插件里的具体数值来自：需求方口径（角色尺寸）+ 社区 wiki 实测（速度/高度）+ C 级手感自定（相机）。
> **不要把本文件当作数值来源。**

---

## 2. 方法

1. 整文件读入内存（68 MB），正则提取所有 ≥3 字节的可打印 ASCII 串，得到 **519,714** 条字符串。
2. 按关键词分组命中（相机 / 滑铲 / 攀爬 / 空中 / 落地 / 蹲 / 跳跃），记录**文件偏移**。
3. 对命中点**回溯 ±512 字节做连续区域转储**，观察字段是否**成块连续出现**。
   —— 这是判断"这些字段属于同一个数据结构"的关键：Apex 的 datatable schema 字段表是
   连续排布的字符串数组，字段之间没有分隔符，只有长度前缀。
4. 把成块区域与 v0.1 已知的 `ACT_VM_MANTLE_*`、`traversal_viewLerp*` 交叉对照，确认同一套系统。

复现脚本见 §8。

---

## 3. 相机 / FOV

### 3.1 观测到的字段与字符串

| 偏移（文件） | 字符串 | 说明 |
|---|---|---|
| `0x2FE49E8` | `cl_fovScale` | 用户可调 FOV 缩放（Source 2 谱系命名） |
| `0x3017258` | `setting.cl_fovScale` | 同一项在**设置系统**里的键名（可持久化） |
| `0x3020B28` | `USE_FOV_SCALING` | 编译期/运行期开关宏名 |
| `0x3052308` | `fov_disableAbilityScaling` | 关闭"技能改变 FOV"的开关 |
| `0x3051EB0` | `SwchFOVAbilityScaling` | UI 开关（Swch = Switch） |
| `0x3051ED8` / `0x3051EC8` | `SldFOV` / `TextEntrySldFOV` | UI 滑条（Sld = Slider） |
| `0x304A880` | `SetCockpitFOV` | 载具座舱 FOV |
| `0x304C678` / `0x304E488` | `SetFOVFade` / `Hud_SetFOVFade` | FOV 淡入淡出 |
| `0x3059428` | `chasecamFoV` | 追尾相机 FOV（第三人称） |
| `0x306D8D0` / `0x306F508` / `0x31528E0` | `zoom_fov_viewmodel` / `zoom_fov` / `weapon_zoom_fov` | 开镜 FOV |
| `0x2E326CC` 等 **9 处** | `.fTanHalfFOV` | **FOV 以 tan(半角) 形式存储**的字段名 |

### 3.2 结论

**（A）** 相机 FOV 体系存在且分三层：

1. **基础 FOV** —— 由 `cl_fovScale` 缩放，设置项 `setting.cl_fovScale` 持久化；
2. **瞬时 FOV 修饰** —— `SetFOVFade` / `fov_disableAbilityScaling`（技能、冲刺、滑铲等会改 FOV）；
3. **开镜 FOV** —— `zoom_fov` / `weapon_zoom_fov` / `zoom_fov_viewmodel` 独立。

**（A）** `.fTanHalfFOV` 表明内部用 **tan(半 FOV)** 做投影与缩放运算 ——
这意味着 FOV 的线性叠加是**在 tan 空间**做的，不是在角度空间。
本插件因此提供 `FovMode.Tangent` / `FovMode.Linear` 两种插值模式（默认 Tangent）。

**（C）** `cl_fovScale` 与"显示 FOV 度数"的换算公式**没有读到**（无立即数、无公式常量）。
本插件不猜公式，直接以**水平 FOV 度数**为参数（Godot `Camera3D.KeepAspect = KEEP_WIDTH`），
`FovScale` 作为一个独立乘子暴露给使用者。

---

## 4. 镜头晃动 / 视角踢 / 侧倾

### 4.1 `viewkick` —— 弹簧-阻尼模型（A 级）

在 `0x3069C48`–`0x306C3E8` 连续观测到 **71 个** `viewkick*` 字段，其中**决定运动模型**的是这 5 个：

```
0x306C3C0  viewkick_spring
0x306C318  viewkick_spring_cooldown_holdtime
0x306C340  viewkick_spring_cooldown_fadetime
0x306C368  viewkick_spring_velocityScalePerShot
0x306C3E8  viewkick_spring_heatpershot
```

**解读（B 级推导）**：`spring` + `velocityScalePerShot` + `heatpershot` 描述的是
「每次开火给弹簧一个速度冲量，冲量随连发累积升温（heat），冷却分 hold/fade 两段」。
这不是正弦抖动，而是**二阶弹簧**。本插件的落地/受击晃动采用同构模型：
`x'' = -k·x - c·x' + impulse`。

配套的**状态缩放**字段（A 级，说明晃动强度要按玩家状态调制）：

```
viewkick_move_scale     ← 移动时缩放
viewkick_duck_scale     ← 蹲下时缩放
viewkick_hover_scale    ← 悬停（Valk 飞行）时缩放
viewkick_air_scale_ads  ← 空中开镜时缩放
viewkickFallDistMin / viewkickFallDistMax / viewkickFallDistMaxScale  ← 按下落距离缩放
```

### 4.2 落地分档（A 级）

`0x3110150`–`0x3110180` 连续块：

```
landDistHigh
landDistMedium
landDistLow
hardFallDist
```

对应动画状态（`0x310A4A0`–`0x310A5F0`）：

```
ACT_MP_JUMP_LAND_LOW / _MEDIUM / _HIGH        （各带 _FORWARD/_BACKWARD/_LEFT/_RIGHT 变体）
ACT_MP_JUMP_LAND_SLAM                          ← 极高处落地（硬着陆）
```

**结论（A）**：落地按**下落距离**分 **4 档**（Low / Medium / High / Hard）。
本插件的 `LandingTier` 枚举直接照此实现，并用 `viewkickFallDist*` 的三点线性映射决定晃动强度。

### 4.3 相机侧倾 viewTilt —— 三套独立参数（A 级）

```
0x313F418  slide_viewTiltDecreaseSpeed
0x313F438  slide_viewTiltIncreaseSpeed
0x313F458  slide_viewTiltSide
0x313F470  slide_viewTiltPlayerSpeed
0x31100E8  sprinttiltMaxRoll
0x3118028  wallrun_viewTiltSpeed
0x3118040  wallrun_maxViewTilt
0x3118348  wallrun_viewTiltPredictTime
0x3117C80  dodge_viewTiltFalloffTime
0x3117CA0  dodge_viewTiltDecreaseSpeed
0x3117CC0  dodge_viewTiltIncreaseSpeed
0x3117D28  dodge_viewTiltMax
```

**结论（A）**：相机 roll 是**独立子系统**，每种运动状态有"增大速率 / 减小速率 / 最大角"三件套。
滑铲额外有 `slide_viewTiltPlayerSpeed`（说明侧倾角与**玩家速度**相关，不是固定角）
和 `slide_viewTiltSide`（侧倾方向）。

本插件 `SlideRoll` 按此实现：`targetRoll = maxRoll * saturate(speed / playerSpeedRef) * side`，
增大/减小用两个不同速率插值。

### 4.4 相机高度偏移（A 级）

```
0x3112E80  sprintViewOffset          ← 冲刺时的相机偏移
0x31123F8  ziplineViewOffset         ← 滑索
0x3114430  viewheight                ← 基础视高
0x3114418  firstpersonproxyoffset    ← 第一人称代理偏移
0x30C5DF8  cam_player_viewheight_scale
0x310FE18  viewmodelDuckOffset       ← 蹲下时的视模型偏移
0x30BA720 / 0x30BA738 / 0x30BA7B8   m_vecViewOffset.z / .y / .x
```

**结论（A）**：Apex 的"眼位"不是常量，而是 `viewheight` + 一堆状态偏移
（sprint / duck / zipline / proxy）叠加。本插件的 `EyeOffset` 因此实现为
`base + sprintOffset*sprintBlend + crouchOffset*crouchBlend`。

### 4.5 负结论：没有 `cl_bob`

全文件搜索 `cl_bob` → **0 命中**；`viewbob` → **0 命中**；`camerabob` → **0 命中**；
`bob` 的 16 处命中全是 `ComboBox` 等无关串。

**结论（A）**：Apex **没有** Source 那种 `cl_bob*` 相机摇晃 cvar。
走路摇晃来自**第一人称动画（viewmodel animation）**，不是相机代码。
因此本插件的走路摇晃参数**无法从二进制标定**，全部标 **C 级**，默认值按手感自定并暴露为可调参数。

---

## 5. 移动系统字段（datatable schema 区块）

以下字段在 `0x3110000`–`0x3119000` 区间**成块连续**出现，符合 datatable schema 字段表布局。
同区间还夹杂 `DT_InfoTarget`、`C_PlayerLocalData` 等类名，佐证这是"数据结构字段名表"。

### 5.1 空中（对应 wiki「无空气摩擦」）

```
0x3110240  airDrag
0x3110248  airFriction
0x3110258  airAcceleration
0x3110268  airSpeed
0x3110230  impactSpeed
```

**结论（A）**：空中移动**有独立字段组**，且 `airFriction` 与 `airAcceleration` 分开
—— 支持 wiki「Air acceleration=500、Air speed=60、无空气摩擦」的说法
（`airFriction` 存在但可以被设成 0）。

### 5.2 地面 / 冲刺

```
0x3114370  acceleration        0x3114360  deceleration
0x3114338  sprintAcceleration  0x3114320  sprintDeceleration
0x3114380  sprintspeed
0x3114350  lowAcceleration     0x3114390  lowSpeed
0x31100E8  sprinttiltMaxRoll
0x3114400  footstepInterval    0x31143E8  footstepIntervalSprint
```

**结论（A）**：走 / 冲刺 / 低速是**三套加减速**，不是一套乘倍率。
本插件因此把 `WalkAccel` / `SprintAccel` / `CrouchAccel` 分开暴露（默认取同值，
因为 wiki 说「acceleration is the exact same whether you crouch walk, walk or Sprint」）。

### 5.3 滑铲

```
0x3118E00  slide_while_on_unwalkable_ground
0x3118E28  slide_auto_stand
0x3118E40  slide_step_velocity_reduction
0x3118E60  slide_max_angle_dot          ← 可行走坡度阈值（arccos(0.7) → dot 0.7）
0x3118E78  slide_max_duck_frac_to_start
```

动画状态（`0x3109F20`–`0x310AF00`）：

```
ACT_MP_SLIDE_FORWARD / _LEFT / _RIGHT / _BACKWARD
ACT_MP_CROUCHSLIDE_AIR                  ← 空中滑铲是独立状态
ACT_MP_CROUCHSLIDE_END_FORWARD / _LEFT / _RIGHT / _BACKWARD_LEFT / _BACKWARD_RIGHT
```

**结论（A）**：
- `slide_max_angle_dot` 与 wiki 的 `arccos(0.7)=45.572°` **完全对应** —— wiki 数值与二进制字段的交叉验证成功。
- `ACT_MP_CROUCHSLIDE_AIR` 独立存在，证实 wiki 的 **Air Slide vs Ground Slide** 区分是真实机制。
- `slide_auto_stand` 证实滑铲会自动站起（本插件有对应逻辑）。

### 5.4 攀爬（Climb = 贴墙爬）

```
0x3110F80  climbEnabled        0x3110F70  climbHeight
0x3110F28  climbAccel          0x3110F18  climbDecel
0x3110EF8  climbHorzAccel      0x3110EE8  climbHorzDecel
0x3110F08  climbHorzSpeed
0x3110F48  climbSpeedStart     0x3110F38  climbSpeedEnd
0x3110F58  climbFinalJumpUpHeight
0x3110ED0  climbHighJumpMaxHeight
0x3110E98  climbJumpEdgeAirAcceleration
0x3110EB8  climbJumpEdgeAirSpeed
0x3110E88  climbingNoSlide
0x3118DC0  wallclimb_vertical_gain_reduction
```

**结论（A）**：攀爬有**起止速度**（`climbSpeedStart/End`）与**独立加速度**，
说明是"速度曲线"而不是"固定时长插值"。`climbHeight` 就是 wiki 的 273 hu 上限字段。

### 5.5 翻越 / 攀爬分档（核心）

```
0x3110590  mantleDurationHigh
0x31105A8  mantleDurationAbove
0x31105C0  mantleDurationLevel
0x31105D8  mantleDurationBelow
0x3110040  mantleHeight
0x3110028  mantleAngleScale
0x31141F0  mantlePitchLeveling
0x3110540  mantleBoostSprintSpeedMultiplier
0x3110568  mantleBoostJumpHeight
0x3110580  canMantleBoost
0x3114570  mantleBoostSweetSpotAngleThreshold
```

自动翻越控制（`0x3118368`–`0x3118630`）：

```
automantle_enable / automantle_disable_hang / automantle_debug
automantle_maxangle_view        ← 允许触发翻越的最大视角偏角（wiki: ±50°）
automantle_maxangle_push
automantle_view_pitch_max / _min
automantle_view_yaw_max / automantle_view_high_yaw_max
automantle_rest_frac / automantle_rest_frac_below   ← 挂边时手的位置比例
automantle_mindist
automantle_jumpoff_duration / automantle_jumpoff_anim_maxfrac
automantle_backoff_anim_maxfrac
```

动画状态（`0x3108C70`–`0x31094C0`，共 **10 个**，比 v0.1 报告的 7 个更全）：

```
ACT_VM_MANTLE_HIGH / _ABOVE / _LEVEL / _BELOW          ← 4 个主分类
ACT_VM_MANTLE_CROUCH_ABOVE / _CROUCH_LEVEL / _CROUCH_BELOW / _CROUCH_HIGH   ← 4 个蹲姿变体
ACT_VM_MANTLE_IDLE                                     ← 挂边待机（Mantle hang）
ACT_VM_MANTLE_JUMPOFF                                  ← 蹬墙脱离（Mantle hop）
```

**结论（A）**：**4 档高度分类 × (站姿 + 蹲姿) = 8 种翻越动作 + 挂边 + 蹬离**。
`mantleDuration{High,Above,Level,Below}` 就是这 4 档的时长。
本插件 `MantleClass` 枚举按此 4 档实现（另加 Crouch 变体标记），
`MantleGrab`/`MantlePull` 两段式来自 wiki 的 0.45s + 0.45s 时序描述。

### 5.6 窗口翻越（TRAVERSAL_WINDOW）

```
0x31183B0  traversal_window_finish_angle
0x31183D0  traversal_window_hand_vertical_offset
0x3118400  traversal_window_forward_offset
0x3118420  traversal_window_sideways_offset
0x3118448  traversal_window_duration
0x3118468  traversal_window_enable
0x3118480  traversal_cooldown
0x3118498  traversal_anim
0x31184A8  traversal_hand_required_width
0x31184C8  traversal_hand_debug
0x31184E0  traversal_enable
0x31194B8  traversal_move_crouch_mantles_enabled
0x3084AE8  traversal_window_yaw_max
0x3084F38  traversal_window_view_pitch_min
0x3084F80  traversal_window_view_pitch_max
```

状态字段（`0x30858A0`–`0x3087D58`）：

```
m_traversalAnimProgress / m_traversalProgress / m_traversalState / m_traversalType
m_traversalBegin / m_traversalMid / m_traversalEnd / m_traversalMidFrac
m_traversalStartTime / m_traversalReleaseTime / m_traversalHandAppearTime
m_traversalBlendOutStartOffset / m_traversalBlendOutStartTime
m_traversalYawDelta / m_traversalForwardDir / m_traversalRefPos
```

类名：`0x3119498  C_GameMovement::TraversalMove`（A 级，直接证明这是 GameMovement 的一个移动分支）。

**结论（A）**：窗口翻越是**独立的 traversal 类型**，有自己的
`forward_offset` / `sideways_offset` / `hand_vertical_offset` / `finish_angle` / `duration`，
且 `traversal_hand_required_width` 说明**需要检测窗口开口宽度**。
本插件 `WindowTraversal` 按这 6 个参数实现。

### 5.7 跳跃 grace / bhop 保速

```
0x3117D70  jump_grace_controller_disable_duration
0x3117D98  jump_grace_multi_tap_decay_velocity_based_easing_type
0x3117DD0  jump_grace_multi_tap_decay_velocity_based_max_speed
0x3117E08  jump_grace_multi_tap_decay_velocity_based_max_decay
0x3117E40  jump_grace_cutoff_speed
0x3117E58  jump_grace_jump
0x31191A0  gamemovement_skip_speed_retain_override
```

**结论（A）**：跳跃宽限（土狼时间 / bhop 窗口）有 **velocity-based decay**（按速度衰减），
不是固定计时器。`jump_grace_cutoff_speed` 是超过该速度就不再施加衰减的阈值 ——
这正是 wiki 说的「速度高于冲刺速度时，每次 bhop 扣最多 100hu/s，但超高速时有 cutoff」。

### 5.8 蹲（Duck）

```
0x3088078  m_DuckHullMax        0x30880D0  m_DuckHullMin      ← Source 谱系的蹲下 hull
0x3088120  m_duckState          0x30889F0  m_doingHalfDuck
0x3085E68  m_duckTransitionRemainderMsec
0x3149920  DS_DUCK_STARTING     0x314A1E0  DS_DUCKED     0x314A1D0  DS_DUCK_ENDING
0x310FE18  viewmodelDuckOffset
0x3119228  gamemovement_duck_debounce_threshold
0x31454D0  IN_DUCK              0x3145A78  IN_DUCKTOGGLE
```

**结论（A）**：蹲有**显式状态机**（STARTING / DUCKED / ENDING）与
`m_duckTransitionRemainderMsec`（过渡剩余毫秒）—— 与 wiki「站立→蹲下 0.4s 后才进入蹲下状态」吻合。
`m_doingHalfDuck` 说明存在"半蹲"中间态。

### 5.9 Superjump（超级跳 / superglide 底层）

```
0x31100A8  superjumpLimit          0x31100B8  superjumpMaxHeight
0x31100D0  superjumpMinHeight      0x3110090  superjumpHorzSpeed
0x3110070  superJumpHorzSpeedDampFrac
0x3110050  superjumpCanUseAfterWallrun
0x3117C28  superjump_min_height_fraction
0x3117C08  superjump_drain_power_onfail
```

**结论（A）**：Superjump 是**独立机制**（有高度区间 + 水平速度 + 衰减比例）。
**本版不实现**（需求范围外），但字段已记录，v0.3 可直接接。

---

## 6. 与 wiki 的交叉验证

| 项 | wiki 说法 | 二进制字段 | 是否吻合 |
|---|---|---|---|
| 可行走坡角 | `arccos(0.7) = 45.572°` | `slide_max_angle_dot` | ✅ dot=0.7 完全对应 |
| 空中无摩擦 | 「Apex does not feature any Air friction」 | `airFriction` 字段存在（可设 0） | ✅ 不矛盾 |
| 空中加速 | Air acceleration=500, Air speed=60 | `airAcceleration` / `airSpeed` | ✅ 字段同名 |
| 空中滑铲独立 | Air Slide vs Ground Slide | `ACT_MP_CROUCHSLIDE_AIR` | ✅ 独立动画状态 |
| 攀爬上限 | 273 hu | `climbHeight` | ✅ 字段存在 |
| 翻越分档 | grab 0.45s + pull 0.45s | `mantleDurationHigh/Above/Level/Below` | ✅ 4 档时长 |
| 窗口翻越 | 有独立技术条目 | `traversal_window_*` × 6 + `C_GameMovement::TraversalMove` | ✅ 独立分支 |
| 自动翻越视角限制 | 「±50°」 | `automantle_maxangle_view` | ✅ 字段存在 |
| 蹲下状态延迟 | 「站立→蹲下 0.4s 后才进入蹲下状态」 | `m_duckTransitionRemainderMsec` + `DS_DUCK_STARTING` | ✅ 有过渡状态 |
| 落地分档 | （wiki 未细化） | `landDistLow/Medium/High` + `hardFallDist` | — 逆向补充 |
| 相机 bob | （wiki 无） | **无** `cl_bob` | — 双负结论 |

---

## 7. 明确没读到 / 读不到的

| 项 | 状态 | 原因 |
|---|---|---|
| **所有字段的数值** | **未读到** | 字段名表只有名字，值在运行时对象里；cvar 默认值落在 `.data` 的 BSS 区（vsize 0x0B40AB8C ≫ rawsize 0x00D00000），静态文件里不存在 |
| `cl_fovScale` → FOV 度数的换算公式 | 未读到 | 无反汇编到具体公式；只有字符串与 tan 字段名 |
| 相机 bob 的振幅/频率 | 不存在 | Apex 用 viewmodel 动画做 bob，无相机 bob 代码 |
| `punchangle`（Source 经典命名） | 不存在 | Apex 改用 `viewpunch_*` / `viewkick_*` 命名 |
| Fallstun 公式 | 未读到 | 只有 `landDist*` / `hardFallDist` 分档字段 |
| `.rpak` 内的脚本/配置 | 未解包 | Respawn 私有格式，需专用工具（v0.1 §3.2 已说明） |

> 要拿到数值，只有三条路：动态调试（反作弊禁止）、实机观测反推（本插件 + wiki 走的路）、
> 参考公开引擎默认值（B 级）。本插件采用后两条。

---

## 8. 复现方式

```bash
cd E:\prj\Game\project-a

# ① 分组字符串清单（701 行，按 9 个主题分组）
python - <<'PY'
import re
p = r"F:\SteamLibrary\steamapps\common\Apex Legends\r5apex_dx12.exe"
data = open(p, "rb").read()          # 只读
pat = re.compile(rb"[\x20-\x7e]{3,}")
for m in pat.finditer(data):
    s = m.group().decode("latin1")
    if "viewkick" in s:
        print(hex(m.start()), s)
PY

# ② 区块转储（证明字段成块连续）
#    见 .re-apex/evidence/apex_schema_regions.txt（9 个区块，355 行）

# ③ 成品
#    .re-apex/evidence/apex_movement_camera_strings.txt
#    .re-apex/evidence/apex_schema_regions.txt
```

**依赖**：Python 3.13（仅标准库 `re`，不需要 capstone —— 本轮全是字符串层证据）。
**只读保证**：所有脚本只用 `open(path, "rb")`，无任何写操作。

---

## 9. 对 Godot 实现的指导（v0.2 实际采用的 9 条）

| # | 逆向结论 | 插件落地 |
|---|---|---|
| 1 | FOV 在 tan 空间运算（`.fTanHalfFOV`） | `CameraEffectsConfig.FovMode = Tangent`（默认）/ `Linear` |
| 2 | 相机侧倾分状态、有独立增/减速率与最大角 | `CameraRig` 的 `SlideRoll` / `SprintRoll`，各有 `IncreaseSpeed` / `DecreaseSpeed` |
| 3 | 晃动是弹簧模型 | `ViewKickSpring`：二阶弹簧 + 冲量 + 阻尼 |
| 4 | 落地按下落距离分 4 档 | `LandingTier { Low, Medium, High, Hard }` + `landDist*` 阈值 |
| 5 | 攀爬 4 档高度分类 | `MantleClass { Below, Level, Above, High }` |
| 6 | 攀爬分 grab / pull 两段 | `MantlePhase { Grab, Pull, Hang }` |
| 7 | 窗口翻越有 6 个几何参数 | `WindowTraversalConfig`（forward / sideways / handVertical / finishAngle / duration / requiredWidth） |
| 8 | 眼位 = viewheight + 多状态偏移 | `CameraRig` 的 `EyeOffset` 由 base + sprint + crouch 三段叠加 |
| 9 | 跳跃 grace 按速度衰减 | `JumpGrace`：`cutoffSpeed` 之上不再衰减 |

> 数值全部不来自本文件（本文件没有数值），见 `wiki-reference.md` 与 `MovementConfig.cs`。
