#!/usr/bin/env python3
"""S0 · 等价基线冻结（WP0.2–WP0.5）

调用 Project_Forge 的参考实现跑完 8 组用例，把产物固化为可复查的快照：

    tests/fixtures/forge-baseline/
    ├── case-01-fullstack-all/
    │   ├── tree.json      # [{ path, type, sha256, isPlaceholder, ignored }]
    │   └── files/         # 逐字内容（文本）
    └── ...

设计约束：
- 只调用 Forge 的 generate()，不复制其逻辑，确保基线是"参考实现的事实"。
- 易变内容占位化：`.{yyyyMMdd-HHmmss}.bak` 统一改写为 `.TIMESTAMP.bak` 后再计 hash。
- 同用例连跑两次，快照必须逐字节一致（确定性，WP0.5）。

用法：
    python tests/fixtures/tools/forge_baseline.py \
        --forge-src D:/MyProjects/01_Active_Projects/Project_Forge/src \
        --out tests/fixtures/forge-baseline
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import sys
import tempfile
from pathlib import Path

TIMESTAMP_RE = re.compile(r"\.\d{8}-\d{6}\.bak$")
PLACEHOLDER_NAME = ".gitkeep"
PROJECT_NAME = "MyProject"
# 描述必须是常量：否则 README 会随用例 ID 变化，破坏跨用例可比性
BASELINE_DESCRIPTION = "S0 基线项目：仅用于 Forge 等价性比对"

# 8 组用例（方案 9.1）
CASES: list[dict] = [
    {
        "id": "case-01-fullstack-all",
        "template": "全栈项目",
        "modules": "all",
        "seed": None,
        "purpose": "全栈项目 + 全模块，空目录：完整结构",
    },
    {
        "id": "case-02-fullstack-none",
        "template": "全栈项目",
        "modules": "none",
        "seed": None,
        "purpose": "全栈项目 + 全部不勾，空目录：退化路径与 .gitkeep",
    },
    {
        "id": "case-03-light-tool",
        "template": "轻量工具",
        "modules": "none",
        "seed": None,
        "purpose": "轻量工具（无模块），空目录：基线",
    },
    {
        "id": "case-04-light-tool-repeat",
        "template": "轻量工具",
        "modules": "none",
        "seed": None,
        "purpose": "与 case-03 参数完全相同：确认产物确定性",
    },
    {
        "id": "case-05-desktop-all",
        "template": "单体桌面工具",
        "modules": "all",
        "seed": None,
        "purpose": "单体桌面工具 + 全模块，空目录：新模板完整结构",
    },
    {
        "id": "case-06-desktop-main-only",
        "template": "单体桌面工具",
        "modules": ["desktop"],
        "seed": None,
        "purpose": "单体桌面工具 + 仅主模块：.gitkeep 退化路径",
    },
    {
        "id": "case-07-fullstack-rerun",
        "template": "全栈项目",
        "modules": "all",
        "seed": {
            "README.md": "# 既有项目\n\n> 手工创建的说明，用于验证幂等与备份\n",
            ".gitignore": "# 用户既有规则\n*.tmp\n",
        },
        "purpose": "全栈项目 + 全模块，预置 README 与 .gitignore：G1 幂等 / G2 保留",
    },
    {
        "id": "case-08-desktop-rerun",
        "template": "单体桌面工具",
        "modules": "all",
        "seed": {
            "README.md": "# 既有桌面项目\n\n> 预置 README，验证新模板的幂等与备份\n",
        },
        "purpose": "单体桌面工具 + 全模块，预置 README：G1 幂等（新模板）",
    },
]


def die(message: str) -> None:
    print(f"[FAIL] {message}", file=sys.stderr)
    raise SystemExit(1)


def load_forge(forge_src: Path):
    if not (forge_src / "generator.py").is_file():
        die(f"Forge 源码目录无效（缺少 generator.py）：{forge_src}")
    sys.path.insert(0, str(forge_src))
    import generator  # type: ignore
    import templates  # type: ignore

    return generator, templates


def sha256_text(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def normalize_rel(rel: str) -> str:
    # 注意：占位符不能含 <>（Windows 非法文件名字符），故用 .TIMESTAMP.bak
    return TIMESTAMP_RE.sub(".TIMESTAMP.bak", rel.replace("\\", "/"))


def collect_snapshot(base: Path, template, generator) -> dict:
    """把生成结果读成可比较的快照（不依赖时间戳与绝对路径）。"""
    prefixes = generator._ignored_directory_prefixes(template.gitignore)
    entries: list[dict] = []
    file_payloads: dict[str, bytes] = {}

    for root, dirs, files in os.walk(base):
        root_path = Path(root)
        for name in dirs:
            rel = (root_path / name).relative_to(base).as_posix()
            entries.append(
                {
                    "path": normalize_rel(rel),
                    "type": "dir",
                    "sha256": None,
                    "isPlaceholder": False,
                    "ignored": bool(generator._matches_ignored_directory(rel, prefixes)),
                }
            )
        for name in files:
            abs_path = root_path / name
            rel = abs_path.relative_to(base).as_posix()
            raw = abs_path.read_bytes()
            try:
                text = raw.decode("utf-8")
            except UnicodeDecodeError:
                text = None
            normalized_rel = normalize_rel(rel)
            if text is None:
                entries.append(
                    {
                        "path": normalized_rel,
                        "type": "file",
                        "sha256": hashlib.sha256(raw).hexdigest(),
                        "isPlaceholder": name == PLACEHOLDER_NAME,
                        "ignored": bool(
                            generator._matches_ignored_directory(
                                str(Path(rel).parent).replace("\\", "/"), prefixes
                            )
                        ),
                    }
                )
                file_payloads[normalized_rel] = raw
            else:
                payload = text.encode("utf-8")
                entries.append(
                    {
                        "path": normalized_rel,
                        "type": "file",
                        "sha256": sha256_text(text),
                        "isPlaceholder": name == PLACEHOLDER_NAME,
                        "ignored": bool(
                            generator._matches_ignored_directory(
                                str(Path(rel).parent).replace("\\", "/"), prefixes
                            )
                        ),
                    }
                )
                file_payloads[normalized_rel] = payload

    entries.sort(key=lambda item: (item["path"], item["type"]))
    counts = {
        "dirs": sum(1 for item in entries if item["type"] == "dir"),
        "files": sum(1 for item in entries if item["type"] == "file"),
        "placeholders": sum(1 for item in entries if item["isPlaceholder"]),
        "backups": sum(1 for item in entries if item["path"].endswith(".TIMESTAMP.bak")),
        "preservedGitignore": any(item["path"] == ".gitignore" and item["sha256"] == sha256_text("# 用户既有规则\n*.tmp\n") for item in entries),
    }
    return {
        "project": PROJECT_NAME,
        "entries": entries,
        "counts": counts,
        "_payloads": file_payloads,
    }


def run_case(case: dict, template, generator, workdir: Path) -> dict:
    """在独立临时目录跑一次生成，返回快照。"""
    base_target = workdir / case["id"]
    base_target.mkdir(parents=True)
    project_dir = base_target / PROJECT_NAME
    seed = case["seed"] or {}
    if seed:
        project_dir.mkdir()
        for rel, content in seed.items():
            target = project_dir / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(content, encoding="utf-8", newline="\n")

    if case["modules"] == "all":
        selected = tuple(module.key for module in template.modules)
    elif isinstance(case["modules"], list):
        selected = tuple(module.key for module in template.modules if module.key in case["modules"])
    else:
        selected = ()
    request = generator.GenerationRequest(
        project_name=PROJECT_NAME,
        description=BASELINE_DESCRIPTION,
        target_dir=base_target,
        selected_modules=selected,
    )
    try:
        result = generator.generate(request, template)
    except generator.GenerationError as exc:
        die(f"用例 {case['id']} 生成失败：{exc}")

    snapshot = collect_snapshot(project_dir, template, generator)
    snapshot["reported"] = {
        "dirsCreated": len(result.dirs_created),
        "filesWritten": len(result.files_written),
        "backups": len(result.backups),
        "preserved": list(result.preserved),
        "placeholders": len(result.placeholders),
    }
    return snapshot


def strip_payloads(snapshot: dict) -> dict:
    return {key: value for key, value in snapshot.items() if key != "_payloads"}


def write_snapshot(snapshot: dict, case_id: str, out_root: Path) -> None:
    case_dir = out_root / case_id
    if case_dir.exists():
        shutil.rmtree(case_dir)
    (case_dir / "files").mkdir(parents=True)

    payload = strip_payloads(snapshot)
    payload.pop("reported", None)
    tree_json = json.dumps(payload, ensure_ascii=False, indent=2) + "\n"
    (case_dir / "tree.json").write_text(tree_json, encoding="utf-8", newline="\n")

    for rel, data in snapshot["_payloads"].items():
        target = case_dir / "files" / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)


def main() -> None:
    parser = argparse.ArgumentParser(description="冻结 Forge 等价基线")
    parser.add_argument(
        "--forge-src",
        default=os.environ.get("FORGE_SRC", r"D:/MyProjects/01_Active_Projects/Project_Forge/src"),
        help="Project_Forge 的 src 目录",
    )
    parser.add_argument("--config", default=str(Path(__file__).with_name("templates.baseline.json")))
    parser.add_argument("--out", default="tests/fixtures/forge-baseline")
    args = parser.parse_args()

    forge_src = Path(args.forge_src)
    config_path = Path(args.config)
    out_root = Path(args.out)

    generator, templates = load_forge(forge_src)
    all_templates = templates.load_templates(config_path)
    print(f"Forge 参考实现：{forge_src}")
    print(f"模板配置：{config_path}（{len(all_templates)} 套：{'、'.join(all_templates)}）")

    out_root.mkdir(parents=True, exist_ok=True)
    rows: list[tuple[str, dict, bool]] = []

    for case in CASES:
        template = all_templates.get(case["template"])
        if template is None:
            die(f"用例 {case['id']} 引用了不存在的模板：{case['template']}")

        with tempfile.TemporaryDirectory(prefix="forge-s0-") as tmp:
            first = run_case(case, template, generator, Path(tmp) / "run1")
            second = run_case(case, template, generator, Path(tmp) / "run2")
            deterministic = strip_payloads(first) == strip_payloads(second) and first["_payloads"] == second["_payloads"]

        write_snapshot(first, case["id"], out_root)
        rows.append((case["id"], first, deterministic))

    # 跨用例确定性：case-03 与 case-04 参数完全相同，快照应逐字一致
    by_id = {case_id: snap for case_id, snap, _ in rows}
    cross_ok = strip_payloads(by_id["case-03-light-tool"]) == strip_payloads(by_id["case-04-light-tool-repeat"])

    print("\n用例结果")
    print(f"{'用例':<28}{'目录':>5}{'文件':>5}{'占位':>5}{'备份':>5}{'保留':>5}  确定性")
    for case_id, snapshot, deterministic in rows:
        counts = snapshot["counts"]
        flag = "OK" if deterministic else "不稳定"
        print(
            f"{case_id:<28}{counts['dirs']:>5}{counts['files']:>5}"
            f"{counts['placeholders']:>5}{counts['backups']:>5}"
            f"{len(snapshot['reported']['preserved']):>5}  {flag}"
        )
    print(f"\n跨用例一致（case-03 vs case-04）：{'OK' if cross_ok else '不一致'}")

    failures = [case_id for case_id, _, ok in rows if not ok]
    if failures:
        die(f"以下用例两次运行快照不一致：{', '.join(failures)}")
    if not cross_ok:
        die("case-03 与 case-04 快照不一致，产物存在不确定性")

    print(f"\n快照已写入：{out_root.resolve()}")


if __name__ == "__main__":
    main()
