# 已修 bug 归档（根因均已实测确认）

> 这里记录**根因被实测确认**的 bug：症状 → 根因 → 修法 → 验证方式。
> 纯手感调整、参数标定不放这里（那些在 [VERSION-HISTORY](VERSION-HISTORY.md)）。
>
> 为什么单独成文：每一条都是一个"看起来像玄学、实际有精确根因"的案例，
> 将来改这块代码前先扫一眼，能省掉重新踩坑的时间。

## 目录

| 分组 | 条目 |
|---|---|
| [v0.4.1](#v041-新增) | ⑱ 落地抖动 / ⑲ 坡面落地丢速度 / ⑳ 滑铲跳断链 / ㉑ 相机空中摇晃死分支 |
| [v0.4](#v04-新增) | ⑬ 贴地吸附压进地面 / ⑭ 疲劳跳高度为 0 / ⑮ 19cm 台阶卡死 / ⑯ 网格线卡角色 / ⑰ 窗口翻越落点撞窗楣 |
| [v0.3](#v03-新增) | ⑧–⑫ |
| [早期五条](#站着却完全走不动--贴地吸附把胶囊压进地面) | ① 站着走不动 ② 疲劳跳高度 0 ③ 19cm 台阶 ④ 网格线 ⑤ 窗口落点 |
| [其余](#-收枪散步被误判成冲刺--用速度反推意图) | ⑥ 收枪被误判冲刺 ⑦ 侧倾几乎不动 + 三个附带问题 |

---


## v0.4.1 新增：⑱ ⑲ ⑳ ㉑

### ⑱ 翻越 1.3 m 平台时角色悬在半空、vy 掉到 −41 却纹丝不动

**症状**（需求方截图 + 探针复现）：贴墙 → `Space` → 按住 `W`，人停在脚底 0.87 m 处，
`state=Air`、`onGround=false`，`vy` 每 tick 更负（一路到 −41），但 `pos` 完全不变。

**排除项**：向下射线命中地面在 y=0（距离 1.847）、前后上三向射线全 `none`、
胶囊 `intersect_shape` 的 overlap count = **0** ⇒ 不是被几何体卡住，是**速度被吃光**。

**根因**：`GodotMovementWorld.QueryNormalAt` 在"扫掠刚好停在接触面边缘"时
`GetRestInfo` 打空（`ProbeDepth` 只有 2 cm，胶囊其实还没真正重叠），于是走了
`return fallback.Normalized()` 兜底 —— 而 `fallback = -delta.Normalized()`，
即"运动方向的反向"这种**伪法线**。`MoveWithCollision` 拿它做
`_vel = F3.ProjectOnPlane(_vel, normal)`，把水平与竖直分量**一起**投影掉。

逐帧插桩（`TRACE pre/post`）证据：

```
TRACE pre  t=67 y=1.8397 z=32.5936 vy=4.918 vz=0.254
TRACE post t=67 y=1.8467 z=32.5940 vy=-0.000 vz=-0.000     ← vy 被吃掉
TRACE pre  t=68 y=1.8467 z=32.5940 vy=-0.346 vz=0.254
TRACE post t=68 y=1.8466 z=32.5940 vy=-0.345 vz=0.000      ← 位置不再变化
```

**修法**：两级兜底（见 §6.2 ④）—— ① 原地把胶囊半径与半球外扩 `ProbeDepth` 再查一次；
② 仍失败则把 `-fallback` 投影到水平面当"竖直墙"法线，只挡水平分量。
**宁可当成墙（最差少走一点），也不要把竖直速度抹掉（永久卡死）。**

回归：`ClimbTest` 新增 1.3 m 平台用例；`integration_test` 新增「1.3 m 平台 W+Space 能站上去」。

### ⑲ 滑铲在坡上是"越陡越慢"，与直觉相反

v0.4 及之前滑铲用一条与坡度无关的 `SlideFriction = 0.9` 做指数衰减，
重力分量被 `_vel = F3.ProjectOnPlane(_vel, _groundNormal)` 直接投掉 ⇒
**下坡反而比平地减速更快**。

修法：改成真实物理 `a = g·sinθ − μ·g·cosθ`（先加下坡重力分量、再减库仑摩擦），
θ 由 `_groundNormal` 与 up 的夹角给出。副产品：`tanθ = μ` 的临界角自动成立，
μ=0.45 ⇒ 24.23°，比这更陡的坡上滑铲永远停不下来 —— 这是物理的必然，不是特判。

回归：`FeatureTest` 新增「平地下坡上坡三种坡度下的滑铲加速度符号与量级」；
`integration_test` 新增「15° 下坡滑铲 1 s 后比平地更快」。

### ⑳ `SampleFriction` 的 meta 回退链缺失，只有碰撞体挂 meta 时才生效

需求要"不同材质不同摩擦系数"，第一版只在命中节点自己身上读 meta `friction`，
但场地里通常把材质 meta 挂在 `StaticBody3D`（或再包一层的分组节点）上，
而 `IntersectRay` 返回的 collider 可能是子级的 `CollisionShape3D` 宿主 ⇒ 读不到、静默用默认值。

修法：`GodotMovementWorld.SampleFriction` 沿父链**逐级回退最多 4 层**
（够覆盖"分组节点 → 刚体 → 碰撞体"，但不一路爬到场景根，避免误捡全局 meta），
任一级取到就用。这样"给 `StaticBody3D` 挂一个 meta `friction`"就够了，
不用给每个 `CollisionShape3D` 单独配。

```gdscript
# 场地里加一种材质只要这三行（testbed_builder.gd 的 _build_friction_lanes）
var body := StaticBody3D.new()
body.set_meta("friction", 0.05)      # 冰面
add_child(body)                       # 挂个带 CollisionShape3D 的 MeshInstance3D 即可
```

### ㉑ 高墙爬不上去：到顶脱手被"反作弊规则"永久拉黑

**症状**：6 m 以上的墙，爬到 `ClimbAttachOffset`(2.54 m) 处脱手后**再也抓不回去**，
人贴墙往下掉，按 W 也没反应 —— 高墙永远上不去。

**根因**：`DetachWallGrabAtTop()` 里调了 `RememberClimbSurface()`。
而重附着规则是"必须先掉回上次 attach 点以下才能再抓"（wiki L172-175，防刷攀爬），
于是"到顶脱手"这个**不是玩家放弃**的动作，被当成"玩家主动脱手"记进了黑名单 ⇒
后续每一次重抓都在 `here > _lastClimbAttachY` 处被拒。实测卡在 2.54 m。

**修法**：`DetachWallGrabAtTop()` **故意不调** `RememberClimbSurface()`。
理由写进代码注释：到顶脱手是"爬升距离到上限"，玩家还按着 W，应该能落回去继续爬；
反作弊规则只针对玩家主动脱手（`S` / 跳 / 甩视角），那些走 `DetachWallGrab`。

**回归**：`FeatureTest` 两条 —— 「单段爬升不超过 `ClimbAttachOffset`（不是瞬移到顶）」
实测单段最大 2.91 m；「到顶脱手后能重新抓墙，分段爬到台面」实测 7.5 m 墙能登顶。

---

## v0.4 新增：⑬ ⑭ ⑮ ⑯ ⑰

### ⑬ 扒墙默认化后，贴着墙走下小台阶会被"凭空吸上墙"

`WallHangRequiresCrouch` 改成 `false` 之后，扒墙的进入条件只剩"离地 + 够得到墙 + 正在下落"。
而**离开地面的那一 tick `_onGround` 已经是 false，但人其实还站在台沿上**，
于是"贴着墙往下走一小步"这种最普通的动作被判成"空中下落贴墙" ⇒ 瞬间被吸上墙。
自检 ① 报的症状是 `state=Climb onGround=True z=-1.574` —— 一边说在地面上，一边说在攀爬。

修法：附着判定里加一道**脚下地面余量**门槛（`WallGrabMinGroundClearance = 0.5 m`）：

```csharp
F3 feetNow = _pos - _up * (_currentHeight * 0.5f);
if (_world.Raycast(feetNow, -_up, _cfg.WallGrabMinGroundClearance, out _, out F3 belowN) &&
    F3.Dot(belowN, _up) >= _cfg.WalkableNormalDot)
    return false;      // 脚下半米内还是地面 → 这是"走下去"，不是"掉下去"
```

### ⑭ 贴墙下滑到底会压进地面 —— 落地后彻底走不动

贴墙态是**运动学积分**（`_pos += _up * (vUp * dt)`），不走 `MoveWithCollision`，
所以一路下滑到地面时会把胶囊压进地里。一旦重叠，后续 `Sweep` 从已重叠的位置起扫返回
`fraction = 0` ⇒ 水平移动也被挡住，表现为"落地后原地卡死"。
修法：下滑分支里先向下打一条 `slideDown + 0.02 m` 的射线，命中可行走法线就**贴到地面并直接脱离**：

```csharp
if (vUp < 0f)
{
    F3 feetNow = _pos - _up * (_currentHeight * 0.5f);
    float slideDown = -vUp * dt;
    if (_world!.Raycast(feetNow, -_up, slideDown + 0.02f, out F3 groundHit, out F3 groundN) &&
        F3.Dot(groundN, _up) >= _cfg.WalkableNormalDot)
    {
        _pos = groundHit + _up * (_currentHeight * 0.5f);
        DetachWallGrab(jumped: false, drop: true);
        return;
    }
}
```

### ⑮ 翻越小障碍后几乎原地不动 —— 出口速度用的是"已经归零的当前速度"

`_vaultExitSpeed = hSpeed * VaultSpeedRetention` 里的 `hSpeed` 取自 `_vel`，
但走到障碍前时 `MoveWithCollision` 早已把速度**投影到立面**上 ⇒ 水平分量≈0，
于是翻完只留下 0.62 m/s，与需求"削减较小、翻完快速恢复满速"正好相反。
修法：用**意图速度**（`WishSpeed`）兜底 —— 玩家的意图不会因为撞墙而消失：

```csharp
float wish = WishSpeed(input);
if (wish > hSpeed) { horiz = WishDirection(input) * wish; hSpeed = wish; }
_vaultExitSpeed = hSpeed * _cfg.VaultSpeedRetention;
```

### ⑯ 1.0–2.03 m 是"死区" —— 既翻不过去，又挂不住

"太矮不挂"探针原来量的是 `AutoMantleMaxHeight`（2.03 m），而翻越上限是 `VaultMaxHeight`（1.0 m）⇒
**1.0–2.03 m 之间的障碍两套机制都拒绝**，玩家撞上去只能干瞪眼。
修法：探针改成按需求口径量 `VaultMaxHeight`，且相对**脚底**而不是胶囊中心
（跳跃贴墙那一瞬间脚底才 0.03 m 高，按中心算会平白多探 1 m），取 0.6 倍给 1.5 m 这种刚过档的墙留余量：

```csharp
F3 highOrigin = FeetPosition + _up * (_cfg.VaultMaxHeight * 0.6f);
if (!_world.Raycast(highOrigin, fwd, reach, out _, out _)) return false;   // 上方没墙 → 矮坎，不挂
```

### ⑰ 蹲下反而晃得更厉害 —— 把蹲伏修正混进了几何项

步态模型里若先用"降频后的步频"算步幅（`L = v / f`），蹲下会让步幅**变长** ⇒ 摆腿角变大
⇒ 垂直起伏反而变大，方向完全反了（自检 ⑱ 抓到的真实 bug）。
修法：几何项（步幅 / 摆腿角 / 腾空弹道）一律用**站立步频**推，蹲伏只按比例缩小最终幅度：

```csharp
float strideStand = speed / cadenceStand;                  // 几何项专用
float cadence     = cadenceStand * (1f - p.CrouchCadenceDrop * crouch);   // 只影响相位速度
...
vertical *= 1f - p.CrouchAmplitudeDrop * crouch;           // 蹲伏只缩幅度
```

> 另有三个**工具链**层面的坑（不是插件逻辑 bug，但都会让人误判插件有 bug）：
> ① `CheckButton` 在构造器里设 `ButtonPressed` 不会重算按下态图标（截图里该开却是关）→ 进树后 `SetPressedNoSignal`；
> ② `CanvasLayer` 暴露给 GDScript 的属性表里没有继承来的 `visible`，跨语言要读普通 bool 属性；
> ③ 截图脚本拍完第三人称图没把 `current` 切回玩家相机，导致两张"FOV 对比图"差异只有 0.2%（全在 HUD 区域）。

## v0.3 新增：⑧ ⑨ ⑩ ⑪ ⑫

### ⑪ 打滑态把垂直速度整体归零 —— 空中进滑区永远落不了地，悬在离地 22cm

`StepSlip` 的 `LockToPlane` 分支写的是 `_vel -= _up * F3.Dot(_vel, _up)` —— 这行把**垂直分量整体**
（含向下的重力速度）抹掉，于是空中进打滑体积后既下不去也上不来；同时贴地探测
（`Sweep` 向下 0.1 m）因为角色悬空 22cm 而永远打空。
症状很有迷惑性：HUD 明写 `state=Slip`，但 `ground=False`、`feet=0.22` —— 数值上看"在打滑"，
画面里人浮在半空。修法：只压掉**向上**分量

```csharp
float upVel = F3.Dot(_vel, _up);
if (upVel > 0f) _vel -= _up * upVel;      // 只杀向上，保留下落
```

并把重力 / `MaxFallSpeed` 处理从 `else if (!_onGround)` 提到独立 `if (!_onGround)`，
让 `LockToPlane` 与空中都走重力分支。回归断言："贴地滑道上仍然踩在地上（ground=True，不悬空）"
→ 实测 `OnGround=True feet=0.001`。

### ⑫ 截图抓帧落后于物理 tick —— 图上是 3.00 m/s，控制台是 8.00 m/s

`_save()` 里 `await RenderingServer.frame_post_draw` **只等一次**就 `get_texture().get_image()`，
Vulkan 下拿到的可能是"已提交但还没显示"的旧帧。而 HUD 文本在 `_Process` 里刷新、又比物理 tick
慢一拍 ⇒ PNG 上的 `speed=3.00` 与控制台 `speed=8.00` 对不上，视觉复核会得出错误结论。
修法：等**两次** `frame_post_draw` 再抓；所有 `_save(...)` 改成 `await _save(...)`；
`_save` 里打印抓帧前后两份 HUD 文本（`HUD@capture:` / `HUD@after:`）做交叉核对。
修完两份都是 `state=Slip speed=8.00 vy=0.00 ground=True`。

> 这条不是插件 bug，但**会让人误判插件有 bug** —— 所以同样记在这里。

### ⑧ 攀爬贴墙代码从未生效 —— 点积符号写反

`float dist = Dot(hitPoint - _pos, normal)` 在玩家位于墙外时是**负数**，而 `want = 半径 + 0.02` 是正数，
`if (dist > want)` 永不成立 ⇒ 玩家停在离墙 1 m 处；下一 tick 的到顶探针（射程 0.7 m）必然打空，
于是 `wallGone = true` → 立刻 `DetachClimbAtTop()` 给 End boost。逐 tick 轨迹判据：第 1 tick 就
`state=Air, vy=5.437`，而 `sqrt(2 × 20.78 × 0.7112) = 5.4366` —— 精确等于 End boost 的抛体初速。
修法：`dist = Dot(_pos - hitPoint, normal)`，位移 `_pos += normal * (want - dist)`
（同一个式子既处理"贴太远"也处理"已经陷进去"）。

### ⑨ 攀爬状态跳过了位移步骤 —— "按住 W 爬墙"原地不动

`Step()` 第 4 步 `if (_state == Climb) { StepClimb(input, dt); return; }` **提前 return**，
第 8 步 `MoveWithCollision` 与第 9 步 `UpdateGroundContact` 在攀爬时从不执行；而 v0.2 的
`StepClimb` 只赋 `_vel` 不位移 ⇒ 爬墙在原地抖动。修法：攀爬改运动学位移（照抄 `StepTraversal`），
每 tick 再贴墙钳制一次。

### ⑩ 测试替身把"刚好贴住盒顶"当成没撞到 —— 上墙后失重、钻进墙里

`AnalyticWorld.Sweep` 的盒顶 / 盒底分支用 `f >= 0f` 判命中，而贴面时 `f` 因 float 舍入得 **-5e-7**，
被判成未命中（地面分支早有容差，盒子分支漏了）。于是翻越 / 攀爬把胶囊**恰好**放到台面上之后
`onGround` 立刻变 false，角色一边"空中加速"一边钻进墙体（实测终点 `y=0.976, z=-3.244` 在墙里）。
修法：`contactEps = 1e-4` + `if (f < 0f) f = 0f`，并新增"台面上站得住 / 走出边缘会掉到地面 /
不会卡进墙体内部"三条断言锁死。

### ① 站着却完全走不动 —— 贴地吸附把胶囊压进地面

旧版每 tick 给一个 `-GroundStickSpeed*dt` 的向下速度来"保证贴地探测命中"。
这会让胶囊每 tick 下沉 0.5mm，逐渐穿透地面；一旦穿透，胶囊扫掠在"起点已接触"时
返回 `fraction=0`，于是**水平移动也被一起挡住**。

**修法**：删掉这个速度。贴地完全由 0.2 m 探针 + 吸附完成。

### ② 疲劳跳高度为 0 —— 贴地吸附在上升途中执行

贴地吸附会把胶囊直接压到接触面。若在**上升途中**执行，它会把整段跳跃初速吃掉。
满速跳（初速 7.69）第一 tick 上升 0.128 m，侥幸躲过 0.20 m 吸附距离；
**疲劳跳**（初速 4.21）第一 tick 只上升 0.071 m，第二 tick 就被吸附回地面 ⇒ 高度 0。

**修法**：`if (_state == Air && VerticalSpeed > 0.1f)` 时跳过吸附与着地判定。

> 条件里**必须**带 `_state == Air`：上坡时把速度投影到坡面后 VerticalSpeed 天然为正
> （15° 坡、4.4 m/s → +1.14 m/s）。无条件判定会让上坡被误判成跳跃，
> 角色在坡上一路弹跳（实测 15° 斜坡只爬升 0.11 m 就停住）。

### ③ 19cm 台阶就卡死 —— 缺少"上台阶"逻辑

wiki 明写 Apex 可以"直接走过"约 30hu（0.762 m）的台阶，但**胶囊扫掠不会自动爬台阶**：
底部半球会楔在台阶上沿的下侧，直接向上抬只会加深穿插。

**修法**：实现 `TryStepUp` —— **退后一个半径 → 抬升 → 水平推进（最小 0.75×半径）→ 下落找面**。
"退后"是关键：让球面脱离接触，抬升才自由。

### ④ 网格线把角色卡住

测试场地的地面网格线 / 刻度 / 标签原本都是带碰撞的 `StaticBody3D`，
形成 1~2cm 高的"台阶"。**修法**：装饰几何改用 `_deco_box()`（只有 MeshInstance3D，无碰撞）。

### ⑤ 窗口翻越落点撞在窗楣里

落点前推距离（0.90 m）小于墙厚 + 半径，胶囊头部卡进窗楣。
**修法**：`WindowForwardOffset` 提到 1.20 m，并在落点做收缩 2 cm 的重叠检查。

### ⑥ 收枪散步被误判成"冲刺" —— 用速度反推意图

`Sprinting` 原本写成 `HorizontalSpeed > WalkSpeed * 1.05`（4.627 m/s）。
但**收枪行走** = 173.5 × 1.15 = 199 hu/s = **5.055 m/s**，天然超过这条线 ⇒
收枪散步时相机 FOV 多 3°、冲刺侧倾多 1.5° 全部误触发。
这个 bug 只在截图复核时才暴露（自检断言没覆盖"收枪行走"这个组合）。

**修法**：`Sprinting = 冲刺意图(input.SprintHeld) ∧ 地面 ∧ 真的跑起来了`。
保留速度条件是为了覆盖"冲刺途中被 Fallstun 压下来"的情况。
新增 2 项断言锁死：收枪行走 5.068 m/s 且 `Sprinting == false`。

### ⑦ 侧倾几乎不动 —— 把"度/秒"当成指数时间常数

二进制里 `slide_viewTiltIncreaseSpeed` / `_DecreaseSpeed` / `sprinttilt*` 是
**度/秒的线性斜率**（正因为是线性斜率，才会分成"增大速率"和"减小速率"两个字段）。
但代码用 `Approach()`（指数逼近）消费它，于是 60°/s 被当成时间常数
1/1.047 ≈ 0.95 s —— 侧倾要 1 秒才到位。实测滑铲 0.2 s 时只到 **1.5°**（目标 5°）。

**修法**：新增 `MoveTowards()`（每帧最多走 `速率 × dt` 的线性趋近），侧倾改用它。
修后同样时机实测 **5.5°**，10 m/s 滑铲满值 5.00°，冲刺侧倾 2.17°。

> 教训：**"速率"这个词有歧义**。指数逼近的速率是「1/秒」，线性趋近的速率是「单位/秒」。
> 二进制字段名带 `Speed` 且成对出现（Increase/Decrease）时，几乎一定是线性斜率。

### 附带修掉的三个问题

| 问题 | 现象 | 修法 |
|---|---|---|
| `AnalyticWorld.Sweep` 把"已在表面上"当成没撞到 | 角色每 tick 被吸附下压 0.5mm 后掉进"探测不到地面"的黑洞 | 按 Godot `CastMotion` 语义返回 `fraction=0` 的命中 |
| 窗口扫描的探针上限只到头顶上方 0.4m | 窗楣在 2.5m 时扫不到，被误判成"没有窗楣" | 提到头顶上方 0.9m |
| `Sprint`/`Walk` 共用一个速度字段 | 无法表达"持枪 260 / 收枪 299" | 拆成 `SprintSpeed` + `HolsterMultiplier` |

> ⑥⑦ 都是**截图视觉复核**才发现的：自检断言只看数值、不看画面，
> 而这两个 bug 恰好是"数值在容差内、观感却错了"。这就是本轮坚持逐张 `read_image` 的原因。

---
