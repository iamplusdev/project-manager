# S0 · Forge 等价基线用例说明（CASE-MATRIX）

> 用途：把「与 Forge 行为等价」从口头承诺变成**可复查资产**。G1 门禁的自动判定以本目录快照为准。
> 生成时间：2026-10-23 · 生成工具：`tests/fixtures/tools/forge_baseline.py`

---

## 1. 参考实现（不可变锚点）

Forge 仓库不是 git 仓库，无法用 commit 固定版本，因此以**源文件 SHA256** 作为参考实现指纹。任何一份指纹变化即视为「参考实现已变更」，必须重新评审基线。

| 文件 | SHA256 |
|---|---|
| `Project_Forge/src/generator.py` | `5d014ee83c0d66d64045a3af3f09ba029a128d7aea1f41e5950fba46ed9b5136` |
| `Project_Forge/src/templates.py` | `0db57127506ef47692419bf0ffbaa7099eee942b9dab5c1ab752c9038441eb33` |
| `Project_Forge/src/validation.py` | `b89c1d40747f9a8539b744f802e14f532ccbadc95afe1534ac21858d01c960b4` |

> 备注：`validation.py` 未参与快照生成（快照只跑 `generate()`），记录它是为了将来校验规则比对时有共同锚点。

**环境快照**

| 项 | 值 |
|---|---|
| OS | Windows 10/11（win32） |
| Python | 3.13.14（managed） |
| 生成日期 | 2026-10-23 |
| 模板配置 | `tests/fixtures/tools/templates.baseline.json`（3 套：全栈项目、轻量工具、单体桌面工具） |

**为什么这里要自带模板配置**：Forge 原生 `config/templates.json` 只有 2 套模板，而方案 9.1 要求覆盖第 3 套「单体桌面工具」。该模板由方案 5.1.1 定义，仅含 Forge 认识的字段（`structure`/`modules`/`descriptions`/`readme_quickstart`/`gitignore`），PM 专属字段（`files`/`naming`/`components`/`release`/`displayName`）**刻意不写入**——它们属于 PM 增量，见第 6 节。

---

## 2. 用例矩阵（8 组）

| # | 用例 ID | 模板 | 模块 | 目标目录初态 | 验证目标 |
|---|---|---|---|---|---|
| 1 | `case-01-fullstack-all` | 全栈项目 | 全勾选 | 空 | 完整结构 |
| 2 | `case-02-fullstack-none` | 全栈项目 | 全部不勾 | 空 | 退化 + `.gitkeep` |
| 3 | `case-03-light-tool` | 轻量工具 | 无模块 | 空 | 基线 |
| 4 | `case-04-light-tool-repeat` | 轻量工具 | 无模块 | 空 | 与 #3 参数相同，校验确定性 |
| 5 | `case-05-desktop-all` | 单体桌面工具 | 全勾选 | 空 | 新模板完整结构 |
| 6 | `case-06-desktop-main-only` | 单体桌面工具 | 仅 `desktop` | 空 | `.gitkeep` 退化路径 |
| 7 | `case-07-fullstack-rerun` | 全栈项目 | 全勾选 | **已有 README + .gitignore** | G1 幂等 / G2 保留 |
| 8 | `case-08-desktop-rerun` | 单体桌面工具 | 全勾选 | **已有 README** | G1 幂等（新模板） |

**预置内容（固定文本，不随机）**

- 用例 7 的 `README.md`：`# 既有项目\n\n> 手工创建的说明，用于验证幂等与备份\n`
- 用例 7 的 `.gitignore`：`# 用户既有规则\n*.tmp\n`
- 用例 8 的 `README.md`：`# 既有桌面项目\n\n> 预置 README，验证新模板的幂等与备份\n`
- 项目名统一为 `MyProject`；简介统一为常量 `S0 基线项目：仅用于 Forge 等价性比对`（描述若随用例变化，README 内容也会变，会破坏跨用例可比性）

**实测计数**

| 用例 | 目录 | 文件 | 占位 | 备份 | 保留 |
|---|---:|---:|---:|---:|---:|
| case-01 | 27 | 20 | 17 | 0 | 0 |
| case-02 | 19 | 17 | 14 | 0 | 0 |
| case-03 | 6 | 9 | 6 | 0 | 0 |
| case-04 | 6 | 9 | 6 | 0 | 0 |
| case-05 | 14 | 11 | 8 | 0 | 0 |
| case-06 | 11 | 11 | 8 | 0 | 0 |
| case-07 | 27 | 21 | 17 | 1 | 1 |
| case-08 | 14 | 12 | 8 | 1 | 0 |

---

## 3. 快照结构

```text
tests/fixtures/forge-baseline/
├── case-01-fullstack-all/
│   ├── tree.json          # 快照清单
│   └── files/             # 逐字内容（文本文件镜像）
├── ...
└── CASE-MATRIX.md         # 本文件
```

`tree.json`（按 `path` 排序）：

```json
{
  "project": "MyProject",
  "entries": [
    { "path": "apps", "type": "dir", "sha256": null, "isPlaceholder": false, "ignored": false },
    { "path": "apps/web/.gitkeep", "type": "file", "sha256": "e3b0c442...", "isPlaceholder": true, "ignored": false }
  ],
  "counts": { "dirs": 27, "files": 20, "placeholders": 17, "backups": 0, "preservedGitignore": false }
}
```

- `path`：相对项目根，统一 `/` 分隔；
- `sha256`：文件内容 hash（目录为 `null`）；空文件为 `e3b0c442...`（空串的 SHA256）；
- `isPlaceholder`：文件名是否为 `.gitkeep`；
- `ignored`：是否落在模板 `gitignore` 的目录前缀规则内（如 `output/`）。

**占位化规则（WP0.4）**：备份文件名 `.{yyyyMMdd-HHmmss}.bak` 统一改写为 `.TIMESTAMP.bak` 后入库并计 hash。
占位符不使用尖括号——Windows 文件名不允许 `<` `>`，早期用 `.<TIMESTAMP>.bak` 会触发 `PermissionError`。
若将来引入 `{{date}}` 替换，同样改写为 `.DATE` 后再计 hash（当前模板无该占位符，未触发）。

---

## 4. 复现步骤

```bash
# 依赖：Python 3.13+；Project_Forge 源码在并列目录
python tests/fixtures/tools/forge_baseline.py \
    --forge-src D:/MyProjects/01_Active_Projects/Project_Forge/src \
    --config tests/fixtures/tools/templates.baseline.json \
    --out tests/fixtures/forge-baseline
```

脚本行为：

1. 每组用例在**独立临时目录**生成（不污染仓库）；
2. 每组连跑两次，比较快照（`tree.json` + `files/`）必须逐字节一致；
3. 额外校验 `case-03` 与 `case-04` 跨用例一致；
4. 全部通过才写盘，任一不一致以非零退出码失败。

---

## 5. 确定性证据（WP0.5）

| 校验 | 结果 |
|---|---|
| 8 组各自连跑两次快照一致 | 全部 OK |
| `case-03` vs `case-04`（参数完全相同） | 一致 |
| 快照是否含时间/绝对路径 | 否（时间戳已占位化，路径全部相对） |

> 曾经出现过一次"跨用例不一致"，根因是 README 里写入了用例 ID 作为简介（**脚本缺陷，非引擎问题**），已改为常量文案后复测通过。该记录保留以示基线可信度是校验出来的，不是声称的。

---

## 6. 基线覆盖范围（重要）

**纳入比对（G1–G7 的 Forge 语义）**

- 结构展开与模块合并顺序
- 目录创建、`.gitkeep` 占位、忽略目录跳过
- `README.md` / `structure.md` / `.gitignore` 文本逐字内容
- 同名文件备份（`.bak`）与 `.gitignore` 保留
- 空叶子目录判定、`gitignore` 目录前缀匹配

**不纳入比对（PM 增量，须由 PM 自己的用例覆盖）**

| 增量 | 原因 | PM 覆盖方式 |
|---|---|---|
| `files` 内容注入（`.sln`/`.csproj` 等） | Forge 无此字段 | M1 专项用例：键内占位符、目标已存在跳过（G9） |
| `.pm-scaffold.json` | Forge 无元数据 | M1 专项用例：字段完整性（G10） |
| `.bak` 同秒加序号 | Forge 只有秒级时间戳，同秒重跑会覆盖备份 | M1 专项用例（G1 增强） |
| `.gitkeep` 已存在不重写 | Forge 用 `exists()` 跳过，语义已一致 | 保留为回归项（G3） |
| 回滚不可中断、阶段间隙取消 | Forge 无取消概念 | M1 专项用例（G8） |
| dry-run / 三档策略 | Forge 无预览 | M2 |

> 结论：**基线只证明"该等价的部分等价"**，不能用来证明 PM 增量正确。增量必须有自己的断言，避免「基线全绿」被误读为「全部正确」。

---

## 7. 人工复核记录（G0 第 4 条）

逐条对照方案 4.3 的 G1–G7 检查快照，确认没有把缺陷固化成"等价基线"：

| 项 | 复核方式 | 结论 |
|---|---|---|
| G1 备份 | 查 `case-07/files/README.md.TIMESTAMP.bak` 内容 = 预置文本 | 通过；但**同秒重跑会覆盖备份**（Forge 缺陷）→ 记为 PM 增强项，不继承 |
| G2 保留 `.gitignore` | 查 `case-07/files/.gitignore` 内容 = 用户预置内容，未被模板覆盖 | 通过 |
| G3 占位 | `case-02` 中 `output` 无 `.gitkeep`，其余空叶子均有 | 通过 |
| G4 预检 | 基线不含失败用例（失败无法产出快照）；预检语义由 PM 单测覆盖 | 需在 M1 单独覆盖 |
| G5 回滚 | 同上：`generate()` 抛错时无产物可快照 | 需在 M1 用文件系统桩覆盖 |
| G6 编码 | 快照 `files/` 全为 UTF-8 无 BOM；换行 LF | 通过 |
| G7 纯逻辑 | 生成过程未触碰 UI（`generator.py` 无 UI 依赖） | 通过 |

**发现的 Forge 缺陷（记录，不继承）**

1. **同秒重跑覆盖备份**：`stamp` 精确到秒，同一秒内二次生成会用同一 `.bak` 名覆盖首次备份 → PM 按 G1 加 `-1`/`-2` 序号。
2. **`structure.md` 只列有描述的目录**：`descriptions` 缺失的目录不会出现在说明小节（树里仍有）→ 保持等价，属设计选择而非缺陷，PM 不改。
3. **预检不检查"目标目录已存在"**：Forge 直接合并，无提示 → PM 按 `PM-SCF-004` 增加三档策略，属增量非等价项。

---

## 8. 变更控制

- 快照**只允许在"修正而非退让"时变更**，且需两人确认，并在本节追加记录（原因 / 确认人 / 日期）。
- 变更后必须重跑 M1 快照比对，差异需能解释为"预期变更"。
- Forge 归档前须确认：本目录已入库、`CASE-MATRIX.md` 的指纹未变、复现脚本可独立跑通。

**变更记录**

| 日期 | 变更 | 原因 | 确认 |
|---|---|---|---|
| 2026-10-23 | 初始冻结（8 组） | S0 首次基线 | 待复核人签署 |
