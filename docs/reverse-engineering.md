# Apex 移动系统逆向报告

> 目标：`F:\SteamLibrary\steamapps\common\Apex Legends\r5apex_dx12.exe`
> 版本：**v3.0.3.65**（`gameversion.txt`），构建 `R5pc_r5-301_J65_CL11630615_FSv30_1_2026_10_02_09_54`
> 方式：**只读**静态分析。全程 `open(path,"rb")`，未写入、未修改游戏安装目录任何文件。
> 工作目录：`.re-apex/`（临时产物，已 gitignore）

---

## 1. 证据等级定义

| 等级 | 含义 |
|---|---|
| **A** | 在二进制中**直接观测**到（字符串、枚举、结构、指令） |
| **B** | 由引擎谱系（Source 2 / Titanfall）推导，未在二进制中直接读到该值 |
| **C** | 设计选择 / 需实机手感校准，**无**可靠静态证据 |

---

## 2. 观测到的（A 级证据）

### 2.1 PE 结构

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

### 2.2 移动相关的 cvar 名称存在

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

### 2.3 cvar 注册点（A 级）

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

### 2.4 Apex 特有的翻越分类（A 级，最有价值的发现）

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
> 完整复现 7 分类需要动画系统配合，超出本次范围（见 §5）。

### 2.5 其他观测到的移动相关系统（A 级，但未实现）

| 系统 | 证据 |
|---|---|
| 墙上跑 | `WallRunStartPos`、`GetNearestWallrunPatchPoint`、`wallrun_curveDebug`、91 处含 `wallrun` |
| 滑索 | 135 处含 `zipline`、`ziplineInitialEntities` |
| 钩爪 | 182 处含 `grapple`、`GrappleData` |
| 滑铲 | 69 处含 `Slide`、`slide_start_3p` |
| 闪避 | 44 处含 `dodge` |

---

## 3. 没读到的（以及为什么）—— 必须诚实说明

### 3.1 cvar 默认值（数值）读不到

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

### 3.2 `.rpak` 里的脚本读不到

Apex 全部脚本资源在 `.rpak`（8290 个文件 / 65.5GB）中，**没有 `.vpk`**。
`.rpak` 是 Respawn 私有格式（非 Source 的 VPK），**需要专用解包工具**。
本次未解包。

### 3.3 `r5apexdata.bin` 是加密的

4096 字节，内容为不可读的编码数据（疑似签名/校验块），非配置。

---

## 4. 本插件采用的数值与依据

全部参数集中在 `core/MovementConfig.cs`，**每个字段都标注了证据等级**。

### 4.1 核心速度与几何 —— 已按需求方口径标定（等级 A-需求方）

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

### 4.2 其余参数 —— 谱系推导 + 设计选择

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
> 但 4.1 节那六项是需求方明确给定的，不在此列。

## 5. 明确没做 / 做不到的

| 项 | 状态 | 原因 |
|---|---|---|
| cvar 精确默认值 | **没拿到** | 在 BSS 里，需动态调试（见 §3.1） |
| `.rpak` 脚本 | **没解** | 私有格式，需专用工具 |
| 7 种 MANTLE 分类的完整复现 | **只做了 2 态** | 需要动画系统配合 |
| 窗口翻越（TRAVERSAL_WINDOW） | **没做** | 需要窗口几何检测 |
| 墙上跑 / 滑索 / 钩爪 | **没做** | 不在本次需求内 |
| 动态验证 | **没做** | 反作弊 + 用户要求不碰游戏环境 |

---

## 6. 复现方式

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

## 7. 对 Godot 实现的指导结论

从 A 级证据直接得到的 3 条设计约束：

1. **沿用 Source 谱系的加速/摩擦公式**（`accel × dt × wishSpeed`、`drop = max(speed,stopSpeed) × friction × dt`）
   —— 因为 cvar 体系确认是 Source 谱系。本插件 `ApexMovementCore` 已按此实现。

2. **攀爬必须按高度分类**（不是单一动作）—— `ACT_VM_MANTLE_*` 的 7 个变体证明 Apex 按相对高度区分。
   本插件用 `VaultMaxHeight` / `MantleMaxHeight` 两阈值近似。

3. **翻越过程需要相机插值**（`traversal_viewLerpOutPos/Angle`）—— 本插件当前是纯位移，
   相机直接跟随；若观感生硬，后续可按这两个字段的思路加相机 lerp。
