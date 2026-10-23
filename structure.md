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
├── apps/
│   └── windows/
├── contracts/
│   ├── api/
│   └── protobuf/
├── packages/
│   ├── ui-web/
│   ├── utils-js/
│   ├── config/
│   └── shared-models/
├── infra/
│   ├── docker/
│   ├── nginx/
│   └── database/
├── output/
│   └── windows-exe/
├── assets/
│   ├── design/
│   └── icons/
├── docs/
└── scripts/
```

## 📖 文件夹作用说明

- `.gitattributes` : 行尾策略（`* text=auto`），避免跨平台 diff 噪音。
- `.gitignore` : 排除 `output/`、`.workbuddy/`、`bin/`、`obj/`、`.vs/` 等。
- `ProjectManager.sln` : 解决方案入口；当前含脚手架引擎与引擎测试两个项目。
- `src` : 可编译源码根目录。
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
- `apps` : 应用层：存放所有可独立运行和部署的程序（多端）。
- `apps/windows` : Windows 客户端（WPF 主程序，M2 起落地）。
- `assets` : 资源层：全局设计资源与静态文件。
- `assets/design` : Figma、PSD 等设计源文件。
- `assets/icons` : 多端通用的图标、Logo。
- `contracts` : 契约层：多端通信规范（如 Swagger YAML、Protobuf），确保各端接口一致。
- `contracts/api` : API 接口定义文件。
- `contracts/protobuf` : gRPC/Protobuf 协议文件。
- `docs` : 文档层：设计方案、评审报告、融合方案、开发计划等。
- `infra` : 基础设施层：部署、运维、数据库相关配置。
- `infra/database` : 数据库初始化 SQL、迁移脚本。
- `infra/docker` : 存放各服务的 Dockerfile。
- `infra/nginx` : 反向代理和静态资源服务器配置。
- `output` : 产物层 (加入 .gitignore)：统一存放各端编译打包后的最终文件。
- `output/windows-exe` : Windows 可执行文件。
- `packages` : 共享包层：跨端复用的代码、UI 组件和配置，避免重复造轮子。
- `packages/config` : 统一的 Lint 规则、代码格式化配置。
- `packages/shared-models` : 多端共用的数据模型/类型定义。
- `packages/ui-web` : Web 端共享的 UI 组件库。
- `packages/utils-js` : 通用的 JS/TS 工具函数。
- `scripts` : 脚本层：跨语言的自动化命令，如一键安装、一键启动、一键打包。
