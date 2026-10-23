# M2 交付说明 · 新建项目向导 + 项目注册

> 日期：2026-10-23
> 阶段：M2（出口门禁 G2）
> 前置：S0 等价基线、M1 生成引擎（G1 通过）
> 本阶段目标（开发计划 4）：**用户可在 PM 内完成"新建项目"全流程，生成后立即在项目列表可见、可打开——此阶段结束即"可交付的最小可用产品"。**

---

## 1. 交付内容

### 1.1 新增项目（4 个）

| 项目 | 目标框架 | 职责 | 为什么单独成项目 |
|---|---|---|---|
| `ProjectManager.Storage` | net8.0 | 项目清单、设置持久化（JSON，原子写 + 损坏隔离） | 接口化后可在测试中用内存实现；换 SQLite 只改实现 |
| `ProjectManager.CliInterop` | net8.0 | Git CLI 封装（M2 只需 `git init`） | 进程调用必须统一超时与输出编码；不能被 UI 层各写一套 |
| `ProjectManager.Application` | net8.0 | 向导状态机、校验聚合、预览构建、生成编排、项目列表模型 | **让 M2 的核心逻辑可在无窗口环境完整单测**（93 项断言） |
| `ProjectManager.App` | net8.0-windows | WPF 视图、主题、薄封装样式、单实例、启动装配 | 只做表现，不含业务规则 |

> **结构增补说明**：方案 3.2 的依赖方向是 `App → Core / Storage / CliInterop`。本阶段把"向导状态机 + 生成编排"从 App 中抽出，独立为 `Application`（net8.0，零 WPF 依赖）。原因是若不抽，向导的"步骤合法性、预览清单、生成即注册"只能靠手工点界面验证；抽出后它们全部进了自动化断言。这是一次**有意的分层细化**，不改变方案的分层意图（UI 薄、逻辑纯、可测）。

### 1.2 工作包对照

| WP | 交付 | 落点 |
|---|---|---|
| WP2.1 主题与薄封装 | 亮/暗两套颜色令牌 + 23 个 `Pm*` 样式键 | `App/Themes/{Light,Dark,Controls}.xaml` |
| WP2.2 主壳 | 单行 44px 顶栏 + 200px 侧栏 + 空状态 + 项目概览 + `Ctrl+N` | `App/Views/MainWindow.xaml(.cs)` |
| WP2.3 向导外壳 | 独立模态 Window、`CenterOwner`、`ShowInTaskbar=false`、48px 步骤条、单 ViewModel 贯穿三步、关闭拦截、单实例 | `App/Views/NewProjectWindow.*`、`App.xaml.cs` |
| WP2.4 Step 1 | 名称/简介/路径 + 三张模板卡片（推荐徽标、默认选中）+ 即时校验 | 同上 |
| WP2.5 Step 2 | 模块勾选（全选/反选/清空）+ 派生标识只读区 + 勾选联动刷新预览 | 同上 |
| WP2.6 Step 3 | 三类清单 + 两档策略（合并 / 仅预览）+ `git init` 勾选 + 目标已存在提示 | 同上 |
| WP2.7 生成执行 | 异步执行 + 阶段进度文案 + 取消（阶段间隙）+ **回滚中禁关闭** | `Application/Wizard/GenerationCoordinator.cs` |
| WP2.8 结果页三态 | 成功（已在清单中）/ 失败（错误码）/ 回滚失败（清查清单）/ 注册失败（重试注册） | `App/Views/NewProjectWindow.xaml`（Result 区） |
| WP2.9 项目注册 + 最近列表 | 生成成功即写清单；按 `LastOpenedAt` 倒序；主壳即时可见并选中 | `Storage/JsonProjectRepository.cs`、`Application/Projects/ProjectListModel.cs` |
| WP2.10 设置页 | 模板来源只读展示 + 打开配置文件夹 + 主题切换（即时生效）+ 生成后打开目录开关 | `App/Views/SettingsWindow.*` |

---

## 2. 门禁 G2 证据

| 门禁项 | 结论 | 证据 |
|---|---|---|
| 方案 7.2「UI 验收」全部勾选 | ⚠ **部分完成，3 项待人工目视** | 见 2.1 |
| 生成后主壳立即出现该项目并可打开 | ✅ | `HappyPathGeneratesAndRegisters`：生成后清单 `Count==1`、名称/模板名正确；主壳 `ReloadAsync` 后自动选中最新项目 |
| 三套模板 × 典型组合各走一遍 | ✅（逻辑层） | 单体桌面工具（全模块 / 仅主模块）、轻量工具（无模块）在 `WizardTests` 与 S0 快照中覆盖；全栈模板由 M1 的 8 组等价快照覆盖 |
| 深浅色 + DPI 125/150/200% + 最小窗口无重叠 | ⚠ **待人工** | 两套令牌键集合完全一致（可静态校验）；DPI 与最小尺寸需在实际桌面目视 |
| 纯键盘可完成一次生成 | ⚠ **部分** | `Ctrl+N` 进入、`Tab` 顺序、`Enter` 触发默认按钮、`Esc` 取消生成均已接线；完整键盘路径需人工走查 |
| 结果页**不再出现**「加入项目清单」按钮 | ✅ | 结果页按钮为「打开目录 / 复制路径 / 重试注册 / 完成」，文案为"该项目已在项目清单中" |

### 2.1 自动化证据

```
dotnet build                                      →  0 个警告  0 个错误
dotnet run --project tests/ProjectManager.Scaffolding.Tests   →  全部通过：115 项断言
dotnet run --project tests/ProjectManager.Application.Tests   →  全部通过：93 项断言
```

`ProjectManager.Application.Tests` 覆盖（93 项）：

- **Storage**：落盘/读回、同路径去重（保留 Id 与首次创建时间）、`Touch` 重排最近、删除、**损坏文件隔离后仍可继续写入**、原子写不残留 `.tmp`、设置往返、损坏设置回退默认
- **Wizard**：初始状态（默认模板 = 单体桌面工具、模块全选、`CanGoNext=false`）、非法名称/保留名/超长拦截、路径不存在拦截、三步前进后退不丢数据、派生标识（`my project` → `MyProject` / `my-project`）、模块勾选联动预览目录数、目标已存在提示
- **生成闭环**：成功生成 + 立即注册 + `git init` 调用一次 + `GitInitialized` 落库；**仅预览不写任何字节且不注册、不跑 git**；**注册失败降级**（文件已在、结果仍为成功、提供重试注册、结论文案区分）；**git 失败不影响生成**

**断言器有效性验证**：临时把"仅预览"用例的期望值改为 `Success`，测试正确报出 `失败 1 项（通过 92 项）｜期望：Success｜实际：PreviewOnly`，确认不是空转。（验证后已还原。）

### 2.2 运行时验证（编译期不覆盖的部分）

XAML 的 `StaticResource` 与 BAML 只有在实例化时才解析，编译通过不等于能打开。用一次性冒烟程序（未入库）验证：

```
App.xaml 资源加载 OK
Bootstrapper OK：模板 3 套，来源 = 用户配置（%APPDATA%\ProjectManager\templates.json）
MainWindow 构造 OK
NewProjectWindow 构造 OK：默认模板 = 单体桌面工具，模块 3 个
SettingsWindow 构造 OK
预览 OK：待建目录 14，预览根节点 10，警告 = <无>
SMOKE-PASS
```

另用真实 `GitRunner`（非桩）验证一次完整链路：

```
git 可用性探测：True
Kind=Success  GitSucceeded=True  GitMsg="已初始化 Git 仓库"
有 .git 目录：True   清单记录数：1   清单 GitInitialized：True
GIT-PROBE-PASS
```

### 2.3 运行时发现并修掉的两个真实缺陷

这两个都是**编译期无法发现**的，值得记录：

1. **主题字典用相对 URI 会依赖"入口程序集"**。`new Uri("Themes/Dark.xaml", UriKind.Relative)` 在自身 App 里正常，换任何宿主程序集就报 `找不到资源"themes/light.xaml"`。已改为绝对 pack URI（程序集名动态取自 `typeof(ThemeService).Assembly`）。这直接影响将来"拆控件库"或"被其他进程加载"的可复用性。
2. **`RowDefinition.Height` 不接受 `sys:Double` 资源**。`<sys:Double x:Key="PmTopBarHeight">44</sys:Double>` 赋给 `GridLength` 属性会在加载时抛 `"44"不是属性"Height"的有效值`（WPF 不做 double→GridLength 隐式转换）。已拆成 `PmControlHeight`（double，供 `Height` 用）与 `PmTopBarRowHeight` / `PmSidebarColumnWidth` / `PmStepBarRowHeight`（GridLength，供布局用）。

---

## 3. 与方案的偏差（逐条说明理由）

| # | 方案原文 | 实际实现 | 理由 |
|---|---|---|---|
| D1 | 6.7 控件映射到 WPF-UI（`FluentWindow`/`InfoBar`/`BreadcrumbBar`） | **内置 WPF + `Pm*` 薄封装样式**，未引入 WPF-UI | ① 保持"任何机器 `dotnet build` 即可离线构建"（首次构建不需联网还原）；② 视图只引用 `Pm*` 键，换 WPF-UI 只需替换 `Themes/Controls.xaml` 内各 Template，**视图 XAML 零改动**；③ M2 门禁考核的是流程与状态，不是视觉库 |
| D2 | 5.2 SQLite + DPAPI | **JSON + 接口化仓储** | 项目数量为"几十条"量级；SQLite 需 NuGet 依赖，与 D1 同一取舍。`IProjectRepository` 已隔离，换 SQLite 只改实现，不动 Application/App |
| D3 | 独立 `FluentWindow`（自绘标题栏） | 系统标题栏 + 内容区 48px 步骤条 | `WindowChrome` 自绘标题栏的收益是观感，风险是拖拽/最大化/多屏 DPI 一系列平台坑。核心验收点（`CenterOwner`、模态、不进任务栏、单实例）不依赖自绘 |
| D4 | Step 3 窗口从 880×620 扩到 980×700 | **固定 900×660**，内容区自适应 | 方案自己把"向导尺寸在三步间跳动"列为风险；固定尺寸更稳，两列布局在 900 宽下够用 |
| D5 | 3.2 依赖方向 `App → Core/Storage/CliInterop` | 增加 `Application` 层 | 见 1.1 结构增补说明：为可测性 |

---

## 4. 运行方式

```bash
# 构建（离线可用，0 NuGet 依赖）
dotnet build

# 启动桌面端
dotnet run --project src/ProjectManager.App

# 两套测试（退出码即结果）
dotnet run --project tests/ProjectManager.Scaffolding.Tests
dotnet run --project tests/ProjectManager.Application.Tests
```

首次启动行为：

1. 在 `%APPDATA%\ProjectManager\` 创建用户数据目录；
2. 若用户目录没有 `templates.json`，从安装目录 `config/templates.json` **复制**一份（复制而非引用：安装目录会被更新整体替换且通常不可写）；
3. 模板三源优先级生效：用户配置 > 安装目录 > 内置默认；
4. 主题按设置应用（默认跟随系统，读 `HKCU\...\Themes\Personalize\AppsUseLightTheme`，读不到按浅色）。

## 5. 关键实现语义（供后续阶段接手）

| 语义 | 实现 | 为什么不能随便改 |
|---|---|---|
| 取消只在**阶段间隙**生效 | `GenerationCoordinator` 在每个阶段前 `ThrowIfCancellationRequested`；落盘阶段一旦开始交给引擎 journal 回滚（G8） | 若允许"落盘中取消"，会留下半生成目录，比彻底失败更难收拾 |
| 生成中关闭窗口 | 拦截 → 确认 → `Cancel()` → **等回滚完成再关**（`_closeAfterGeneration`） | 直接关会丢掉取消信号，用户以为已清理，实际留下半成品 |
| 注册失败 ≠ 生成失败 | `Kind=Success` + `RegistrationFailed=true` + 可重试注册 | 报"失败"会让用户重新生成，覆盖自己刚改的内容 |
| `git init` 失败不阻断 | `GitFailed=true`，仅结果页提示 | 仓库初始化是可选项，目录与文件才是主产物 |
| 同路径重复注册 | 保留原 `Id` 与首次 `CreatedAt`，更新名称/模板 | 否则最近列表会出现同一项目的多条记录 |
| 清单损坏 | 改名 `.corrupt-{时间戳}[-序号]` 留存，本轮以空清单继续并提示 | 直接覆盖会让用户永久失去清单；静默空清单会让人以为项目丢了 |

## 6. 遗留项（不阻塞 M2 出口）

| # | 项 | 影响 | 建议时点 |
|---|---|---|---|
| R1 | 深浅色、DPI 125/150/200%、最小窗口布局需**人工目视** | 可能有个别间距/对比度问题 | 交付验收时 |
| R2 | 完整键盘路径需**人工走查**（Tab 顺序、焦点可见性） | 无障碍体验 | 交付验收时 |
| R3 | 生成中被强杀会留下 journal 未清理 | 极端场景 | M3（开发计划 4.3 已列） |
| R4 | 设置页「重新加载模板」当前只提示重启 | 改模板需重启应用 | M4 模板生态 |
| R5 | 主壳项目概览仅为占位（Git 工作台等属 M3） | 功能未到，非缺陷 | M3 |
