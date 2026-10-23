# Project_Manager

> 一个项目管理器，管理所有项目的版本，维护不同项目的git身份，不同项目代码推送，打包，版本发布，初始化项目结构

## 📂 目录结构

详情请参阅：[structure.md](./structure.md)

## 🚀 快速开始

### 环境要求

- .NET 8 SDK（当前开发验证版本 8.0.426）
- 无需联网：依赖全部来自 SDK 内置，无 NuGet 包引用

### 构建与测试

```bash
dotnet build                                              # 应零警告（TreatWarningsAsErrors）
dotnet run --project tests/ProjectManager.Scaffolding.Tests   # 引擎测试，退出码即结果
```

### 当前进度

| 阶段 | 内容 | 状态 |
|---|---|---|
| S0 | Forge 等价基线冻结（8 组快照） | ✅ 已完成 |
| M1 | 生成引擎（`ProjectManager.Scaffolding`） | ✅ 已完成 |
| M2 | 新建项目向导 + 项目注册 | 待开始 |
| M3 | 领域打通（组件识别 / `git init`） | 待开始 |

## 📚 文档索引

- [软件设计方案](./docs/软件设计方案.md) — 产品定义、架构、领域模型、UI 规格
- [设计方案评审报告](./docs/2026-10-23-设计方案评审报告.md) — 四维评审与问题清单
- [Forge 融合方案与 UI 设计](./docs/Forge融合方案与UI设计.md) — 脚手架能力的整合方案与界面规格
- [Forge 融合开发计划](./docs/Forge融合开发计划.md) — 阶段制计划与门禁
- [M1 引擎交付说明](./docs/Forge融合_M1引擎交付说明.md) — 引擎实现、等价性证据与偏差记录
- [仓库与远程约定](./REPOS.md) — GitHub 为唯一推送源
