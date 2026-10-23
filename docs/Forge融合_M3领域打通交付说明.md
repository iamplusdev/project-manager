# Forge 融合 · M3 领域打通 交付说明

> 状态：**进行中**（2026-10-23）。已完成 WP3.1 / WP3.2 / WP3.3（部分）与 WP3.6（主壳视觉收口）；
> 未完成 WP3.4 剩余部分（补写元数据）、WP3.5 模板升级检测、Git 工作台与发版页。
> 出口门禁 G3 当前 **4/5 达成**（详见第 5 节）。

---

## 1. 本阶段做了什么

| WP | 工作包 | 状态 | 代码位置 |
|---|---|---|---|
| WP3.6 | 主壳视觉统一（D1 收口） | ✅ 主壳部分 | `Views/MainWindow.xaml`、`Themes/Controls.xaml` |
| WP3.1 | 组件识别接入白名单 | ✅ | `Application/Components/ComponentScanner.cs` |
| WP3.2 | Git init 纳入 + 实时状态 | ✅（查询侧） | `CliInterop/GitRunner.GetStatusAsync` |
| WP3.3 | 项目详情页 | ⚠ 部分（模块清单未展示） | `MainWindow` 详情区 |
| WP3.4 | 打开已有项目 | ⚠ 登记已完成，缺「补写元数据」 | `Application/Projects/ProjectImporter.cs` |
| WP3.5 | 模板升级检测 | ❌ 未开始 | — |

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

`GitRunner.GetStatusAsync` 走三条命令，而不是一条 `git status -sb` 硬解析：

| 步骤 | 命令 | 为什么 |
|---|---|---|
| ① 是否仓库 | `rev-parse --is-inside-work-tree` | 新建的**零提交仓库**里 `rev-parse --abbrev-ref HEAD` 会失败，用这个才不会把空仓库误判为"不是仓库" |
| ② 分支 | `symbolic-ref --short HEAD`，失败回落 `rev-parse --abbrev-ref HEAD` | 前者在零提交仓库也有效；后者用于分离头指针场景 |
| ③ 改动数 | `status --porcelain` | 每行一个条目，含未跟踪文件；只数行数，不解析具体状态码 |

输出解析抽成 `GitStatusSnapshot.FromOutputs(...)` 静态方法——**解析规则可单测**，不用为每条规则真起进程。

摘要文案：`未初始化 Git` / `main · 工作区干净` / `main · 2 处未提交改动` / `Git 状态不可用：<原因>`。

**没有做的**：ahead/behind、remote 探测。理由：详情页不需要，且这类查询在无网络时会拖慢打开速度；等 Git 工作台（M3 后段）再引入。

### 3.1 证据

`GitStatusTests` 18 项断言：CRLF 与空行、空分支回退、四种摘要文案；外加**真 git 链路**（`init -b main` → 写文件 → 查到 1 处改动 → 普通目录判为非仓库）。本机有 git，该链路实际执行并通过（日志无"已跳过"）。

---

## 4. UI 变更

| 位置 | 变化 |
|---|---|
| 侧栏「组件」 | 由禁用态开放：点击切到组件页；选中态跟随当前页（不再写死） |
| 组件页 | 组件名 + 相对路径 + 标记文件名 + 技术栈徽标；空态按三种原因分别提示（未选项目 / 扫描中 / 无声明） |
| 详情页 | 新增「仓库状态」卡片（分支 + 改动数）；「待接入」文案改为只承诺尚未做的部分 |
| 项目详情元信息 | 去掉了 `已初始化 Git` 这个**库内快照**——它可能是过期的（用户可能后来删了 `.git`），仓库状态一律以实时查询为准 |

> 这条是修正而非新增：`StoredProject.GitInitialized` 表示"生成时是否执行过 init"，与"现在是不是仓库"是两回事，界面上不该混用。

---

## 5. 出口门禁 G3

| 门禁项 | 结论 | 依据 |
|---|---|---|
| 生成的项目在组件列表中不出现 `docs/ assets/ config/ output/` | ✅ | `ExcludesNeverScanDirectories`：把四个目录都写进白名单，结果只出 1 个组件 |
| `git init` 成功后仓库清单可见，失败时项目仍可用且提示清晰 | ⚠ 部分 | init 与状态查询已通；「仓库清单」页面（跨项目汇总）尚未做，当前可见形态是详情页的仓库状态卡 |
| 外部项目登记后显示"外部项目"而非"PM 生成" | ✅ | 卡片徽标与 `SelectedMeta` 都回退为「外部项目」；`Generated` 字段落库 |
| 模板升级提示可触达，且补结构不删除既有目录 | ❌ | WP3.5 未开始 |
| 主壳信息架构与 UI 方案 §6.2 逐项对齐 | ✅ | 顶栏四控件 + 侧栏四项/折叠 + 主区三态 + SnackBar |

---

## 6. 自动化证据

```
dotnet build                                   →  0 个警告  0 个错误
tests/ProjectManager.Scaffolding.Tests         →  全部通过：120 项断言
tests/ProjectManager.Application.Tests         →  全部通过：177 项断言
```

Application 侧较上一轮 +42：组件识别 24 项、Git 状态 18 项。

视觉证据（真实运行截图）：`output/app-components.png`（组件页）、`output/app-detail-git.png`（详情 + 仓库状态），另见 WP3.6 的五张主壳截图。

---

## 7. 已知取舍与风险

| 取舍 | 说明 | 何时重新评估 |
|---|---|---|
| 只扫两层 | 深层的 monorepo（如 `apps/web/packages/ui`）识别不到 | 等出现真实用例再加「按 marker 递归」，并配扫描深度上限 |
| 白名单只认顶层目录名 | 带 `/` 的声明被忽略（视为配置错误） | 模板需要声明子路径时 |
| 每选一次项目起 3 个 git 进程 | 单项目可接受（毫秒级），批量场景不行 | Git 工作台需要跨仓库汇总时改为批量/缓存 |
| `IGitRunner` 接口新增方法 | 测试用的 `FakeGitRunner` 需同步实现（已加） | — |
