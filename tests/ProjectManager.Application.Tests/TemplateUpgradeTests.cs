using ProjectManager.Application.Projects;
using ProjectManager.Scaffolding.Models;
using ProjectManager.Scaffolding.Scaffolding;

namespace ProjectManager.Application.Tests;

/// <summary>模板升级检测与"只加不删"补结构：指纹比对、模块沿用、幂等、不覆盖既有文件。</summary>
public static class TemplateUpgradeTests
{
    private const string TemplateName = "单体桌面工具";
    private const string OldHash = "old-hash";
    private const string NewHash = "new-hash";

    public static void Run()
    {
        Check.Section("Upgrade · 模板升级检测");
        UpToDateWhenHashMatches();
        OutdatedListsMissingDirectoriesOnly();
        NotManagedWhenMetadataMissing();
        TemplateMissingWhenTemplateGone();
        ApplyCreatesStructureAndUpdatesHash();
        ApplyNeverOverwritesExistingFiles();
    }

    private static Template NewTemplate()
    {
        var root = new DirNode("root");
        TreeBuilder.Insert(root, "src");
        TreeBuilder.Insert(root, "docs");
        TreeBuilder.Insert(root, "output");

        var modules = new[]
        {
            new ModuleOption("desktop", "桌面应用主体", new[] { "src/App" }),
            new ModuleOption("installer", "安装包与更新", new[] { "output/installer" }),
        };

        return new Template(
            TemplateName,
            root,
            modules,
            new Dictionary<string, string>(),
            string.Empty,
            new[] { "output/" })
        {
            ConfigHash = NewHash,
            SchemaVersion = 2,
        };
    }

    /// <summary>造一个"由旧模板生成"的项目目录：只有 src，附带旧指纹。</summary>
    private static string NewProject(TempWorkspace workspace, string moduloHash = OldHash, string modules = "[\"desktop\"]")
    {
        var root = workspace.Path2("proj");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(
            Path.Combine(root, ScaffoldMetadata.FileName),
            $$"""
            {
              "engine": "ProjectManager.Scaffolding",
              "template": "{{TemplateName}}",
              "templateHash": "{{moduloHash}}",
              "schemaVersion": 1,
              "projectName": "Demo",
              "modules": {{modules}},
              "generatedAt": "2026-10-20T10:00:00+08:00"
            }
            """);
        return root;
    }

    private static void UpToDateWhenHashMatches()
    {
        using var workspace = new TempWorkspace("upgrade-same");
        var project = NewProject(workspace, NewHash);

        var plan = new TemplateUpgradeService().Plan(project, TemplateName, NewTemplate());

        Check.Equal(UpgradeState.UpToDate, plan.State, "指纹一致应判定为已对齐");
        Check.That(!plan.HasWork, "已对齐时不应有待补项");
        Check.That(!plan.CanApply, "已对齐时不应允许补结构");
    }

    private static void OutdatedListsMissingDirectoriesOnly()
    {
        using var workspace = new TempWorkspace("upgrade-diff");
        var project = NewProject(workspace);

        var plan = new TemplateUpgradeService().Plan(project, TemplateName, NewTemplate());

        Check.Equal(UpgradeState.Outdated, plan.State, "指纹不同应判定为待升级");
        Check.That(plan.HasWork, "应有待补目录");
        Check.That(plan.CanApply, "待升级且有待补项时应可执行");

        var missing = string.Join("|", plan.MissingDirectories);
        Check.That(missing.Contains("docs", StringComparison.Ordinal), $"应包含缺失的 docs，实际：{missing}");
        Check.That(missing.Contains("output", StringComparison.Ordinal), $"应包含缺失的 output，实际：{missing}");
        Check.That(
            !missing.Contains("output/installer", StringComparison.Ordinal),
            $"未勾选的 installer 模块不应被补进结构，实际：{missing}");
        Check.That(
            !plan.MissingDirectories.Contains("src", StringComparer.Ordinal),
            $"已存在的 src 不应出现在待补列表，实际：{missing}");
        Check.That(
            plan.MissingDirectories.Contains("src/App", StringComparer.Ordinal),
            $"已勾选模块的子目录缺失时应补出来，实际：{missing}");
    }

    private static void NotManagedWhenMetadataMissing()
    {
        using var workspace = new TempWorkspace("upgrade-none");
        var root = workspace.Path2("external");
        Directory.CreateDirectory(root);

        var plan = new TemplateUpgradeService().Plan(root, string.Empty, NewTemplate());

        Check.Equal(UpgradeState.NotManaged, plan.State, "外部项目应为 NotManaged");
        Check.That(!plan.CanApply, "NotManaged 不应允许补结构");
    }

    private static void TemplateMissingWhenTemplateGone()
    {
        using var workspace = new TempWorkspace("upgrade-gone");
        var project = NewProject(workspace);

        var plan = new TemplateUpgradeService().Plan(project, TemplateName, null);

        Check.Equal(UpgradeState.TemplateMissing, plan.State, "模板被删除时应单独提示而不是报错");
        Check.That(!plan.CanApply, "模板缺失时不应允许补结构");
    }

    private static void ApplyCreatesStructureAndUpdatesHash()
    {
        using var workspace = new TempWorkspace("upgrade-apply");
        var project = NewProject(workspace);
        var service = new TemplateUpgradeService();
        var template = NewTemplate();

        var plan = service.Plan(project, TemplateName, template);
        var outcome = service.Apply(project, template, plan);

        Check.That(outcome.CreatedDirectories.Count >= 2, $"应至少补出 docs 与 output，实际 {outcome.CreatedDirectories.Count}");
        Check.That(Directory.Exists(Path.Combine(project, "docs")), "docs 目录应已创建");
        Check.That(Directory.Exists(Path.Combine(project, "output")), "output 目录应已创建");
        Check.That(
            !Directory.Exists(Path.Combine(project, "output", "installer")),
            "未勾选模块的目录不应被创建");
        Check.That(
            File.Exists(Path.Combine(project, "docs", TreeBuilder.PlaceholderFileName)),
            "空叶子目录应生成占位文件");
        Check.That(
            !File.Exists(Path.Combine(project, "output", TreeBuilder.PlaceholderFileName)),
            "被 .gitignore 忽略的目录不应生成占位文件");
        Check.That(outcome.MetadataUpdated, "补结构后应更新模板指纹");
        Check.That(outcome.Message.Contains("已补", StringComparison.Ordinal), $"结果文案应说明补了什么，实际：{outcome.Message}");

        var after = service.Plan(project, TemplateName, template);
        Check.Equal(UpgradeState.UpToDate, after.State, "补完结构后应判定为已对齐");

        var again = service.Apply(project, template, service.Plan(project, TemplateName, template));
        Check.Equal(0, again.CreatedDirectories.Count, "重复补结构应幂等（不再新建目录）");
    }

    private static void ApplyNeverOverwritesExistingFiles()
    {
        using var workspace = new TempWorkspace("upgrade-keep");
        var project = NewProject(workspace);
        var docsPlaceholder = Path.Combine(project, "docs", TreeBuilder.PlaceholderFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(docsPlaceholder)!);
        File.WriteAllText(docsPlaceholder, "用户自己写的说明");

        var service = new TemplateUpgradeService();
        var template = NewTemplate();
        service.Apply(project, template, service.Plan(project, TemplateName, template));

        Check.Equal(
            "用户自己写的说明",
            File.ReadAllText(docsPlaceholder),
            "占位文件已存在时不得覆盖用户内容");
    }
}
