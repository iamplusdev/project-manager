# Project_Manager 项目结构说明

```text
Project_Manager/
├── ProjectManager.sln
├── src/
│   └── ProjectManager.Scaffolding/
│       ├── IO/
│       ├── Models/
│       ├── Naming/
│       ├── Resources/
│       ├── Scaffolding/
│       ├── Templates/
│       └── Validation/
├── tests/
│   ├── ProjectManager.Scaffolding.Tests/
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
- `ProjectManager.sln` : 解决方案入口；当前含脚手架引擎与引擎测试两个项目。M2 起追加 `src/ProjectManager.App`（WPF 表现层）。
- `src` : 可编译源码根目录；**所有 .NET 项目都放在这里**。
- `src/ProjectManager.Scaffolding` : 脚手架生成引擎（零 UI / 零存储依赖，可无界面单测）。
- `src/ProjectManager.Scaffolding/IO` : 文件系统与时钟抽象，支撑故障注入与确定性测试。
- `src/ProjectManager.Scaffolding/Models` : 模板、模块、生成请求/结果、错误码等领域模型。
- `src/ProjectManager.Scaffolding/Naming` : 命名派生（pascal / kebab / snake），规则在此冻结。
- `src/ProjectManager.Scaffolding/Resources` : 内置模板配置（`templates.default.json`，随程序集嵌入）。
- `src/ProjectManager.Scaffolding/Scaffolding` : 树展开、文档产物、生成主流程、回滚日志、元数据。
- `src/ProjectManager.Scaffolding/Templates` : 模板加载与校验（三源优先级：用户目录 > 安装目录 > 内置）。
- `src/ProjectManager.Scaffolding/Validation` : 表单校验（名称 / 简介 / 生成路径）。
- `tests` : 测试与固化资产。
- `tests/ProjectManager.Scaffolding.Tests` : 引擎测试（等价性比对、G1–G10 不变量、边界表、性能预算）。
- `tests/fixtures` : 测试固件（不可随意改动的判定基线）。
- `tests/fixtures/tools` : 基线生成工具（`forge_baseline.py` 及其模板配置）。
- `tests/fixtures/forge-baseline` : 冻结的 Forge 等价基线（8 组快照，行为等价的唯一判定依据）。
- `assets` : 资源层：全局设计资源与静态文件。
- `assets/design` : Figma、PSD 等设计源文件。
- `assets/icons` : 通用图标、Logo。
- `docs` : 文档层：设计方案、评审报告、融合方案、开发计划等。
- `output` : 产物层（已在 `.gitignore` 中排除）：统一存放编译打包后的最终文件，按端再分子目录。
- `scripts` : 脚本层：跨语言的自动化命令，如一键安装、一键启动、一键打包。

## 为什么没有 `apps/` `packages/` `contracts/` `infra/`

本产品是**单机桌面工具**：只有一个可执行程序、没有服务端、没有第二端消费方。原先的多端骨架（`apps/` 多端应用、`packages/` 跨端共享包、`contracts/` 跨端契约、`infra/` nginx/docker/database）四类目录全为空占位，没有任何消费方，属于纯维护税。按评审报告 P8 / 4.1 裁剪，规则如下：

| 原目录 | 处置 | 理由 |
|---|---|---|
| `apps/windows/` | 删除 | 单 App 不需要“多端”这一层；WPF 主程序直接落到 `src/ProjectManager.App` |
| `packages/*` | 删除 | 四个子包（ui-web / utils-js / config / shared-models）无消费方；真出现第二端再抽包 |
| `contracts/*` | 删除 | api / protobuf 均空；本产品无跨进程契约需求 |
| `infra/*` | 删除 | docker / nginx / database 均空；单机工具无部署面 |

**重新引入的门槛**：只有出现**第二端**（Web / 移动端）或**服务端组件**时才恢复对应目录，且恢复时必须同时有真实内容与非空消费方——不恢复空占位。此约定用于避免“骨架先行”再次发生。

> 与被裁剪目录同名的 `apps/`、`contracts/` 等路径仍会出现在 `src/ProjectManager.Scaffolding/Resources/templates.default.json` 中，那是**模板生成到用户目标目录**的内容（如「全栈项目」模板），与本仓库自身结构无关。
