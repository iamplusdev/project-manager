# 仓库地址

| **托管平台** |       仓库名       |  用途  |  分支  |                           地址                           |        备注       |
| :------: | :-------------: | :--: | :--: | :----------------------------------------------------: | :-------------: |
|  Github  | project-manager | 项目管理 | main |   <https://github.com/iamplusdev/project-manager.git>  |    只推送Github    |
|   Gitea  | project-manager | 项目管理 | main | <https://gitea.iamplus.cn/iamplus/project-manager.git> | Gitea定时镜像Github |

## 本地远程配置约定

- **只配置 `origin`**，指向 GitHub（SSH：`git@github:iamplusdev/project-manager.git`），分支 `main`。
- **不配置 Gitea 远程**：Gitea 由服务端定时镜像 GitHub，本地无需 `git remote add`，也不向它推送。
- **不使用 Gitee**：Gitee 的 SSH 主机别名（`Host gitee`）已从 `~/.ssh/config` 移除。

首次推送：`git push -u origin main`。之后仅 `git push` 即可。

