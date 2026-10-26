# ApexMovement 插件 v0.5.3

固定 tick（60 Hz）的**自研第三人称移动系统** + 相机子系统 + 可玩测试场地，Godot 4.7 / C#。

起点是只读逆向 Apex Legends v3.0.3.65 并按社区 wiki 标定数值，
**但从 v0.4 起不再对标 Apex** —— 手感规则全部按需求方口径重写，Apex 数值只作起点参考。
v0.5 起**常规系数回到真实地球量**（g = 9.80665 m/s²），效果量由设计目标反推。

---

## 1. 文档索引

**从这里开始，按你想知道什么来选：**

| 我想知道… | 看 |
|---|---|
| 怎么跑起来 / 按键是什么 | 本文 §3、§4 |
| **某个参数现在到底是多少** | [PARAMETERS.md](PARAMETERS.md) ← **唯一事实源** |
| 代码结构 / 状态机 / 一个 tick 怎么走 | [ARCHITECTURE.md](ARCHITECTURE.md) |
| 哪些地方符合物理、哪些是故意不物理 | [PHYSICS-AUDIT.md](PHYSICS-AUDIT.md) |
| 为什么改成现在这样（谁提的需求、实测数据） | [VERSION-HISTORY.md](VERSION-HISTORY.md) |
| 某个 bug 的根因 | [BUGS.md](BUGS.md) |
| Apex 逆向证据 / wiki 数值出处 | [REVERSE-ENGINEERING.md](REVERSE-ENGINEERING.md) |
| 改了代码该同步改哪些文档 | [MAINTENANCE.md](MAINTENANCE.md) |
| 每次发版的自检原始输出 | [selftest/](selftest/) |

---

## 2. 能力清单

**移动**　WASD / 蹲 / 跳 / 冲刺 / 收枪 / 空中转向 / 跳跃疲劳 / 土狼时间 / bunnyhop 减速 / Fallstun

**滑铲**　纯地面状态；平面速度入口（门槛 5.0 m/s）+ 坡度 boost（水平口径 3.81 m/s，沿坡 /cosθ）；
库仑摩擦 a = g·sinθ − μ·g·cosθ（μ 按地面材质可调）；无速度上限；
减速到蹲走速度 2.0 m/s 自动转蹲伏/蹲走；滑铲中 W 无效、A/D 微转向、S 主动制动

**三档障碍**　≤0.25 m 直接迈过 / 0.25–1 m W+Space 翻越 / >1 m 是墙（撞停，W+Space 起跳后攀爬）

**攀爬与扒墙**　必须「面向墙前进」或「跳出来的腾空」才抓墙；攀爬按住 W、松 W 转扒墙、S 立刻松手；
扒墙默认化（空中下落贴墙自动扒住）、握力消耗/回补、W+A/W+D 斜移、Space 蹬墙跳

**星球**　PlanetProfile 四档预设（地球/月球/火星/Apex-like）+ SetUpDirection(F3 up) 任意重力方向

**相机**　步态推导的 ∞ 摇晃（Gerono 双纽线，由视线方向承载）、弹簧视角踢、落地冲击分档、翻越插值、FOV 平滑

**调试**　`\` 菜单（FOV / 灵敏度 / 摇晃倍率 / 冲击倍率 / 星球下拉 / 第三人称 / 速度渲染开关）；
三维加速度分解箭头（重力/摩擦/输入/冲量/碰撞/合计，七色 + 实时数值标注）

---

## 3. 操作

| 按键 | 动作 |
|---|---|
| `W A S D` | 移动 |
| `Space` | 跳跃；地面朝小障碍按 = 翻越 |
| `Ctrl` | 蹲下 |
| `Shift` | 冲刺 |
| `Shift + Ctrl` | 滑铲（需平面速度 ≥ 5.0 m/s） |
| `Shift + Ctrl + Space` | 滑铲跳 |
| 走向 ≤0.25 m 台阶 | 直接迈过，水平速度零损失 |
| 走向 0.25–1 m 小障碍 + `W` + `Space` | 直接翻越 |
| 走向 >1 m 墙 + 只按 `W` | 撞墙停住，不会被吸上墙 |
| `W` 贴墙 + `Space` 跳起 | 抓住墙 → 攀爬 |
| 攀爬中按住 `W` / 松 `W` | 上爬 2 m/s / 转扒墙 |
| 空中下落贴墙（不按键） | 自动扒住 |
| 扒墙中 `S` | 立刻松手垂直下落 |
| 扒墙中 `Space` | 蹬墙跳 |
| 走进打滑体积 | 打滑（Slip） |
| `\` | 调试菜单开/关 |
| `H` | 收枪 / 持枪（收枪 ×1.15 速度） |
| `[` / `]` | FOV −2° / +2° |
| `R` | 回到出生点 |
| `Esc` | 释放鼠标（菜单打开时 = 关闭菜单） |

---

## 4. 运行

```bash
GODOT="D:/Godot/Godot_4.7_stable_mono/Godot_v4.7-stable_mono_win64_console.exe"

# 有窗口，可玩
"$GODOT" --path . --rendering-driver vulkan res://addons/apex_movement/testbed/apex_testbed.tscn

# 自检 A：纯数学（跨帧率确定性 + 11 套单元测试 = 298 项断言）
"$GODOT" --headless --path . res://addons/apex_movement/tests/determinism_test.tscn
# 期望：=== APEX MOVEMENT SELFTEST PASS ===

# 自检 B：真实 Godot 物理集成（65 项断言）
"$GODOT" --headless --path . res://addons/apex_movement/tests/integration_test.tscn
# 期望：=== INTEGRATION PASS（65 项断言） ===

# 截图（需要窗口 / 真实渲染）
"$GODOT" --path . --rendering-driver vulkan --resolution 1920x1080 \
         res://addons/apex_movement/testbed/shot_testbed.tscn
"$GODOT" --path . --rendering-driver vulkan --resolution 1600x900 \
         res://addons/apex_movement/testbed/shot_debug_renderer.tscn

# 上台阶顿挫 A/B 复现（同一场景，按 H 切换眼位平滑）
"$GODOT" --path . --rendering-driver vulkan res://addons/apex_movement/testbed/repro_stairs.tscn
```

**纯 C# 快速通道**（不启动 Godot，毫秒级，改代码时先用它）：
```bash
cd .re-apex/tools/apex_sim && dotnet run -c Release -- selftest all
# 或只跑一套：selftest slide | gait | camera | climb | sliphang | feature | physics | friction | bob | det
```

> **为什么需要两套自检**：自检 A 跑在 AnalyticWorld（纯数学碰撞替身）上，
> 覆盖不到 Godot 桥接层与相机应用层。历史上「无法移动」「Ctrl 穿地」
> 「跳跃初速被吸附吃掉」三个 bug 都只存在于桥接层，A 层完全复现不出来。
> 详见 [ARCHITECTURE.md](ARCHITECTURE.md) §4。

---

## 5. 关键数值速览（完整表见 [PARAMETERS.md](PARAMETERS.md)）

| 项 | 当前值 | 备注 |
|---|---|---|
| 站立 / 蹲下高度 | 1.90 / 0.95 m | 需求方口径 |
| 眼位 | 头顶 −0.10 m | |
| 重力 | 9.80665 m/s² | **真实地球**；切星球只改这个 |
| 行走 / 冲刺 / 收枪冲刺 | 4.407 / 6.604 / 7.595 m/s | 收枪 ×1.15 |
| 蹲走 | 2.0 m/s | 也是滑铲减速下限 |
| 跳跃 | 起跳速度 3.7537 m/s **固定**（按地球反推力度） | 地球实测峰高 **0.750 m**；月球 4.38 / 火星 1.93 |
| 驱动加速度 | 14 m/s² | 到顶速 0.47 s |
| 静 / 动摩擦系数 | μs 1.9 / μk 0.7 | 主刹停距离 3.18 m |
| 主动急停 | μk × 2.0，从最大制动**衰减**到 μk·g | BrakeRampTime 0.25 s |
| 滑铲入口门槛 | 平面速度 5.0 m/s | 用「与地面平行的分速度」判定 |
| 滑铲 boost | 3.81 m/s（水平口径，沿坡 /cosθ） | 无速度上限 |
| 滑铲临界角 | atan(μk) = 35.0° | 超过它下坡会一直加速 |
| 台阶 / 翻越 / 攀爬上限 | 0.25 / 1.0 / 6.9342 m | |
| 空中 wishspeed | 1.524 m/s | Apex 无空气摩擦 |
| 镜头摇晃 | 竖直 = min(步频, 3.0) Hz、横向 = 一半 | ∞ 由视线方向承载 |

---

## 6. 已知限制

| 项 | 状态 |
|---|---|
| 抓墙吸附瞬移（最多 0.88 m） | **保留**。改成「物理连续」会让墙态探针全部失效（实测 ClimbTest 10 项 + 集成 4 项红），见 [PHYSICS-AUDIT.md](PHYSICS-AUDIT.md) §3 |
| 空中 air-strafe / 土狼 / bunnyhop / slideboost | **有意保留的非物理**，都是手感设计，逐条列在 [PHYSICS-AUDIT.md](PHYSICS-AUDIT.md) §2 |
| 移动平台速度继承 | 未实现（需求范围外） |
| 木筏 / 刚体反作用力接口 | 未实现（需求方要求先不做） |
| 球形行星的胶囊旋转 | 参考系已预留（SetUpDirection），但 AnalyticWorld 的胶囊是固定 Y 轴形状，接真实球体时要改世界层 |
| Superglide / Wallrun / 滑索 / 钩爪 / Tap strafe | 未实现；逆向字段已记录在 [REVERSE-ENGINEERING.md](REVERSE-ENGINEERING.md) |
| 动画 | 无（纯物理位移 + 相机效果） |

---

## 7. 目录

```
addons/apex_movement/
  core/            纯 C# 求解器（不引用 Godot）
  camera/          纯 C# 相机效果
  godot_bridge/    Godot 世界查询实现
  player/          玩家节点 / 调试菜单 / 三维调试渲染器
  testbed/         可玩测试场地 + 截图工具 + 复现场景
  tests/           两套自检入口 + 11 套单元测试
  docs/            文档（本目录）
    images/        全部配图
    selftest/      每次发版的自检原始输出
```

模块级细节见 [ARCHITECTURE.md](ARCHITECTURE.md) §1。
