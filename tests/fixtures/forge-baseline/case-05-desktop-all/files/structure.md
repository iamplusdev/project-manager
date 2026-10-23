# MyProject 项目结构说明

```text
MyProject/
├── src/
│   ├── App/
│   └── Core/
├── tests/
│   └── UnitTests/
├── docs/
├── scripts/
│   └── package/
├── config/
├── assets/
│   ├── icons/
│   └── images/
└── output/
    └── installer/
```

## 📖 文件夹作用说明

- `assets` : 静态资源根目录
- `assets/icons` : 应用与托盘图标
- `assets/images` : 界面截图与插图
- `config` : 运行期配置（JSON，可热覆盖）
- `docs` : 设计文档、方案与评审记录
- `output` : 构建产物（已忽略，不占位）
- `output/installer` : 安装包输出（已忽略，不占位）
- `scripts` : 构建、打包、发布脚本
- `scripts/package` : 安装包与更新包构建脚本
- `src` : 源码根目录，按层拆分
- `src/App` : 表现层（WPF 窗口/视图/视图模型）
- `src/Core` : 领域与纯逻辑，可无界面单测
- `tests` : 测试工程根目录
- `tests/UnitTests` : 单元测试用例
