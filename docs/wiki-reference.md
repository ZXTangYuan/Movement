# Apex 移动系统 —— 社区 wiki 数值与机制参考（v0.2 标定依据）

> 来源：**https://apexmovement.tech/wiki**（Apex Movement Wiki，作者 Mokeysniper / G0f / JayTheYggdrasil / Ilses 等）
> 抓取时间：2026-10。抓取方式：`curl` 拉 SSR 页面 → 剥离标签 → 落盘到 `.re-apex/wiki/`（已 gitignore）。
> 页面清单见文末 §9。
>
> **优先级声明**：wiki 与二进制逆向冲突时，**以逆向为准**。wiki 负责提供**数值与手感描述**，
> 逆向负责提供**机制存在性与字段结构**。本文件里每条数值都标注来源与可信度。

---

## 1. 单位换算（wiki 与逆向一致的唯一硬基准）

| 换算 | 值 | 来源 |
|---|---|---|
| 1 hammer unit (hu) | **2.54 cm = 0.0254 m** | wiki「How the Apex engine handles Movement」：「1m=39.3hu or 1hu=2,54cm。This was taken out of the game files and confirmed via in game measurements」 |
| 1 m | **≈ 39.37 hu**（wiki 写 39.3，取精确 1/0.0254 = 39.3701） | 同上 |
| 速度 hu/s → m/s | `× 0.0254` | 派生 |
| 角度基准 | `arccos(0.7) = 45.572°` | 同上：「Angles in Apex are pretty much all derived from arccos」 |

> 逆向侧独立佐证：`slide_max_angle_dot` 字段存在（见 `reverse-engineering-v0.2.md` §5），
> 与 `arccos(0.7)` 的 `dot = 0.7` 语义完全吻合 —— 这是 wiki 数值与二进制字段能对上的最好例子。

---

## 2. 速度表（全部换算成 m/s，本插件直接用）

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
| 空中加速度 AirAcceleration | 500 | 12.7（Source 语义，见 §3） | wiki Air strafe「Air acceleration=500 and Air speed=60」· 游戏内变量 |
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

## 3. 空中机制（wiki 明确「无空气摩擦」）

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
- 空中蹲下：胶囊**从上下两端同时向中间收缩**（见 §5）。

---

## 4. 滑铲 / 滑铲跳 / 连续滑铲跳

### 4.1 触发条件（两种，来自不同变量）

| 类型 | 条件 | 变量名（wiki 给出） |
|---|---|---|
| **Ground Slide** | 水平速度 > 200 hu/s | `slideRequiredStartSpeed` |
| **Air Slide** | 水平 > 90 hu/s **且** 向下垂直 > 200 hu/s（**两者都要满足**） | `slideRequiredStartSpeedAir` |

> 这条解释了 **Jump slide**（跳起→落地前按蹲）为什么能滑：跳跃提供了 >200 hu/s 的向下垂直速度，
> 侧向输入提供了 >90 hu/s 的水平速度。

### 4.2 Slideboost

- 在**进入滑铲后的第一帧**触发一次。
- 公式：
  ```
  Slideboost = 150 hu/s × (holster_mod × walk_speedboost_mod)
  进入后速度 = 当前速度 + Slideboost
  上限       = 400 hu/s × (holster_mod × walk_speedboost_mod)
  ```
- **Slideboost 有 2 秒冷却**（从上次进入滑铲算起）。这是 Deadslide 的第一大成因。
- 滑铲本身**没有时间上限**，只有地面摩擦让你停下（下坡可以抵消甚至反超摩擦）。

### 4.3 Slidejump 阈值（连续滑铲跳的核心）

跳跃时**只有满足下列任一条件**，游戏才会把跳跃初速**叠加**在当前速度上：

- **A**：当前速度 < 350 hu/s（8.89 m/s），或
- **B**：进入滑铲后已过 0.24 s。

「先满足哪个算哪个」。两个都不满足 ⇒ 跳跃不叠加速度 ⇒ 这就是 **Deadslide**。

### 4.4 Deadslide 的三种成因

1. **Slideboost 冷却中**（2 s 内已经滑过一次）；
2. **没进入滑铲**（速度没过 200 hu/s，按了蹲只是蹲下）；
3. **跳跃时机不对**（既没掉到 350 hu/s 以下，也没到 0.24 s）。

### 4.5 滑铲期间的输入

- 滑铲**不需要**方向输入，松开一切只按蹲也能滑。
- 短暂方向输入**几乎被忽略**；持续按住可以**极轻微转向**；按久了会减速。
- **反向输入减速非常剧烈**。
- 滑铲瞬间把碰撞胶囊拉到蹲下姿态。

### 4.6 滑铲跳（slide jump）的额外规则

- 滑铲跳只对**带 Slideboost 的滑铲**生效。
- **按跳跃前绝对不能松开蹲**：先松蹲再跳会丢掉跳跃高度增益（这正是 Insta-glide 的原理，但对滑铲跳是负面的）。
- 从满速收枪冲刺滑出去，需要至少 **0.21 s** 才能掉到 350 hu/s 以下；
  算上延迟/地形，实操上大多直接撞到 **0.24 s** 门槛。
- 从刚过 200 hu/s 门槛的速度滑出去，只需 **0.11–0.12 s**（俗称 150ms slide jump）。

---

## 5. 蹲下时胶囊的收缩方向（决定 slidehop vs bhop 手感差异）

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

## 6. 跳跃疲劳（Jump fatigue）—— 连续跳/连续滑铲跳的隐藏限制

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

## 7. 攀爬（Climb）与翻越（Mantle）

### 7.1 攀爬（Climb = 贴墙爬）

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

### 7.2 翻越（Mantle = 抓边 + 上拉）

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

### 7.3 二进制里的分类（与 wiki 两段式的对应关系）

逆向在 `.rdata` 观测到 **4 个时长字段** 与 **10 个动画状态**（详见 `reverse-engineering-v0.2.md` §4）：

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

## 8. FOV、镜头晃动、走路摇晃

| 项 | wiki 说法 | 本插件处理 |
|---|---|---|
| FOV 设置 | wiki 未给数值表（`cl_fovScale` 只出现在逆向字符串里） | 提供 `FovBase` / `FovSprintAdd` / `FovSlideAdd` / `FovTraversalKick` 等可调参数，默认值标 C 级 |
| 走路摇晃 | wiki 未给出数值 | 自研正弦步频模型，参数可调 |
| 滑铲视角倾斜 | 逆向有 `slide_viewTiltSide/IncreaseSpeed/DecreaseSpeed/PlayerSpeed`（A 级） | 按逆向字段语义实现：随速度增长的侧倾 + 入/出不同速率 |
| 冲刺视角侧倾 | 逆向有 `sprinttiltMaxRoll`（A 级） | 实现 |
| 落地视角踢 | 逆向有 `viewkick_spring_*` / `viewkickFallDist*`（A 级） | 用弹簧-阻尼模型实现，落地强度按下落距离分档（对应 `landDistLow/Medium/High`、`hardFallDist`） |

> FOV 与晃动**没有 wiki 数值**，本插件按逆向字段语义 + 手感自定，全部标 C 级并在 `CameraEffectsConfig.cs` 里可调。

---

## 9. 页面清单（抓取缓存 `.re-apex/wiki/`）

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

## 10. 未找到 / 不采用的部分

| 项 | 状态 |
|---|---|
| 行走速度的官方 cvar 名 | **未找到**（wiki 只给实测 hu/s 值） |
| `cl_fovScale` 与显示 FOV 度的换算公式 | **未找到**（wiki 无此页；二进制只有字符串，无数值） |
| 相机 bob 的具体振幅/频率 | **未找到**（wiki 无此页；二进制无 `cl_bob` 类 cvar） |
| Fallstun 的精确公式 | wiki 自己标注「unsure of the exact formula」，只给 300hu→0s、800hu→1s 与「可能是二次」 |
| Superglide / Wallbounce / Tap strafe / Lurch | **本版不实现**（需求范围外），wiki 有完整条目可供后续扩展 |
| 移动平台速度继承 | **本版不实现**（需求范围外） |
