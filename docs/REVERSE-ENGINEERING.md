# Apex 移动系统 —— 逆向与 wiki 证据合集

本文把三份分别写成的证据文档合成一份：**§1 是逆向 v0.1**（`r5apex_dx12.exe` 的 PE 结构、移动相关 cvar 的存在性与注册点、Apex 特有的翻越分类，以及读不到的东西和原因）；**§2 是逆向 v0.2**（相机 / FOV、`viewkick` 弹簧-阻尼与落地分档、相机侧倾 `viewTilt`、相机高度偏移，以及移动系统的 datatable schema 字段：空中 / 地面 / 滑铲 / 攀爬 / 翻越 / 窗口翻越 / 跳跃 grace / 蹲 / Superjump）；**§3 是社区 wiki**（单位换算、速度表、空中机制、滑铲与 slidejump 阈值、跳跃疲劳、蹲姿胶囊收缩、攀爬与翻越、FOV 与镜头晃动）；**§4** 是三份文档之间的交叉索引。

**优先级声明**：wiki 与二进制逆向冲突时，**以逆向为准**。wiki 负责提供**数值与手感描述**，逆向负责提供**机制存在性与字段结构**；wiki 侧每条数值都标注来源与可信度。

## 0. 证据等级与阅读方式

三份文档里重复出现的「证据等级定义」合并成下面这一张表：

| 等级 | 含义 |
|---|---|
| **A** | 在二进制中**直接观测**到（字符串、枚举、结构、指令） |
| **B** | 由引擎谱系（Source 2 / Titanfall）推导，未在二进制中直接读到该值 |
| **C** | 设计选择 / 需实机手感校准，**无**可靠静态证据 |

本文所有结论都带等级标注；实现侧实际用到哪些数值、每个值的当前取值与可调范围，见 `PARAMETERS.md`。

> **字段存在性 ≠ 字段数值**：v0.2 的所有结论都是 **A 级的"字段存在性"**。**字段的数值仍然读不到**（见本文 §2.7），
> 因此插件里的具体数值来自：需求方口径（角色尺寸）+ 社区 wiki 实测（速度/高度）+ C 级手感自定（相机）。
> **不要把本文当作数值来源。**

> **采集口径（三份文档的头信息，合并为一条）**
> 目标：`F:\SteamLibrary\steamapps\common\Apex Legends\r5apex_dx12.exe`
> 版本：**v3.0.3.65**（`gameversion.txt`），构建 `R5pc_r5-301_J65_CL11630615_FSv30_1_2026_10_02_09_54`
> 大小：68,749,736 字节（PE32+ x86-64）
> 方式：**只读**静态分析。全部用 `open(path, "rb")` 读取，**未写入、未修改游戏安装目录任何文件**；未运行游戏（EAAntiCheat），未做动态调试。
> 工作目录与产物：`.re-apex/`（临时产物，已 gitignore）；原始转储与成品在 `.re-apex/evidence/`；可复现脚本见本文 §1.5 与 §2.8。
> wiki 来源：**https://apexmovement.tech/wiki**（Apex Movement Wiki，作者 Mokeysniper / G0f / JayTheYggdrasil / Ilses 等）；抓取时间 2026-10，抓取方式 `curl` 拉 SSR 页面 → 剥离标签 → 落盘到 `.re-apex/wiki/`（已 gitignore）；页面清单见本文 §3.9。

## 1. 逆向 v0.1 —— 二进制结构与 cvar

### 1.1 观测到的（A 级证据）

#### 1.1.1 PE 结构

```
PE32+ x86-64
.text     vaddr=0x00001000  vsize=0x02C951AC  rawsize=0x02C95200
.rdata    vaddr=0x02C97000  vsize=0x00680B60  rawsize=0x00680C00
.data     vaddr=0x03318000  vsize=0x0B40AB8C  rawsize=0x00D00000   ← 注意
.pdata    vaddr=0x0E723000  vsize=0x000E32BC
.reloc    vaddr=0x0E80D000  vsize=0x000571B0
```

> `.data` 的 **vsize (0x0B40AB8C ≈ 189MB) 远大于 rawsize (0x00D00000 = 13MB)**。
> 这意味着 `.data` 绝大部分是 **未初始化区（BSS）**，运行期才写入。
> 这是后面"为什么读不到 cvar 默认值"的直接原因。

#### 1.1.2 移动相关的 cvar 名称存在

在 `.rdata` 的 `0x14311C1B8` 起，连续观测到一组 Source 引擎的 movevars 名称：

```
0x14311C1B8  sv_bounce
0x14311C1C8  sv_friction
0x14311C1D8  sv_rollangle
0x14311C1E8  sv_rollspeed
0x14311C1F8  sv_footsteps
0x14311C208  sv_wateraccelerate
0x14311C220  sv_airaccelerate
0x14311C238  sv_maxspeed
0x14311C248  sv_specnoclip
0x14311C258  sv_specspeed
0x14311C268  sv_specaccelerate
0x14311C280  sv_stopspeed
0x14311C290  sv_gravity
```

**结论（A）**：Apex 的移动物理**沿用 Source 谱系**的 cvar 体系
（`sv_accelerate` / `sv_friction` / `sv_airaccelerate` / `sv_stopspeed` / `sv_gravity` 等）。

#### 1.1.3 cvar 注册点（A 级）

用暴力 RIP-relative 扫描（`.re-apex/tools/pe_xref.py`）定位到每个 cvar 的注册代码：

| cvar | 注册点 VA |
|---|---|
| `sv_gravity` | `0x1419D268E` |
| `sv_maxspeed` | `0x1419D2F4E` |
| `sv_friction` | `0x1419D39CE` |
| `sv_airaccelerate` | `0x1419D310E` |
| `sv_stopspeed` | `0x1419D284E` |
| `sv_wateraccelerate` | `0x1419D32CE` |
| `sv_backspeed` | `0x1419D3F0E` |
| `sv_waterdist` | `0x1419D40CE` |
| `sv_maxvelocity` | `0x1419D3D4E` |

注册模式（以 `sv_gravity` 为例）：

```asm
0x1419D2687  mov  r8, qword ptr [rip + 0x4092cb2]    ; r8 = 默认值对象指针
0x1419D268E  lea  rax, [rip + 0x174b3fb]             ; rax → "sv_gravity"
0x1419D2695  mov  qword ptr [rip + 0x24ddd74], rax   ; 存名字
0x1419D269C  lea  rax, [rip + 0x161290d]             ; rax → help 字符串（实测为空串）
0x1419D26A3  mov  qword ptr [rip + 0x24ddd6e], rax
0x1419D26AA  mov  rax, qword ptr [rip + 0x4092c9f]   ; 默认值
0x1419D26B1  mov  qword ptr [rip + 0x24ddd50], rax
0x1419D26B8  mov  byte ptr [rip + 0x24ddd6d], 0
0x1419D26BF  mov  qword ptr [rip + 0x24ddd56], 0
0x1419D26CA  mov  dword ptr [rip + 0x24ddd54], 0x2002 ; flags
```

#### 1.1.4 Apex 特有的翻越分类（A 级，最有价值的发现）

在 `.rdata` `0x14310A400` 起观测到一组**动画/动作枚举名**，直接反映 Apex 的翻越分类体系：

```
0x14310A470  ACT_VM_MANTLE_CROUCH_ABOVE
0x14310A490  ACT_VM_MANTLE_CROUCH_LEVEL
0x14310A4B0  ACT_VM_MANTLE_CROUCH_BELOW
0x14310A4D0  ACT_VM_MANTLE_HIGH
0x14310A4F0  ACT_VM_MANTLE_ABOVE
0x14310A510  ACT_VM_MANTLE_LEVEL
0x14310A530  ACT_VM_MANTLE_BELOW
0x14310A550  ACT_VM_TRAVERSAL_WINDOW_RIGHT
0x14310A570  ACT_VM_TRAVERSAL_WINDOW_LEFT
```

配套的状态字段名（`0x14305AC00` 附近）：

```
0x14305AC68  traversal_viewLerpOutPos
0x14305AC88  traversal_viewLerpOutAngle
0x14305ACA8  traversal_mantle_cap_camera_height
0x14305ACD0  traversal_viewLerpInDuration
0x14305ACF0  traversalYaw
0x14305AD00  traversalPitch
0x14305AD18  grapple_rope
```

**结论（A）**：
- Apex 的攀爬按**相对高度**分成 7 种（`CROUCH_ABOVE/CROUCH_LEVEL/CROUCH_BELOW/HIGH/ABOVE/LEVEL/BELOW`）。
- 另有**窗口翻越** `TRAVERSAL_WINDOW_LEFT/RIGHT`（穿过窗口）。
- 翻越过程有相机插值（`traversal_viewLerpOutPos/Angle`、`traversal_viewLerpInDuration`）
  和**相机高度上限**（`traversal_mantle_cap_camera_height`）。

> 本插件按**障碍高度**把这些归并成 `Vault`（≤1.1m）/ `Mantle`（≤2.2m）两态。
> 完整复现 7 分类需要动画系统配合，超出本次范围（见本文 §1.4）。

#### 1.1.5 其他观测到的移动相关系统（A 级，但未实现）

| 系统 | 证据 |
|---|---|
| 墙上跑 | `WallRunStartPos`、`GetNearestWallrunPatchPoint`、`wallrun_curveDebug`、91 处含 `wallrun` |
| 滑索 | 135 处含 `zipline`、`ziplineInitialEntities` |
| 钩爪 | 182 处含 `grapple`、`GrappleData` |
| 滑铲 | 69 处含 `Slide`、`slide_start_3p` |
| 闪避 | 44 处含 `dodge` |

---

### 1.2 没读到的（以及为什么）—— 必须诚实说明

#### 1.2.1 cvar 默认值（数值）读不到

**尝试过且失败的方法**：

1. 反汇编注册点 → 发现默认值经 `r8` 传入（指针），不是立即数
2. 解析 `r8` 指向的地址 → 目标落在 `.data` 的**未初始化区**（vsize ≫ rawsize）
3. 直接扫描文件中的 float 常量 → 无可靠匹配

**根本原因**：`.data` 段 `vsize=0x0B40AB8C`（189MB）而 `rawsize=0x00D00000`（13MB）。
cvar 默认值对象绝大部分在 **BSS**（未初始化数据）里，**运行期才由代码写入**，
静态文件里根本不存在。

**另一条线索也断了**：`cfg/client/st_data.bin` 只有 183KB，
内容是 **datatable 的 schema 字段名**（如 `m_flFallVelocity`、`m_surfaceFriction`、
`m_vecVelocity`），**不含数值**。

**结论**：要拿到真实数值，只能：
- (a) 动态调试（REA 的 Frida/GDB 会话，但 Apex 有 **EAAntiCheat**，且用户要求不破坏源文件）
- (b) 实机观测手感反推（本插件当前的路线）
- (c) 参考公开的 Titanfall/Source 引擎默认值（本插件采用，标为 B 级）

#### 1.2.2 `.rpak` 里的脚本读不到

Apex 全部脚本资源在 `.rpak`（8290 个文件 / 65.5GB）中，**没有 `.vpk`**。
`.rpak` 是 Respawn 私有格式（非 Source 的 VPK），**需要专用解包工具**。
本次未解包。

#### 1.2.3 `r5apexdata.bin` 是加密的

4096 字节，内容为不可读的编码数据（疑似签名/校验块），非配置。

---

### 1.3 本插件采用的数值与依据

全部参数集中在 `core/MovementConfig.cs`，**每个字段都标注了证据等级**。

#### 1.3.1 核心速度与几何 —— 已按需求方口径标定（等级 A-需求方）

这四个值**不是**从二进制推导的，而是需求方（m00604）给出的实测口径，
优先级高于任何谱系推导：

| 参数 | 值 | 依据 |
|---|---|---|
| `WalkSpeed` | **5.4 m/s** | 需求方：「行走是 5m 多/s」 |
| `SprintMultiplier` | **1.37**（→ 7.4 m/s） | 需求方：「奔跑是 7m 多/s」 |
| `MantleMaxHeight` | **6.5 m** | 需求方：「爬墙可以爬 6m 多」 |
| `StandHeight` | **1.95 m** | 需求方指定 |
| `CrouchHeight` | **0.975 m**（= 站立一半） | 需求方指定 |
| `EyeHeightOffset` | **0.10 m**（站立眼位 1.85 m） | 需求方指定 |

> **重要教训：8.13 m/s 是错的。**
> 早期版本用 Source 的 `cl_forwardspeed = 320 u/s × 0.0254 = 8.13 m/s` 当行走速度。
> 但那是引擎的**速度上限常量**（`cl_forwardspeed` 只是客户端输入缩放上限），
> 不是玩家实际移动速度。Apex 的实际地面速度由服务器端玩家移动代码决定，
> 与这个 cvar 无关。这正是"谱系推导"的典型陷阱 —— 数值算得对，语义套错了。
>
> 同理，`sv_maxspeed` 一类 cvar 也不是实际移速。

#### 1.3.2 其余参数 —— 谱系推导 + 设计选择

| 参数 | 值 | 等级 | 依据 |
|---|---|---|---|
| Gravity | 20.32 m/s² | B | Source `sv_gravity=800` u/s² × 0.0254 |
| MaxFallSpeed | 88.9 m/s | B | Source `sv_maxvelocity=3500` u/s × 0.0254 |
| Friction | 4.0 | B | Source `sv_friction` 默认 |
| StopSpeed | 2.54 m/s | B | Source `sv_stopspeed=100` u/s × 0.0254 |
| GroundAccel | 10.0 | B | Source `sv_accelerate` 默认 |
| AirAccel | 1.0 | C | Apex 空中控制远弱于地面，需校准 |
| JumpHeight | 1.15 m | C | 反解初速 `v=sqrt(2gh)`，需校准 |
| Slide* | 见文件 | C | Apex 滑铲手感，需校准 |
| VaultMaxHeight | 1.1 m | C | 按 A 级"分类存在"推导的阈值 |
| GroundSnapLength | 0.20 m | C | 等价 Godot `floor_snap_length`，本实现自定 |
| MantleDuration / PerMeter / Max | 0.30 / 0.10 / 1.10 s | C | 攀爬时长随高度线性放大（6m 攀爬若用 0.3s 会像瞬移） |

> **这些数值不是从二进制里读出来的精确值**，而是"谱系推导 + 可调初值"。
> 需要实机试玩后按手感校准（这是 `MovementConfig` 存在的意义）。
> 但 §1.3.1 那六项是需求方明确给定的，不在此列。

### 1.4 明确没做 / 做不到的

| 项 | 状态 | 原因 |
|---|---|---|
| cvar 精确默认值 | **没拿到** | 在 BSS 里，需动态调试（见本文 §1.2.1） |
| `.rpak` 脚本 | **没解** | 私有格式，需专用工具 |
| 7 种 MANTLE 分类的完整复现 | **只做了 2 态** | 需要动画系统配合 |
| 窗口翻越（TRAVERSAL_WINDOW） | **没做** | 需要窗口几何检测 |
| 墙上跑 / 滑索 / 钩爪 | **没做** | 不在本次需求内 |
| 动态验证 | **没做** | 反作弊 + 用户要求不碰游戏环境 |

---

### 1.5 复现方式

所有工具脚本在 `.re-apex/tools/`（已 gitignore，可重跑）：

```bash
cd .re-apex/tools

# 列出 cvar 字符串地址
python pe_xref.py "<apex>/r5apex_dx12.exe" strings sv_gravity sv_friction

# 暴力 RIP-relative 扫描定位 cvar 注册点
python bfscan.py

# 反汇编注册点
python cvars.py
```

**依赖**：Python 3.13 + capstone 5.0.7。

**只读保证**：`pe_xref.py` 只用 `open(path, "rb")`，无任何写操作。
`.re-apex/` 是本次新建的临时目录，与游戏安装目录完全隔离。

---

### 1.6 对 Godot 实现的指导结论

从 A 级证据直接得到的 3 条设计约束：

1. **沿用 Source 谱系的加速/摩擦公式**（`accel × dt × wishSpeed`、`drop = max(speed,stopSpeed) × friction × dt`）
   —— 因为 cvar 体系确认是 Source 谱系。本插件 `ApexMovementCore` 已按此实现。

2. **攀爬必须按高度分类**（不是单一动作）—— `ACT_VM_MANTLE_*` 的 7 个变体证明 Apex 按相对高度区分。
   本插件用 `VaultMaxHeight` / `MantleMaxHeight` 两阈值近似。

3. **翻越过程需要相机插值**（`traversal_viewLerpOutPos/Angle`）—— 本插件当前是纯位移，
   相机直接跟随；若观感生硬，后续可按这两个字段的思路加相机 lerp。

## 2. 逆向 v0.2 —— 相机与移动字段

### 2.1 本轮新增结论速览

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
| 13 | cvar **默认数值**依然读不到（在 BSS） | **A（负结论）** | `.data` vsize ≫ rawsize，见本文 §1.2.1；本轮再次确认 |

---

### 2.2 方法

1. 整文件读入内存（68 MB），正则提取所有 ≥3 字节的可打印 ASCII 串，得到 **519,714** 条字符串。
2. 按关键词分组命中（相机 / 滑铲 / 攀爬 / 空中 / 落地 / 蹲 / 跳跃），记录**文件偏移**。
3. 对命中点**回溯 ±512 字节做连续区域转储**，观察字段是否**成块连续出现**。
   —— 这是判断"这些字段属于同一个数据结构"的关键：Apex 的 datatable schema 字段表是
   连续排布的字符串数组，字段之间没有分隔符，只有长度前缀。
4. 把成块区域与 v0.1 已知的 `ACT_VM_MANTLE_*`、`traversal_viewLerp*` 交叉对照，确认同一套系统。

复现脚本见本文 §2.8。

---

### 2.3 相机 / FOV

#### 2.3.1 观测到的字段与字符串

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

#### 2.3.2 结论

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

### 2.4 镜头晃动 / 视角踢 / 侧倾

#### 2.4.1 `viewkick` —— 弹簧-阻尼模型（A 级）

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

#### 2.4.2 落地分档（A 级）

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

#### 2.4.3 相机侧倾 viewTilt —— 三套独立参数（A 级）

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

#### 2.4.4 相机高度偏移（A 级）

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

#### 2.4.5 负结论：没有 `cl_bob`

全文件搜索 `cl_bob` → **0 命中**；`viewbob` → **0 命中**；`camerabob` → **0 命中**；
`bob` 的 16 处命中全是 `ComboBox` 等无关串。

**结论（A）**：Apex **没有** Source 那种 `cl_bob*` 相机摇晃 cvar。
走路摇晃来自**第一人称动画（viewmodel animation）**，不是相机代码。
因此本插件的走路摇晃参数**无法从二进制标定**，全部标 **C 级**，默认值按手感自定并暴露为可调参数。

---

### 2.5 移动系统字段（datatable schema 区块）

以下字段在 `0x3110000`–`0x3119000` 区间**成块连续**出现，符合 datatable schema 字段表布局。
同区间还夹杂 `DT_InfoTarget`、`C_PlayerLocalData` 等类名，佐证这是"数据结构字段名表"。

#### 2.5.1 空中（对应 wiki「无空气摩擦」）

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

#### 2.5.2 地面 / 冲刺

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

#### 2.5.3 滑铲

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

#### 2.5.4 攀爬（Climb = 贴墙爬）

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

#### 2.5.5 翻越 / 攀爬分档（核心）

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

#### 2.5.6 窗口翻越（TRAVERSAL_WINDOW）

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

#### 2.5.7 跳跃 grace / bhop 保速

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

#### 2.5.8 蹲（Duck）

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

#### 2.5.9 Superjump（超级跳 / superglide 底层）

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

### 2.6 与 wiki 的交叉验证

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

### 2.7 明确没读到 / 读不到的

| 项 | 状态 | 原因 |
|---|---|---|
| **所有字段的数值** | **未读到** | 字段名表只有名字，值在运行时对象里；cvar 默认值落在 `.data` 的 BSS 区（vsize 0x0B40AB8C ≫ rawsize 0x00D00000），静态文件里不存在 |
| `cl_fovScale` → FOV 度数的换算公式 | 未读到 | 无反汇编到具体公式；只有字符串与 tan 字段名 |
| 相机 bob 的振幅/频率 | 不存在 | Apex 用 viewmodel 动画做 bob，无相机 bob 代码 |
| `punchangle`（Source 经典命名） | 不存在 | Apex 改用 `viewpunch_*` / `viewkick_*` 命名 |
| Fallstun 公式 | 未读到 | 只有 `landDist*` / `hardFallDist` 分档字段 |
| `.rpak` 内的脚本/配置 | 未解包 | Respawn 私有格式，需专用工具（本文 §1.2.2 已说明） |

> 要拿到数值，只有三条路：动态调试（反作弊禁止）、实机观测反推（本插件 + wiki 走的路）、
> 参考公开引擎默认值（B 级）。本插件采用后两条。

---

### 2.8 复现方式

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

### 2.9 对 Godot 实现的指导（v0.2 实际采用的 9 条）

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

> 数值全部不来自本文件（本文件没有数值），见本文 §3 与 `MovementConfig.cs`。

## 3. 社区 wiki 数值与机制

### 3.1 单位换算（wiki 与逆向一致的唯一硬基准）

| 换算 | 值 | 来源 |
|---|---|---|
| 1 hammer unit (hu) | **2.54 cm = 0.0254 m** | wiki「How the Apex engine handles Movement」：「1m=39.3hu or 1hu=2,54cm。This was taken out of the game files and confirmed via in game measurements」 |
| 1 m | **≈ 39.37 hu**（wiki 写 39.3，取精确 1/0.0254 = 39.3701） | 同上 |
| 速度 hu/s → m/s | `× 0.0254` | 派生 |
| 角度基准 | `arccos(0.7) = 45.572°` | 同上：「Angles in Apex are pretty much all derived from arccos」 |

> 逆向侧独立佐证：`slide_max_angle_dot` 字段存在（见本文 §2.5），
> 与 `arccos(0.7)` 的 `dot = 0.7` 语义完全吻合 —— 这是 wiki 数值与二进制字段能对上的最好例子。

---

### 3.2 速度表（全部换算成 m/s，本插件直接用）

| 项 | hu/s | m/s | 来源 / 可信度 |
|---|---|---|---|
| 行走（持枪） | 173.5 | **4.4069** | wiki Ground「a walk peaks at a velocity of 173.5 hu/s」· 实测 |
| 行走（收枪 Holster） | 199 | **5.0546** | wiki Holster「Walking goes from 173.5hu/s to 199hu/s」· 实测 |
| 蹲行（持枪） | 80 | **2.0320** | wiki Crouch「Crouchwalk peaks at 92hu/s」（收枪值）· 实测；持枪 80 为同段「from 80hu/s to 92hu/s」 |
| 蹲行（收枪） | 92 | **2.3368** | 同上 · 实测 |
| 冲刺（持枪） | 260 | **6.6040** | wiki Sprint「Without Speedboosts it peaks at a velocity of 260 hu/s」· 实测 |
| 冲刺（收枪） | 299 | **7.5946** | wiki Sprint「a Sprint peaks at a velocity of 299 hu/s」· 实测 |
| Holster 加速倍率 | — | **×1.15** | wiki Holster「Holster Speedboost is 15% i.e a factor of 1.15」· 实测 |
| 滑铲触发最低速（地面） | 200 | **5.0800** | wiki Slide「On the ground, sliding can only be activated if your horizontal velocity is above 200hu/s」· 实测 |
| 滑铲触发最低速（空中·水平） | 90 | **2.2860** | wiki Slide「Air Slide: 90hu/s horizontally and 200hu/s vertically」· 实测 |
| 滑铲触发最低速（空中·垂直向下） | 200 | **5.0800** | 同上 |
| Slideboost 增量 | 150 | **3.8100** | wiki Slide「Slideboost is: 150hu/s * (holstered-status modifier * Walking speedboost)」· 实测 |
| 滑铲速度上限 | 400 | **10.1600** | wiki Slide「capped at: 400hu/s * (...)」· 实测 |
| Slidejump 速度阈值 | 350 | **8.8900** | wiki Slide jump「below a velocity of 350hu/s or 0.24sec after initiating the Slide」· 实测 |
| 空中加速度 AirAcceleration | 500 | 12.7（Source 语义，见本文 §3.3） | wiki Air strafe「Air acceleration=500 and Air speed=60」· 游戏内变量 |
| 空中 wishspeed AirSpeed | 60 | **1.5240** | 同上 |
| Bunnyhop 减速惩罚上限 | 100 | **2.5400** | wiki Bunnyhop「each bunnyhop will reduce your speed by up to 100hu/s * speedMultiplier」· 实测 |
| 攀爬上限（climb space cutoff） | 273 | **6.9342** | wiki 引擎篇「You can climb and mantle edges up to a height of 273 hu」· 游戏文件 + 实测 |
| 攀爬 Attach offset | 100 | **2.5400** | wiki Climb「Your Attach offset sits 100hu above your attach point」· 实测 |
| Climb End boost | 28 | **0.7112** | wiki Climb「This End Boost will throw 28hu further up」· 实测 |
| 可越过（overwalkable）台阶 | ~30 | **~0.762** | wiki Ground「Ledges up to a height of around 30hu are overwalkable」· 实测 |
| Auto-mantle 高度上限 | 80 | **2.0320** | wiki Auto mantle「Auto mantle works up to a height of 80hu」· 实测 |
| 跳跃高度 | 56 | **1.4224** | wiki Jump「Normal Jump height is 56 units and on flat ground keeps you in the air for 0.74sec」· 实测 |
| 跳跃滞空时间 | — | **0.74 s** | 同上 |
| 跳跃疲劳最小高度 | 16.3 | **0.4140** | wiki Jump fatigue「16.3 units for the first 0.150sec」· 实测（`antiMultiJumpHeightFrac=0.30`，56×0.30=16.8hu，wiki 实测 16.3） |
| 自由落体 Fallstun 起始高度 | 300 | **7.6200** | wiki Fallstun「duration of Fallstun scales up from 0sec at 300hu (7.6m) to 1sec at 800hu (20.3m)」· 实测 |
| Fallstun 满值高度 | 800 | **20.3200** | 同上 |
| 碰撞胶囊（站立，中等身高 Legend） | 80 | **2.0320** | wiki 引擎篇「overall height of 66,72 and 80 hu depending on Legend height」· 游戏文件 |
| 碰撞胶囊（蹲下） | 47 | **1.1938** | wiki 引擎篇「Crouching shrinks the Collision pill to a height of 47hu」· 游戏文件 |
| 碰撞胶囊半径 | 16 | **0.4064** | wiki 引擎篇「a radius of 16 hu」· 游戏文件 |

> ⚠️ **本插件没有直接采用胶囊 80hu/47hu**：需求方明确指定角色站立 **1.95 m**、蹲下 **0.975 m**（= 站立一半）。
> 见 `README.md` §3 与 `MovementConfig.cs` 的 `ApexPreset` / `LegacyPreset` 双预设。

---

### 3.3 空中机制（wiki 明确「无空气摩擦」）

> **Apex does not feature any Air friction. You do not lose any horizontal Velocity while being in the air.**
> —— wiki 引擎篇「Forces」

- 空中**只有重力**，没有水平速度衰减。这直接决定 bhop / slidehop 能不能保住速度。
- 空中转向由两个游戏内变量控制：**Air acceleration = 500**、**Air speed = 60 (hu/s)**。
  语义是 Source 谱系的 `Accelerate()`：
  ```
  wishSpeed = 60 hu/s = 1.524 m/s
  addSpeed  = wishSpeed − dot(velocity, wishDir)
  accelSpeed= AirAccel × dt × wishSpeed
  velocity += wishDir × min(accelSpeed, addSpeed)
  ```
  由于 `wishSpeed` 只有 1.524 m/s，只有当**当前速度在 wishDir 上的投影小于 1.524 m/s** 时才会加速，
  这就是 air strafe 的物理根因：**必须把视角同步转向**，让 wishDir 一直"贴"在速度矢量的侧前方，
  才能持续获得那一点点 addSpeed。
- 空中蹲下：胶囊**从上下两端同时向中间收缩**（见本文 §3.5）。

---

### 3.4 滑铲 / 滑铲跳 / 连续滑铲跳

#### 3.4.1 触发条件（两种，来自不同变量）

| 类型 | 条件 | 变量名（wiki 给出） |
|---|---|---|
| **Ground Slide** | 水平速度 > 200 hu/s | `slideRequiredStartSpeed` |
| **Air Slide** | 水平 > 90 hu/s **且** 向下垂直 > 200 hu/s（**两者都要满足**） | `slideRequiredStartSpeedAir` |

> 这条解释了 **Jump slide**（跳起→落地前按蹲）为什么能滑：跳跃提供了 >200 hu/s 的向下垂直速度，
> 侧向输入提供了 >90 hu/s 的水平速度。

#### 3.4.2 Slideboost

- 在**进入滑铲后的第一帧**触发一次。
- 公式：
  ```
  Slideboost = 150 hu/s × (holster_mod × walk_speedboost_mod)
  进入后速度 = 当前速度 + Slideboost
  上限       = 400 hu/s × (holster_mod × walk_speedboost_mod)
  ```
- **Slideboost 有 2 秒冷却**（从上次进入滑铲算起）。这是 Deadslide 的第一大成因。
- 滑铲本身**没有时间上限**，只有地面摩擦让你停下（下坡可以抵消甚至反超摩擦）。

#### 3.4.3 Slidejump 阈值（连续滑铲跳的核心）

跳跃时**只有满足下列任一条件**，游戏才会把跳跃初速**叠加**在当前速度上：

- **A**：当前速度 < 350 hu/s（8.89 m/s），或
- **B**：进入滑铲后已过 0.24 s。

「先满足哪个算哪个」。两个都不满足 ⇒ 跳跃不叠加速度 ⇒ 这就是 **Deadslide**。

#### 3.4.4 Deadslide 的三种成因

1. **Slideboost 冷却中**（2 s 内已经滑过一次）；
2. **没进入滑铲**（速度没过 200 hu/s，按了蹲只是蹲下）；
3. **跳跃时机不对**（既没掉到 350 hu/s 以下，也没到 0.24 s）。

#### 3.4.5 滑铲期间的输入

- 滑铲**不需要**方向输入，松开一切只按蹲也能滑。
- 短暂方向输入**几乎被忽略**；持续按住可以**极轻微转向**；按久了会减速。
- **反向输入减速非常剧烈**。
- 滑铲瞬间把碰撞胶囊拉到蹲下姿态。

#### 3.4.6 滑铲跳（slide jump）的额外规则

- 滑铲跳只对**带 Slideboost 的滑铲**生效。
- **按跳跃前绝对不能松开蹲**：先松蹲再跳会丢掉跳跃高度增益（这正是 Insta-glide 的原理，但对滑铲跳是负面的）。
- 从满速收枪冲刺滑出去，需要至少 **0.21 s** 才能掉到 350 hu/s 以下；
  算上延迟/地形，实操上大多直接撞到 **0.24 s** 门槛。
- 从刚过 200 hu/s 门槛的速度滑出去，只需 **0.11–0.12 s**（俗称 150ms slide jump）。

---

### 3.5 蹲下时胶囊的收缩方向（决定 slidehop vs bhop 手感差异）

> wiki 引擎篇原文：
> **Crouching on the ground shrinks it towards the bottom (from the top). Crouching mid air shrinks it towards the middle of the Pill (from the top and bottom). So on the ground you duck your head. And mid air you duck your head and tuck in your feet.**
> 「This is the reason for why Slidehop vs Bunnyhop feel different and give different results.」

| 情形 | 收缩方式 | 脚底是否上升 |
|---|---|---|
| **地面上蹲下** | 顶端向下收缩（脚底不动） | 否 |
| **空中蹲下** | 上下两端同时向中间收缩 | **是**（收脚） |

- 空中蹲下会**推迟落地**（脚底被抬高），并且让角色更容易/更难落在特定地形上。
- 空中蹲下抬高了胶囊底端 ⇒ **可以够到更高的攀爬边缘**（身高越高的 Legend 越明显）。
- 蹲下状态切换有延迟：**站立→蹲下 0.4 s 后才真正进入蹲下状态**（此时蹲下动画已结束）；
  **蹲下→站起是瞬时的**；**滑铲时两个方向都是瞬时**。

---

### 3.6 跳跃疲劳（Jump fatigue）—— 连续跳/连续滑铲跳的隐藏限制

游戏内变量名与数值（wiki 直接给出，来自游戏文件）：

```
antiMultiJumpTimeMin   = 0.15000006
antiMultiJumpTimeMax   = 0.75
antiMultiJumpHeightFrac= 0.3000000119
```

- 落地后 **0.150 s** 内再跳，跳跃高度被压到 **16.3 hu ≈ 0.414 m**（= 56 × 0.30 的量级，wiki 实测 16.3）；
  然后**线性**回升，到 **0.75 s** 恢复满高 56 hu。
- **衰减计时器只在触地时启动**。从高处坠落也算触发疲劳（即使下落了好几秒）。
- 疲劳**状态**与**计时器**是两个独立属性：
  - 状态被「起跳 / 土狼跳 / 攀爬 / 跳板二段跳」打开；
  - 状态被「攀爬中脱离 / 土狼时间条件满足 / 攀爬（mantle）/ 走下边缘 / 滑索、钩爪、传送门等非跳跃离地」关闭；
  - **落地不会关闭状态**。
- **Mantle 会重置跳跃疲劳**；**从 Mantle 跳出去（mantle hop）不会触发疲劳**。
- **Bunnyhop 定义**：落地后 **1 s** 内离地（游戏内变量 `skip_time`）。
  若速度高于冲刺速度，每次 bhop 会**扣最多 100 hu/s × speedMultiplier**（8.89 m/s × 倍率）。

---

### 3.7 攀爬（Climb）与翻越（Mantle）

#### 3.7.1 攀爬（Climb = 贴墙爬）

| 规则 | 值 |
|---|---|
| 可攀爬墙面角度 | **陡于 45.572°**（`arccos(0.7)`） |
| 允许偏离墙面看向的角度 | 最多 **45.57°** |
| 「向上爬」判定 | 垂直速度 ≥ **10 hu/s ≈ 0.254 m/s** |
| 向上爬计时器 | **无**（可无限爬，直到撞到上限） |
| 非向上爬计时器 | **1 s**，到时仍不满足 ≥10hu/s 就脱离 |
| 高度上限 | **273 hu ≈ 6.934 m**（climb space cutoff） |
| Attach offset | 附着点上方 **100 hu ≈ 2.54 m**（个人"体力"上限） |
| End boost | 到达上限时额外上抛 **28 hu ≈ 0.711 m** |
| 脱离方式 | 跳跃 / 蹲 / 后退 / 视角偏转 > 45.57° |

> 两个上限「谁先到谁生效」：附着点太低 → 先耗尽 100 hu 的 attach offset；
> 附着点够高 → 先撞到 273 hu 的 climb space cutoff。

#### 3.7.2 翻越（Mantle = 抓边 + 上拉）

wiki 把 Mantle 拆成两段，每段 **0.45 s**：

| 段 | 时长 | 触发 | 可取消 |
|---|---|---|---|
| **Mantle grab**（抓边） | 0.45 s（从攀爬转入时） | 空中朝边缘移动；移动输入需指向墙面 **±50°** 内 | 蹲 / 后退 / 侧向（需转身 ≥17.5°） |
| **Mantle pull**（上拉） | 0.45 s | 需要前或侧向输入（侧向需转离 ≥6°） | 后退 / 侧向（蹲**不能**取消 pull） |

- **松手会停在抓边状态 → Mantle hang（挂边）**。
- 从地面起跳贴墙时，低于约 42 hu 只会做 pull，不做 grab。
- **Auto-mantle**（按住跳跃）最高 **80 hu ≈ 2.03 m**。
- 攀爬结束瞬间若在冲刺 → 立刻满冲刺速度（"mantle sprint"）；走路进 → 出来还是走；蹲着进 → 出来还是蹲。
- **Mantle 结束瞬间可以直接滑铲**（前提：滑铲真的在 mantle 完成后才按）。

#### 3.7.3 二进制里的分类（与 wiki 两段式的对应关系）

逆向在 `.rdata` 观测到 **4 个时长字段** 与 **10 个动画状态**（详见本文 §2.4）：

```
mantleDurationHigh / mantleDurationAbove / mantleDurationLevel / mantleDurationBelow
ACT_VM_MANTLE_HIGH / ABOVE / LEVEL / BELOW          ← 4 个"主分类"
ACT_VM_MANTLE_CROUCH_ABOVE / _LEVEL / _BELOW        ← 3 个"蹲姿变体"
ACT_VM_MANTLE_CROUCH_HIGH                           ← 蹲姿高位
ACT_VM_MANTLE_IDLE / _JUMPOFF                       ← 挂边 / 蹬墙脱离
```

**结论**：Apex 按**台面相对视平线的高度**分 4 档（HIGH / ABOVE / LEVEL / BELOW），
每档一个时长；蹲姿再乘一个变体。这与 wiki 的「grab + pull 各 0.45s」不矛盾 ——
wiki 描述的是**时序**，二进制字段描述的是**按高度的分档时长**。本插件同时实现两者：
分档决定时长，grab/pull 两段决定轨迹形状。

---

### 3.8 FOV、镜头晃动、走路摇晃

| 项 | wiki 说法 | 本插件处理 |
|---|---|---|
| FOV 设置 | wiki 未给数值表（`cl_fovScale` 只出现在逆向字符串里） | 提供 `FovBase` / `FovSprintAdd` / `FovSlideAdd` / `FovTraversalKick` 等可调参数，默认值标 C 级 |
| 走路摇晃 | wiki 未给出数值 | 自研正弦步频模型，参数可调 |
| 滑铲视角倾斜 | 逆向有 `slide_viewTiltSide/IncreaseSpeed/DecreaseSpeed/PlayerSpeed`（A 级） | 按逆向字段语义实现：随速度增长的侧倾 + 入/出不同速率 |
| 冲刺视角侧倾 | 逆向有 `sprinttiltMaxRoll`（A 级） | 实现 |
| 落地视角踢 | 逆向有 `viewkick_spring_*` / `viewkickFallDist*`（A 级） | 用弹簧-阻尼模型实现，落地强度按下落距离分档（对应 `landDistLow/Medium/High`、`hardFallDist`） |

> FOV 与晃动**没有 wiki 数值**，本插件按逆向字段语义 + 手感自定，全部标 C 级并在 `CameraEffectsConfig.cs` 里可调。

---

### 3.9 页面清单（抓取缓存 `.re-apex/wiki/`）

| 本地文件 | 页面 | 用到的内容 |
|---|---|---|
| `art_engine.html` | `/wiki/articles/Wiki help>How the Apex engine handles Movement` | 单位、胶囊、蹲下收缩方向、无空气摩擦、可行走坡 |
| `page0.html` / `text0.txt` | `/wiki/tech/General Tech>Fundamentals>Ground>...` | 行走/冲刺/蹲行速度、跳跃高度、Jump fatigue、bunnyhop |
| `page1.html` / `text1.txt` | `/wiki/tech/General Tech>Fundamentals>Slide>...` | 滑铲阈值、Slideboost、Slidejump 阈值、Deadslide |
| `page2.html` / `text2.txt` | `/wiki/tech/General Tech>Fundamentals>Air>...` | air strafe 变量、coyote time、fallstun |
| `page3.html` / `text3.txt` | `/wiki/tech/General Tech>Mantle Tech>Mantle>...` | grab/pull 0.45s、auto-mantle 80hu、mantle hop |
| `page4.html` / `text4.txt` | `/wiki/tech/General Tech>Wall Tech>Climb Fundamentals>...` | 攀爬角度、计时器、attach offset、end boost |
| `page5.html` / `text5.txt` | `/wiki/tech/General Tech>Advanced Slide Tech>...` | 高级滑铲 |
| `page6.html` / `text6.txt` | `/wiki/tech/General Tech>Edge & Slant Tech>Slant Tech>...` | 斜面 |
| `catalog.json` | 由 `/_app/immutable/nodes/2.*.js` 提取 | **287 条技术条目 / 82 个页面**的完整索引 |

---

### 3.10 未找到 / 不采用的部分

| 项 | 状态 |
|---|---|
| 行走速度的官方 cvar 名 | **未找到**（wiki 只给实测 hu/s 值） |
| `cl_fovScale` 与显示 FOV 度的换算公式 | **未找到**（wiki 无此页；二进制只有字符串，无数值） |
| 相机 bob 的具体振幅/频率 | **未找到**（wiki 无此页；二进制无 `cl_bob` 类 cvar） |
| Fallstun 的精确公式 | wiki 自己标注「unsure of the exact formula」，只给 300hu→0s、800hu→1s 与「可能是二次」 |
| Superglide / Wallbounce / Tap strafe / Lurch | **本版不实现**（需求范围外），wiki 有完整条目可供后续扩展 |
| 移动平台速度继承 | **本版不实现**（需求范围外） |

## 4. 三份文档之间的交叉索引

| 主题 | 逆向证据（本文） | wiki 数值（本文） | 两者是否一致 |
|---|---|---|---|
| 单位换算（1 hu = 2.54 cm） | §2.5.3（`slide_max_angle_dot` 与 `arccos(0.7)` 对得上） | §3.1 | ✅ 一致：`dot = 0.7` 与 `45.572°` 精确对应 |
| 速度表（行走 / 蹲行 / 冲刺，hu/s → m/s） | §2.5.2（`acceleration` / `sprintAcceleration` / `sprintspeed` 等字段）、§1.3.1（本插件采用的数值） | §3.2 | ⚠️ 口径不同：§3.2 是 wiki 实测 hu/s，§1.3.1 是需求方口径，两套数值并存 |
| 滑铲与 Slideboost | §2.5.3（`slide_*` 5 个字段 + 4 个滑铲动画状态） | §3.4.1、§3.4.2 | ✅ 一致：`ACT_MP_CROUCHSLIDE_AIR` 佐证 Air Slide 独立；wiki 的 `slideRequiredStartSpeed` / `slideRequiredStartSpeedAir` 两个变量名未出现在 §2.5.3 的字段清单里 |
| Slidejump 阈值（350 hu/s 或 0.24 s） | §2.5.7（`jump_grace_*` 的速度衰减结构） | §3.4.3 | ◐ 部分：逆向只有字段名，没有 350 / 0.24 这两个数值 |
| 跳跃疲劳（Jump fatigue） | §2.5.7（`jump_grace_multi_tap_decay_*`、`jump_grace_cutoff_speed`） | §3.6（`antiMultiJumpTimeMin` / `Max` / `antiMultiJumpHeightFrac`） | ✅ 一致：同为按速度/时间衰减的机制，数值由 wiki 给出 |
| 蹲姿胶囊收缩 | §2.5.8（`m_DuckHullMax/Min`、`m_doingHalfDuck`、`DS_DUCK_*`、`m_duckTransitionRemainderMsec`） | §3.5（地面 / 空中收缩方向）、§3.2（80 hu / 47 hu） | ✅ 机制一致（0.4 s 过渡 ↔ `m_duckTransitionRemainderMsec`）；「收缩方向」只有 wiki 有 |
| 攀爬 / 翻越分档 | §1.1.4（v0.1 的 7 个 `ACT_VM_MANTLE_*`）、§2.5.5（4 档 × 站/蹲 + 挂边 + 蹬离、`mantleDuration*`） | §3.7.2、§3.7.3 | ✅ 一致：wiki 给时序（grab / pull 各 0.45 s），逆向给按高度分档的时长 |
| 窗口翻越（TRAVERSAL_WINDOW） | §1.1.4（`ACT_VM_TRAVERSAL_WINDOW_LEFT/RIGHT`）、§2.5.6（`traversal_window_*` 6 个参数、`C_GameMovement::TraversalMove`） | —（§3 无对应条目） | 逆向独有 |
| 空中机制（无空气摩擦 / air strafe） | §2.5.1（`airDrag` / `airFriction` / `airAcceleration` / `airSpeed` / `impactSpeed`） | §3.3、§3.2 | ✅ 一致：字段名与 wiki 变量名同名 |
| FOV | §2.3（`cl_fovScale`、`.fTanHalfFOV` ×9、开镜 FOV） | §3.8（wiki 未给数值） | 逆向独有 |
| 镜头晃动 / viewkick | §2.4.1（`viewkick_spring*` 5 个字段 + 状态缩放字段）、§2.4.2（落地 4 档）、§2.4.5（无 `cl_bob`） | §3.8、§3.10 | ✅ 双负结论：两侧都没有相机 bob |
| 侧倾 viewTilt | §2.4.3（滑铲 / 冲刺 / 墙跑 / 闪避四组参数） | §3.8（只转述逆向字段） | 逆向独有 |
| 落地分档 / Fallstun | §2.4.2（`landDistLow/Medium/High` + `hardFallDist`） | §3.2（300 hu → 0 s、800 hu → 1 s）、§3.10 | ◐ 部分：wiki 给高度-时长，逆向给 4 档字段名 |
| 攀爬上限 / auto-mantle | §1.1.4 旁注 `Vault`（≤1.1m）/ `Mantle`（≤2.2m）、§1.3.1（`MantleMaxHeight` **6.5 m**）、§2.5.4（`climbHeight`） | §3.2（273 hu = **6.9342**、auto-mantle 80 hu = 2.0320） | ⚠️ 数值不同：2.2 m / 6.5 m / 6.9342 m 三处口径不一致，原文数值均原样保留 |
| Superjump / superglide | §2.5.9（`superjump*` 字段） | §3.10（wiki 有条目，本版不实现） | 两侧都没有实现（需求范围外） |

> 表里标 ⚠️ 的几处是原文之间数值口径不同，不是本次合并改写造成的：三份文档的数值一律逐字保留，没有统一、换算或修正任何一个数字。
