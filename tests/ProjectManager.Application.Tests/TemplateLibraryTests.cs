using System.Text;
using ProjectManager.Application.Templates;

namespace ProjectManager.Application.Tests;

/// <summary>
/// 模板导入（WP4.1）。重点不是"能存下来"，而是**坏的配置绝不能覆盖好的**——
/// 导入失败却把旧文件写花了，用户就失去了回退能力。
/// </summary>
public static class TemplateLibraryTests
{
    private const string TwoTemplates = """
        {
          "单体桌面工具": {
            "structure": { "src": ["App"], "tests": ["Unit"] },
            "descriptions": { "src": "源码", "tests": "测试" },
            "readme_quickstart": "dotnet run",
            "modules": [{ "key": "desktop", "label": "桌面壳", "paths": ["src"] }]
          },
          "轻量工具": {
            "structure": { "src": [] },
            "descriptions": { "src": "源码" },
            "readme_quickstart": "dotnet run"
          }
        }
        """;

    public static void Run()
    {
        Check.Section("Templates · 导入与校验");
        ImportsValidConfig();
        BacksUpBeforeOverwrite();
        RejectsInvalidWithoutTouchingExisting();
        TreatsSelfAsInspectionOnly();
        ReportsUnreadableFile();
        DisablesUserConfig();
        KeepsWarningsOutOfErrors();
        InspectionDoesNotWrite();
        ListsTemplatesInOrder();
        BackupsDoNotCollide();
        IssueTextCarriesLocation();
    }

    private static void ImportsValidConfig()
    {
        using var workspace = new TempWorkspace("tpl-import");
        var source = workspace.Path2("incoming.json");
        File.WriteAllText(source, TwoTemplates, new UTF8Encoding(false));

        var library = Library(workspace);
        var outcome = library.Import(source);

        Check.That(outcome.Succeeded, "合法配置应导入成功");
        Check.Equal(2, outcome.Inspection.Entries.Count, "应识别出两个模板");
        Check.That(outcome.BackupPath is null, "首次导入没有旧文件，不该产生备份");
        Check.That(File.Exists(library.UserPath), "导入后用户配置应存在");
        Check.Equal(
            File.ReadAllText(source),
            File.ReadAllText(library.UserPath),
            "写入内容应与源文件一致（不做任何改写）");
    }

    private static void BacksUpBeforeOverwrite()
    {
        using var workspace = new TempWorkspace("tpl-backup");
        var library = Library(workspace, initial: """{ "旧模板": { "structure": { "src": [] }, "descriptions": { "src": "x" }, "readme_quickstart": "run" } }""");
        var before = File.ReadAllText(library.UserPath);

        var source = workspace.Path2("incoming.json");
        File.WriteAllText(source, TwoTemplates, new UTF8Encoding(false));

        var outcome = library.Import(source);

        Check.That(outcome.Succeeded, "覆盖导入应成功");
        Check.That(outcome.BackupPath is not null, "覆盖前必须备份");
        Check.That(File.Exists(outcome.BackupPath!), "备份文件应真的落盘");
        Check.Equal(before, File.ReadAllText(outcome.BackupPath!), "备份内容应是旧配置原文");
        Check.That(
            File.ReadAllText(library.UserPath).Contains("单体桌面工具", StringComparison.Ordinal),
            "用户配置应已换成新内容");
    }

    private static void RejectsInvalidWithoutTouchingExisting()
    {
        using var workspace = new TempWorkspace("tpl-reject");
        const string Good = """{ "可用模板": { "structure": { "src": [] }, "descriptions": { "src": "x" }, "readme_quickstart": "run" } }""";
        var library = Library(workspace, initial: Good);

        var source = workspace.Path2("broken.json");
        File.WriteAllText(
            source,
            """{ "坏模板": { "structure": { "src": [] }, "descriptions": { "src": "x" } } }""",
            new UTF8Encoding(false));

        var outcome = library.Import(source);

        Check.That(!outcome.Succeeded, "缺 readme_quickstart 应被拒绝");
        Check.That(outcome.BackupPath is null, "拒绝时不该产生备份（没覆盖就没有备份可言）");
        Check.Equal(Good, File.ReadAllText(library.UserPath), "失败时现有配置必须一个字节都不变");

        var error = outcome.Inspection.Errors[0];
        Check.Equal("PM-SCF-010", error.Code, "错误码");
        Check.Equal("坏模板", error.Template, "应定位到模板名");
        Check.Equal("readme_quickstart", error.Field, "应定位到字段");
        Check.Equal(0, outcome.Inspection.Entries.Count, "非法配置不该给出模板条目");
    }

    private static void TreatsSelfAsInspectionOnly()
    {
        using var workspace = new TempWorkspace("tpl-self");
        const string Good = """{ "可用模板": { "structure": { "src": [] }, "descriptions": { "src": "x" }, "readme_quickstart": "run" } }""";
        var library = Library(workspace, initial: Good);
        var stamp = File.GetLastWriteTimeUtc(library.UserPath);

        var outcome = library.Import(library.UserPath);

        Check.That(outcome.Succeeded, "选中自身应视为检查并通过");
        Check.That(outcome.BackupPath is null, "不该把自己备份一遍再覆盖自己");
        Check.Equal(stamp, File.GetLastWriteTimeUtc(library.UserPath), "文件不应被重写");
    }

    private static void ReportsUnreadableFile()
    {
        using var workspace = new TempWorkspace("tpl-missing");
        var library = Library(workspace);

        var outcome = library.Import(workspace.Path2("不存在.json"));

        Check.That(!outcome.Succeeded, "文件不存在应失败而不是抛异常");
        Check.Equal(1, outcome.Inspection.Errors.Count, "应给出一条可直接展示的说明");
        Check.That(
            outcome.Inspection.Errors[0].Message.Contains("无法读取", StringComparison.Ordinal),
            "文案要说清是读不出来，而不是模板写错了");
        Check.That(!File.Exists(library.UserPath), "失败时不该创建用户配置");
    }

    private static void DisablesUserConfig()
    {
        using var workspace = new TempWorkspace("tpl-disable");
        const string Good = """{ "可用模板": { "structure": { "src": [] }, "descriptions": { "src": "x" }, "readme_quickstart": "run" } }""";
        var library = Library(workspace, initial: Good);

        Check.That(library.HasUserConfig, "前置：应存在用户配置");

        var backup = library.DisableUserConfig();

        Check.That(backup is not null, "停用应返回备份路径");
        Check.That(!File.Exists(library.UserPath), "停用后用户配置应消失，让加载器回退");
        Check.That(File.Exists(backup!), "原文件应改名为备份而保留下来");
        Check.Equal(Good, File.ReadAllText(backup!), "备份内容应是原文");
        Check.That(!library.HasUserConfig, "HasUserConfig 应随之变假");

        Check.That(library.DisableUserConfig() is null, "没有用户配置时停用应返回 null 而不是抛错");
    }

    private static void KeepsWarningsOutOfErrors()
    {
        using var workspace = new TempWorkspace("tpl-warn");
        var source = workspace.Path2("warn.json");
        File.WriteAllText(
            source,
            """{ "T": { "structure": { "src": [] }, "descriptions": { "src": "x" }, "readme_quickstart": "run", "strcuture": {} } }""",
            new UTF8Encoding(false));

        var inspection = Library(workspace).Inspect(source);

        Check.That(inspection.IsValid, "未知字段只是警告，不该判为非法");
        Check.Equal(0, inspection.Errors.Count, "未知字段不该进 Errors");
        Check.Equal(1, inspection.Warnings.Count, "未知字段应进 Warnings");
        Check.That(
            inspection.Warnings[0].Message.Contains("strcuture", StringComparison.Ordinal),
            "警告要回显拼错的键名");
        Check.That(inspection.Warnings[0].IsWarning, "警告条目应自带标记，视图据此上色");
    }

    private static void InspectionDoesNotWrite()
    {
        using var workspace = new TempWorkspace("tpl-readonly");
        var source = workspace.Path2("incoming.json");
        File.WriteAllText(source, TwoTemplates, new UTF8Encoding(false));

        var library = Library(workspace);
        var inspection = library.Inspect(source);

        Check.That(inspection.IsValid, "检查应通过");
        Check.Equal(64, inspection.Hash.Length, "指纹应是 SHA256 十六进制");
        Check.That(!File.Exists(library.UserPath), "Inspect 绝不能碰用户配置");
    }

    private static void ListsTemplatesInOrder()
    {
        using var workspace = new TempWorkspace("tpl-order");
        var source = workspace.Path2("incoming.json");
        File.WriteAllText(source, TwoTemplates, new UTF8Encoding(false));

        var inspection = Library(workspace).Inspect(source);

        Check.Equal("单体桌面工具", inspection.Entries[0].Name, "顺序应按名称序稳定（第一条）");
        Check.Equal("轻量工具", inspection.Entries[1].Name, "顺序应按名称序稳定（第二条）");
        Check.Equal(4, inspection.Entries[0].Directories, "单体桌面工具：src/App + tests/Unit 共 4 个目录");
        Check.Equal(1, inspection.Entries[0].Modules, "单体桌面工具声明了 1 个模块");
        Check.Equal(1, inspection.Entries[1].Directories, "轻量工具只有 src");
    }

    private static void BackupsDoNotCollide()
    {
        using var workspace = new TempWorkspace("tpl-collide");
        // 时钟固定：三次备份落在同一毫秒，必须靠序号后缀让开（真实时钟下几乎撞不上，测不出来）
        var library = Library(
            workspace,
            initial: """{ "A": { "structure": { "src": [] }, "descriptions": { "src": "x" }, "readme_quickstart": "run" } }""",
            frozenClock: true);
        var source = workspace.Path2("incoming.json");
        File.WriteAllText(source, TwoTemplates, new UTF8Encoding(false));

        var backups = new List<string>();
        for (var round = 0; round < 3; round++)
        {
            var outcome = library.Import(source);
            Check.That(outcome.Succeeded, $"第 {round + 1} 次导入应成功");
            if (outcome.BackupPath is not null)
            {
                backups.Add(outcome.BackupPath);
            }
        }

        Check.Equal(3, backups.Count, "每轮都应产生备份（首轮之前已有旧配置）");
        Check.Equal(3, backups.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "同一毫秒的三个备份必须互不相同");
        Check.That(backups.All(File.Exists), "每个备份都应真的落盘");

        if (backups.Count == 3)
        {
            Check.That(
                backups[1].EndsWith("-1.bak", StringComparison.OrdinalIgnoreCase)
                && backups[2].EndsWith("-2.bak", StringComparison.OrdinalIgnoreCase),
                $"撞名后应按序号递增（实际：{string.Join(" | ", backups.Select(Path.GetFileName))}）");
        }
    }

    private static void IssueTextCarriesLocation()
    {
        var withField = new TemplateIssue("PM-SCF-010", "单体桌面工具", "modules.app", "缺少 paths。", IsWarning: false);
        Check.Equal(
            "[PM-SCF-010] 模板「单体桌面工具」· modules.app：缺少 paths。",
            withField.Describe(),
            "字段级问题的定位串");

        var configLevel = new TemplateIssue("PM-SCF-010", null, null, "JSON 语法错误", IsWarning: false);
        Check.Equal(
            "[PM-SCF-010] 整份配置：JSON 语法错误",
            configLevel.Describe(),
            "配置级问题不该编造模板名");

        // 引擎文案本身已带「模板「T」的 structure」，再拼定位串会出现两遍
        var fromEngine = new TemplateIssue(
            "PM-SCF-010", "T", "structure", "模板「T」的 structure 应是非空对象。", IsWarning: false);
        Check.Equal(
            "[PM-SCF-010] 模板「T」的 structure 应是非空对象。",
            fromEngine.Describe(),
            "引擎文案已自带定位时不应重复拼接");
    }

    private static TemplateLibraryService Library(TempWorkspace workspace, string? initial = null, bool frozenClock = false)
    {
        var userPath = workspace.Path2("user", "templates.json");
        var installPath = workspace.Path2("install", "templates.json");

        if (initial is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(userPath)!);
            File.WriteAllText(userPath, initial, new UTF8Encoding(false));
        }

        var clock = frozenClock
            ? (Func<DateTime>)(() => new DateTime(2026, 10, 23, 23, 0, 0, 123))
            : null;

        return new TemplateLibraryService(
            userPath,
            new Scaffolding.Templates.TemplateLoader(userPath, installPath),
            clock);
    }
}
