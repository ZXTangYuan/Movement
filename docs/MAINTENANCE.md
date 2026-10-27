# 文档维护约定

> 这份文件回答一个问题：**改了代码，我该同步改哪些文档？**
>
> 文档一旦和代码对不上，比没有文档更糟 —— 后来的人（包括三个月后的你）会信错数。

## 1. 文档分工（谁写什么，避免重复）

| 文件 | 只负责 | 不负责 |
|---|---|---|
| [README.md](README.md) | 是什么 / 怎么跑 / 怎么用 / 文档索引 | 参数值、历史、根因 |
| [ARCHITECTURE.md](ARCHITECTURE.md) | 模块地图 / 数据流 / 状态机 / 测试分层 | 具体数值 |
| [PARAMETERS.md](PARAMETERS.md) | **所有参数的当前值（唯一事实源）** | 为什么是这个值 |
| [PHYSICS-AUDIT.md](PHYSICS-AUDIT.md) | 哪些地方符合/不符合物理，以及为什么 | 参数表 |
| [VERSION-HISTORY.md](VERSION-HISTORY.md) | 每个版本改了什么、为什么、实测数据 | 当前值（会过期） |
| [BUGS.md](BUGS.md) | 已修 bug 的症状/根因/修法 | 手感调整 |
| [REVERSE-ENGINEERING.md](REVERSE-ENGINEERING.md) | Apex 逆向证据与 wiki 数值（历史依据） | 本插件当前实现 |
| `selftest/*.txt` | 每次发版的自检原始输出 | 解读 |

**一条铁律**：同一个数字只出现在**一个**地方。当前值只在 `PARAMETERS.md`；
历史值只在 `VERSION-HISTORY.md`；证据出处只在 `REVERSE-ENGINEERING.md`。

## 2. 改代码 → 改文档 对照表

| 你改了什么 | 必须同步 |
|---|---|
| `MovementConfig.cs` / `CameraEffectsConfig.cs` / `GaitModel.cs` 的**默认值** | `PARAMETERS.md` 对应行 |
| 新增一个配置字段 | `PARAMETERS.md` 加一行；有推导的在 `说明` 列写公式 |
| 删除 / 废弃一个字段 | `PARAMETERS.md` §12 加一行（写明替代者） |
| 改状态机 / 新增状态 | `ARCHITECTURE.md` §5 状态图 + `VERSION-HISTORY.md` 新版本节 |
| 改物理模型（摩擦/跳跃/落地…） | `PHYSICS-AUDIT.md` + `VERSION-HISTORY.md` + `PARAMETERS.md` |
| 修掉一个有根因的 bug | `BUGS.md` 加一条（症状 → 根因 → 修法 → 断言编号） |
| 加 / 改测试 | `ARCHITECTURE.md` §4 的项数表 + `README.md` 的自检命令注释 |
| 发版 | 下面第 3 节全套 |

## 3. 发版检查清单

1. `dotnet build ProjectA.csproj` → **0 警告 0 错误**
2. 纯数学自检：`determinism_test.tscn` → `=== APEX MOVEMENT SELFTEST PASS ===`
3. 真实物理集成：`integration_test.tscn` → `=== INTEGRATION PASS（N 项断言） ===`
4. 归档原始输出：`docs/selftest/vX.Y.Z-{determinism,integration}.txt`
5. `PARAMETERS.md` 末尾的版本号注释更新为 `最后核对：vX.Y.Z`
6. `VERSION-HISTORY.md` 顶部加新版本节 + 更新目录表
7. `README.md` 更新：版本号、断言计数、自检命令注释里的分项数
8. `ApexPlayer.cs` 里 HUD 版本号**两处**（Label 的 `Text` 与快照行）
9. 截图有变化就重跑 `shot_testbed.tscn` / `shot_debug_renderer.tscn` 并更新 `images/`

## 4. 断言计数怎么维护

断言总数出现在三个地方，改测试时必须同时改：

1. `ARCHITECTURE.md` §4 的项数表（每套一行 + 合计）
2. `README.md` 的自检命令注释（分项相加 = 总数）
3. `ApexPlayer.cs` 无（HUD 不报断言数）

> 加法算错是历史上最常见的文档漂移。改完用
> `grep -c "\[PASS\]"` 数一遍归档的 raw 输出，不要靠记忆。

## 5. 风格约定

* 中文用全角标点；中英文之间不加空格（`0.75 m` 这种带单位的除外）。
* 字段名一律用反引号包住：`SlideMinStartSpeed`。
* 表格列顺序固定：`参数 | 值 | 单位 | 等级 | 说明`。
* 数值**不要四舍五入**（代码里是 `4.407f` 就写 4.407）。
* 图放 `docs/images/`，引用用相对路径 `images/xxx.png`。
* 每个"实测"数字后面尽量给出**断言编号或复现场景名**，否则三个月后无法复核。

## 6. 发布到独立仓库（github.com/ZXTangYuan/Movement）

本插件在宿主工程 `project-a` 里位于 `addons/apex_movement/`，
独立仓库里则是**根目录**。两边的历史用 `git subtree` 保持同源。

### 6.1 首次 / 一次性配置

```bash
cd <project-a 根目录>
git remote add movement git@github.com:ZXTangYuan/Movement.git   # 已配好则跳过
```

> 认证走 SSH（`~/.ssh/id_ed25519_github` + `~/.ssh/config` 里 `Host github.com` 段）。
> 用 `ssh -T git@github.com` 验证，应回 `Hi <用户名>! You've successfully authenticated`。

### 6.2 每次发版后推送

```bash
cd <project-a 根目录>

# ① 先把插件目录拆成独立历史（分支名固定，重复执行会更新该分支）
git branch -D apex-movement-only 2>/dev/null
git subtree split --prefix=addons/apex_movement -b apex-movement-only

# ② 在远端历史之上合并（**不要 force push**，否则会丢掉独立仓库里的 LICENSE 等文件）
rm -rf ../movement-push && git worktree add ../movement-push -b movement-sync movement/main
cd ../movement-push
git merge --allow-unrelated-histories --no-edit apex-movement-only
git push movement movement-sync:main

# ③ 清理
cd - && git worktree remove --force ../movement-push && git branch -D movement-sync
```

**为什么不用 `git subtree push`**：它每次重新 split，产生的 SHA 与远端已有的
merge commit 不同，会被判成 non-fast-forward。走上面这套「split → merge → push」
可以保留远端的独立文件，也能持续累积历史。

### 6.3 独立仓库里有什么

```
plugin.cfg  plugin.gd          插件入口
core/  camera/  godot_bridge/  纯 C# 部分（不依赖宿主工程）
player/  testbed/  tests/      节点层、测试场地、两套自检
docs/                          本文档目录，自包含（images/ + selftest/）
LICENSE                        独立仓库原有，**不要覆盖**
```

宿主工程特有的东西（`project.godot`、`.gitmodules`、其它 addon、`docs/design/`）
**不**进独立仓库 —— 这正是用 subtree 而不是直接推整个仓库的原因。

