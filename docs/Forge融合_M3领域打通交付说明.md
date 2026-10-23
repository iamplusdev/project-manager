# Forge 融合 · M3 领域打通 交付说明

> 状态：**M3 主体完成**（2026-10-23）。已完成 WP3.1 / WP3.2（查询侧）/ WP3.3（部分）/ WP3.5 / WP3.6（主壳视觉收口）。
> 未完成：WP3.4 剩余部分（补写元数据）、WP3.2 跨项目「仓库清单」页、WP3.3 模块清单展示、Git 工作台与发版页。
> 出口门禁 G3 当前 **4/5 达成**，剩余一项为跨项目仓库汇总页（详见第 7 节）。

---

## 1. 本阶段做了什么

| WP | 工作包 | 状态 | 代码位置 |
|---|---|---|---|
| WP3.6 | 主壳视觉统一（D1 收口） | ✅ 主壳部分 | `Views/MainWindow.xaml`、`Themes/Controls.xaml` |
| WP3.1 | 组件识别接入白名单 | ✅ | `Application/Components/ComponentScanner.cs` |
| WP3.2 | Git init 纳入 + 实时状态 | ✅（查询侧） | `CliInterop/GitRunner.GetStatusAsync` |
| WP3.3 | 项目详情页 | ⚠ 部分（模块清单未展示） | `MainWindow` 详情区 |
| WP3.4 | 打开已有项目 | ⚠ 登记已完成，缺「补写元数据」 | `Application/Projects/ProjectImporter.cs` |
| WP3.5 | 模板升级检测 | ✅ | `Application/Projects/TemplateUpgradeService.cs` |

---

## 2. WP3.1 组件识别

### 2.1 规则（刻意保守的初版）

1. **白名单优先**：只认模板 `components` 声明的顶层目录（`templates.default.json` 中全栈是 `apps/contracts/packages/infra`，单体桌面工具是 `src/tests`），其余目录一概不参与。
2. **永不入列**：`docs / assets / config / output` 与 `.git / .vs / .idea / bin / obj / node_modules` 即使写进白名单也剔除（方案硬约束）。
3. **两层收敛**：顶层目录自身有标记文件 → 它就是一个组件；否则把它的**直接子目录**各算一个；不再向下递归（避免大仓库扫爆）。
4. **技术栈靠标记文件推断**：`*.csproj`/`*.sln` → .NET，`package.json` → Node，`go.mod` → Go，`Cargo.toml` → Rust，`pyproject.toml`/`requirements.txt` → Python，`pom.xml`/`build.gradle` → JVM，`Dockerfile`/`docker-compose.yml` → 容器；都没有则标「未识别」。
5. **只读**：不写文件、不落库。组件清单是"当前磁盘事实"，缓存留给上层内存。

### 2.2 为什么外部项目显示"没有组件声明"而不是猜

方案把组件边界定义为**模板声明的事实**。外部项目（无 `.pm-scaffold.json`）没有这份声明，任何"猜"都会把 `node_modules`、临时目录当组件。所以扫描器在白名单为空时直接返回空，UI 明确提示"缺少组件声明"，把决定权留给后续的「补写元数据」（WP3.4 剩余部分）。

### 2.3 证据

`ComponentScannerTests` 24 项断言，覆盖：硬排除目录、顶层自身有标记、子目录展开与排序、空目录仍算组件、白名单目录不存在时跳过、空/`null` 白名单不猜。

---

## 3. WP3.2 Git 实时状态

**原则不变**：Git 状态**不落库**（软件设计方案第 4 节），每次打开项目现查。

`GitRunner.GetStatusAsync` 走四条命令，而不是一条 `git status -sb` 硬解析：

| 步骤 | 命令 | 为什么 |
|---|---|---|
| ① 是否在仓库内 | `rev-parse --is-inside-work-tree` | 新建的**零提交仓库**里 `rev-parse --abbrev-ref HEAD` 会失败，用这个才不会把空仓库误判为"不是仓库" |
| ② 仓库根是不是本目录 | `rev-parse --show-toplevel` | 见 §3.1：不加这步会把上层仓库的状态当成项目自己的 |
| ③ 分支 | `symbolic-ref --short HEAD`，失败回落 `rev-parse --abbrev-ref HEAD` | 前者在零提交仓库也有效；后者用于分离头指针场景 |
| ④ 改动数 | `status --porcelain` | 每行一个条目，含未跟踪文件；只数行数，不解析具体状态码 |

输出解析抽成 `GitStatusSnapshot.FromOutputs(...)` 静态方法——**解析规则可单测**，不用为每条规则真起进程。

摘要文案：`未初始化 Git` / `未独立初始化 Git · 位于上层仓库 X 内` / `main · 工作区干净` / `main · 2 处未提交改动` / `Git 状态不可用：<原因>`。

### 3.1 修掉的缺陷：继承上层仓库状态

**现象**：把演示项目放在本仓库目录下（`output/seed-upgrade/OldProject`），详情页「仓库状态」显示 `main · 9 处未提交改动`——那 9 处是 **Project_Manager 自己的**改动，与该项目毫无关系。

**根因**：`rev-parse --is-inside-work-tree` 的语义是"当前目录是否位于某个工作树内"，**向上查找**，所以在任何仓库的子目录里都返回 `true`。仅凭它判定"这是仓库"，就会把上层的分支名与改动数冒名顶替成本项目的状态。

**修法**：加一步 `rev-parse --show-toplevel` 拿到真实仓库根，与传入的项目目录做路径比较；不等则返回 `InParentRepository(root)`——仍是"未独立初始化"，但把原因带给用户，而不是显示一个假状态。

**这不算吹毛求疵**：monorepo 子项目、把本项目自己当演示项目打开、以及任何"项目目录建在已有仓库里"的场景都会命中。误报比不报更坏——用户会以为项目处于脏状态，或以为自己初始化过 Git。

路径比较单独抽成 `GitStatusSnapshot.IsSamePath`：git 回来的路径与用户给的路径在**尾分隔符、分隔符风格、盘符大小写**上都不保证一致，必须三者都容忍（Windows 文件系统大小写不敏感）。

### 3.2 证据

`GitStatusTests` 覆盖三层：

- **解析规则**：CRLF 与空行、空分支回退、五种摘要文案；
- **路径比较**（5 项）：尾分隔符、`/` 与 `\` 混用、大小写、前缀相同的不同目录、父子目录；
- **真 git 链路**（本机有 git，实际执行）：
  - 常规链路 `init -b main` → 写文件 → 查到 1 处改动；普通目录判为非仓库；
  - 上层仓库场景：外层 `init` + 造脏文件 → 子目录查询须 `IsRepository == false`、`ParentRepository` 指向真实顶层、`ChangedFiles == 0`、分支为空、摘要含「位于上层仓库」与仓库名；
  - 反向场景：嵌套目录里自己 `git init` 过的，必须认成独立仓库（`ParentRepository is null`），防止改过头把真仓库也否掉。

断言有效性已验证：临时短路 §3 的上层仓库判断后，该组 7 项断言变红，还原即恢复全绿。

---

## 4. WP3.5 模板升级检测

### 4.1 判定依据

`templateHash`（模板配置文件的 SHA256）现记在项目根的 `.pm-scaffold.json` 里。`TemplateUpgradeService.Plan()` 只读比对，得出四态：

| 状态 | 触发条件 | 文案 |
|---|---|---|
| `NotManaged` | 无模板名（外部项目）或文件不存在 | 外部项目没有模板记录 / 缺少 `.pm-scaffold.json` |
| `TemplateMissing` | 有模板名但当前配置里已没有该模板 | 模板「X」不在当前配置中（可能已删除或改名） |
| `UpToDate` | 指纹一致 | 已与模板「X」对齐 |
| `Outdated` | 指纹不一致 → 继续算差 | 模板「X」已更新，可补 N 个目录 |

### 4.2 三条硬约束

1. **只加不删**：`Apply()` 只创建缺失目录与缺失的 `.gitkeep`；已存在的目录/文件一律跳过。模板里删掉的结构不在本项目里动——用户的代码可能就放在那儿。
2. **绝不覆盖**：占位文件只在不存在时写空内容；已存在的文件一个字节都不碰。
3. **补完写回指纹**：`ScaffoldMetadata.TryUpdateHash` 保留 `templateName/modules/createdAt` 等原字段，只更新 `templateHash/schemaVersion/upgradedAt`。不写回的话下次打开又提示"模板已更新"，变成永久红点。

### 4.3 两个容易踩空的地方

- **模块必须沿用元数据里的记录**，不能用全模块重算。否则当初没勾安装包的桌面项目会突然被告知该补 `output/installer`——诱导用户建出自己明确没要的目录。
- **占位文件规则要与生成时同源**（`TreeBuilder.EmptyLeafDirectories` + `IgnoredDirectoryPrefixes`），否则会出现"生成时不建、升级时建"的分叉。

### 4.4 UI

详情页新增「模板同步」卡片：状态文案 + 缺失目录清单（最多展示若干条，其余折叠为计数）+ 「补新增目录」按钮（仅 `CanApply` 时可用）。补完后走 SnackBar 反馈"已补 N 个目录、M 个占位文件，已更新模板指纹"。

### 4.5 证据

`TemplateUpgradeTests` 26 项断言：四态判定、无文件/坏 JSON/无 hash 的容错、按模块推算差集、`gitignore` 忽略目录不补占位、`Apply` 幂等（连跑两次第二次为空）、**既有文件不被覆盖**、指纹写回且其它字段保留、缺模板时不写文件。

---

## 5. UI 变更

| 位置 | 变化 |
|---|---|
| 侧栏「组件」 | 由禁用态开放：点击切到组件页；选中态跟随当前页（不再写死） |
| 组件页 | 组件名 + 相对路径 + 标记文件名 + 技术栈徽标；空态按三种原因分别提示（未选项目 / 扫描中 / 无声明） |
| 详情页 | 「仓库状态」卡（§3）+「模板同步」卡（§4.4）；「待接入」文案改为只承诺尚未做的部分 |
| 项目详情元信息 | 去掉了 `已初始化 Git` 这个**库内快照**——它可能是过期的（用户可能后来删了 `.git`），仓库状态一律以实时查询为准 |

> 这条是修正而非新增：`StoredProject.GitInitialized` 表示"生成时是否执行过 init"，与"现在是不是仓库"是两回事，界面上不该混用。

---

## 6. 副产物：截图工具的坑

验证过程中 `Title` 抓到的一直是 504×27 的一块——鼠标停留在项目路径上有 ToolTip，而 ToolTip 与主窗同属 `HwndWrapper[ProjectManager.App...]` 窗口类。`EnumWindows` 取第一个命中就会抓到它。改为**按面积取最大窗口**才稳定。（脚本在 `output/` 下，属本地验证工具，不入库。）

---

## 7. 出口门禁 G3

| 门禁项 | 结论 | 依据 |
|---|---|---|
| 生成的项目在组件列表中不出现 `docs/ assets/ config/ output/` | ✅ | `ExcludesNeverScanDirectories`：把四个目录都写进白名单，结果只出 1 个组件 |
| `git init` 成功后仓库清单可见，失败时项目仍可用且提示清晰 | ⚠ 部分 | init 与状态查询已通；「仓库清单」页面（跨项目汇总）尚未做，当前可见形态是详情页的仓库状态卡 |
| 外部项目登记后显示"外部项目"而非"PM 生成" | ✅ | 卡片徽标与 `SelectedMeta` 都回退为「外部项目」；`Generated` 字段落库 |
| 模板升级提示可触达，且补结构不删除既有目录 | ✅ | §4：详情页卡片 + `Apply` 只建不删；`UpdatesHashWithoutTouchingOtherFields`、`ApplyIsIdempotent`、`KeepsExistingFiles` 断言 |
| 主壳信息架构与 UI 方案 §6.2 逐项对齐 | ✅ | 顶栏四控件 + 侧栏四项/折叠 + 主区三态 + SnackBar |

---

## 8. 自动化证据

```
dotnet build                                   →  0 个警告  0 个错误
tests/ProjectManager.Scaffolding.Tests         →  全部通过：120 项断言
tests/ProjectManager.Application.Tests         →  全部通过：221 项断言
```

Application 侧较本轮起点（203）**+18**，全部来自上层仓库判定修复（路径比较 5 项 + 真 git 两条场景）。组件识别（24）、打开已有项目（27）、搜索过滤（15）、模板升级（26）等为上一轮已交付部分。

视觉证据（真实运行截图）：`output/app-components.png`（组件页）、`output/app-upgrade.png`（模板同步卡）、`output/app-git-parent.png`（仓库状态正确识别上层仓库），另见 WP3.6 的五张主壳截图。

---

## 9. 已知取舍与风险

| 取舍 | 说明 | 何时重新评估 |
|---|---|---|
| 只扫两层 | 深层的 monorepo（如 `apps/web/packages/ui`）识别不到 | 等出现真实用例再加「按 marker 递归」，并配扫描深度上限 |
| 白名单只认顶层目录名 | 带 `/` 的声明被忽略（视为配置错误） | 模板需要声明子路径时 |
| 每选一次项目起 4 个 git 进程 | 单项目可接受（毫秒级），批量场景不行 | 「仓库清单」页需要跨仓库汇总时改为批量/缓存 |
| 模板升级只看目录结构 | 模板里改的文件内容/模板文本不参与升级（只补目录与占位） | 需要"更新生成物模板片段"时单开工作包，且必须先解决覆盖用户改动的冲突问题 |
| 缺 `.pm-scaffold.json` 时不做"补写" | 见 WP3.4 剩余：补写要猜模块组合，猜错比不做更糟（会谎报对齐或诱导补多余目录） | 想清楚"按磁盘现有目录反推模块"的可行性再动手 |
| `IGitRunner` 接口新增方法 | 测试用的 `FakeGitRunner` 需同步实现（已加） | — |
