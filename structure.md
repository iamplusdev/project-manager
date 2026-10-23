# Project_Manager 项目结构说明

```text
Project_Manager/
├── ProjectManager.sln
├── src/
│   ├── ProjectManager.Scaffolding/     # 生成引擎（零 UI / 零存储）
│   │   ├── IO/
│   │   ├── Models/
│   │   ├── Naming/
│   │   ├── Resources/
│   │   ├── Scaffolding/
│   │   ├── Templates/
│   │   └── Validation/
│   ├── ProjectManager.Storage/         # 项目清单与设置（JSON，原子写 + 损坏隔离）
│   ├── ProjectManager.CliInterop/      # 外部命令封装（Git CLI 侧）
│   ├── ProjectManager.Application/     # 向导状态机、生成编排、项目列表（零 WPF）
│   │   ├── Mvvm/
│   │   ├── Projects/
│   │   └── Wizard/
│   └── ProjectManager.App/             # WPF 桌面端
│       ├── Services/
│       ├── Themes/
│       ├── Theming/
│       ├── ViewModels/
│       └── Views/
├── tests/
│   ├── ProjectManager.Scaffolding.Tests/
│   ├── ProjectManager.Application.Tests/
│   └── fixtures/
│       ├── tools/
│       └── forge-baseline/
├── output/
├── assets/
│   ├── design/
│   └── icons/
├── docs/
└── scripts/
```

## 📖 文件夹作用说明

- `.gitattributes` : 行尾策略（`* text=auto`），避免跨平台 diff 噪音。
- `.gitignore` : 排除 `output/`、`.workbuddy/`、`bin/`、`obj/`、`.vs/` 等。
- `ProjectManager.sln` : 解决方案入口；含 5 个生产项目与 2 个测试项目。

### 生产代码（`src/`）

- `src` : 可编译源码根目录；**所有 .NET 项目都放在这里**。
- `src/ProjectManager.Scaffolding` : 脚手架生成引擎（零 UI / 零存储依赖，可无界面单测）。
- `src/ProjectManager.Scaffolding/IO` : 文件系统与时钟抽象，支撑故障注入与确定性测试。
- `src/ProjectManager.Scaffolding/Models` : 模板、模块、生成请求/结果、错误码等领域模型。
- `src/ProjectManager.Scaffolding/Naming` : 命名派生（pascal / kebab / snake），规则在此冻结。
- `src/ProjectManager.Scaffolding/Resources` : 内置模板配置（`templates.default.json`，随程序集嵌入）。
- `src/ProjectManager.Scaffolding/Scaffolding` : 树展开、文档产物、生成主流程、回滚日志、元数据。
- `src/ProjectManager.Scaffolding/Templates` : 模板加载与校验（三源优先级：用户目录 > 安装目录 > 内置）。
- `src/ProjectManager.Scaffolding/Validation` : 表单校验（名称 / 简介 / 生成路径）。
- `src/ProjectManager.Storage` : 受管项目清单与设置持久化；接口化仓储，换 SQLite 只改实现。
- `src/ProjectManager.CliInterop` : Git CLI 进程封装（统一超时看门狗、UTF-8 输出、错误归一）。
- `src/ProjectManager.Application` : 应用编排层，零 WPF 依赖 —— M2 的核心逻辑都在这里，因此可被测试完整覆盖。
- `src/ProjectManager.Application/Mvvm` : 最小 `ObservableObject` / `RelayCommand`（不引第三方 MVVM 库）。
- `src/ProjectManager.Application/Projects` : 主壳项目列表模型（最近项目、打开时间刷新、降级提示）。
- `src/ProjectManager.Application/Wizard` : 三步向导状态机、预览树构建、生成协调、结果三态。
- `src/ProjectManager.App` : WPF 桌面端（主壳 + 新建项目向导 + 设置），只做表现与平台交互。
- `src/ProjectManager.App/Services` : 启动装配（`Bootstrapper`）与应用级依赖容器（`ShellContext`）。
- `src/ProjectManager.App/Themes` : 亮/暗颜色令牌与 `Pm*` 薄封装样式（视图只引用这些键）。
- `src/ProjectManager.App/Theming` : 主题服务（跟随系统 / 浅 / 深，绝对 pack URI 加载）。
- `src/ProjectManager.App/ViewModels` : 主壳视图模型。
- `src/ProjectManager.App/Views` : `MainWindow` / `NewProjectWindow` / `SettingsWindow`。
- `src/ProjectManager.App/config`（构建产物，不在源码树内）: 构建时从 `Scaffolding/Resources/templates.default.json` 链接而来，随包分发后由首次启动复制到用户目录——**不维护第二份 JSON**。

### 测试与固件（`tests/`）

- `tests` : 测试与固化资产。
- `tests/ProjectManager.Scaffolding.Tests` : 引擎测试（等价性比对、G1–G10 不变量、边界表、性能预算）。
- `tests/ProjectManager.Application.Tests` : 向导与持久化测试（项目清单、三步导航、生成即注册闭环）。
- `tests/fixtures` : 测试固件（不可随意改动的判定基线）。
- `tests/fixtures/tools` : 基线生成工具（`forge_baseline.py` 及其模板配置）。
- `tests/fixtures/forge-baseline` : 冻结的 Forge 等价基线（8 组快照，行为等价的唯一判定依据）。

### 资源、产物与文档

- `assets` : 资源层：全局设计资源与静态文件。
- `assets/design` : Figma、PSD 等设计源文件。
- `assets/icons` : 通用图标、Logo。
- `docs` : 文档层：设计方案、评审报告、融合方案、开发计划、各阶段交付说明。
- `output` : 产物层（已在 `.gitignore` 中排除）：统一存放编译打包后的最终文件。
- `scripts` : 脚本层：跨语言的自动化命令，如一键安装、一键启动、一键打包。

## 为什么没有 `apps/` `packages/` `contracts/` `infra/`

本产品是**单机桌面工具**：只有一个可执行程序、没有服务端、没有第二端消费方。原先的多端骨架（`apps/` 多端应用、`packages/` 跨端共享包、`contracts/` 跨端契约、`infra/` nginx/docker/database）四类目录全为空占位，没有任何消费方，属于纯维护税。按评审报告 P8 / 4.1 裁剪，规则如下：

| 原目录 | 处置 | 理由 |
|---|---|---|
| `apps/windows/` | 删除 | 单 App 不需要“多端”这一层；WPF 主程序落到 `src/ProjectManager.App` |
| `packages/*` | 删除 | 四个子包（ui-web / utils-js / config / shared-models）无消费方；真出现第二端再抽包 |
| `contracts/*` | 删除 | api / protobuf 均空；本产品无跨进程契约需求 |
| `infra/*` | 删除 | docker / nginx / database 均空；单机工具无部署面 |

**重新引入的门槛**：只有出现**第二端**（Web / 移动端）或**服务端组件**时才恢复对应目录，且恢复时必须同时有真实内容与非空消费方——不恢复空占位。此约定用于避免“骨架先行”再次发生。

> 与被裁剪目录同名的 `apps/`、`contracts/` 等路径仍会出现在 `src/ProjectManager.Scaffolding/Resources/templates.default.json` 中，那是**模板生成到用户目标目录**的内容（如「全栈项目」模板），与本仓库自身结构无关。
