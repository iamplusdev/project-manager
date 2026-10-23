# MyProject 项目结构说明

```text
MyProject/
├── apps/
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
├── assets/
│   ├── design/
│   └── icons/
├── docs/
└── scripts/
```

## 📖 文件夹作用说明

- `apps` : 应用层：存放所有可独立运行和部署的程序（多端）。
- `assets` : 资源层：全局设计资源与静态文件。
- `assets/design` : Figma、PSD 等设计源文件。
- `assets/icons` : 多端通用的图标、Logo。
- `contracts` : 契约层：多端通信规范（如 Swagger YAML、Protobuf），确保各端接口一致。
- `contracts/api` : API 接口定义文件。
- `contracts/protobuf` : gRPC/Protobuf 协议文件。
- `docs` : 文档层：项目架构图、开发指南、踩坑记录等。
- `infra` : 基础设施层：部署、运维、数据库相关配置。
- `infra/database` : 数据库初始化 SQL、迁移脚本。
- `infra/docker` : 存放各服务的 Dockerfile。
- `infra/nginx` : 反向代理和静态资源服务器配置。
- `output` : 产物层 (加入 .gitignore)：统一存放各端编译打包后的最终文件。
- `packages` : 共享包层：跨端复用的代码、UI 组件和配置，避免重复造轮子。
- `packages/config` : 统一的 Lint 规则、代码格式化配置。
- `packages/shared-models` : 多端共用的数据模型/类型定义。
- `packages/ui-web` : Web 端共享的 UI 组件库。
- `packages/utils-js` : 通用的 JS/TS 工具函数。
- `scripts` : 脚本层：跨语言的自动化命令，如一键安装、一键启动、一键打包。
