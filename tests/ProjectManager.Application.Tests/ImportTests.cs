using ProjectManager.Application.Projects;
using ProjectManager.Storage;

namespace ProjectManager.Application.Tests;

/// <summary>「打开已有项目」：路径归一、元数据采信、同路径去重、损坏文件降级。</summary>
public static class ImportTests
{
    public static void Run()
    {
        Check.Section("Import · 打开已有项目");
        RejectsMissingDirectory();
        AddsPlainDirectory();
        AdoptsScaffoldMetadata();
        DetectsGitRepository();
        ReimportSamePathIsNotDuplicate();
        CorruptMetadataFallsBackToDirectoryName();
    }

    private static ProjectImporter NewImporter(TempWorkspace workspace, out JsonProjectRepository repository)
    {
        repository = new JsonProjectRepository(workspace.Path2("projects.json"));
        return new ProjectImporter(repository);
    }

    private static void RejectsMissingDirectory()
    {
        using var workspace = new TempWorkspace("import-missing");
        var importer = NewImporter(workspace, out var repository);

        var blank = importer.ImportAsync("   ").GetAwaiter().GetResult();
        Check.Equal(ImportStatus.MissingDirectory, blank.Status, "空路径应被拒绝");
        Check.That(!blank.Succeeded, "空路径不应算成功");

        var absent = importer.ImportAsync(workspace.Path2("nope")).GetAwaiter().GetResult();
        Check.Equal(ImportStatus.MissingDirectory, absent.Status, "不存在的目录应被拒绝");

        Check.Equal(0, repository.LoadAsync().GetAwaiter().GetResult().Projects.Count, "被拒绝的导入不应写入清单");
    }

    private static void AddsPlainDirectory()
    {
        using var workspace = new TempWorkspace("import-plain");
        var importer = NewImporter(workspace, out var repository);
        var target = workspace.NewTarget("LegacyTool");

        var result = importer.ImportAsync(target).GetAwaiter().GetResult();

        Check.Equal(ImportStatus.Added, result.Status, "普通目录应加入清单");
        Check.Equal("LegacyTool", result.Project?.Name, "无元数据时项目名取目录名");
        Check.Equal(string.Empty, result.Project?.TemplateName ?? "非空", "普通目录不应有模板名");
        Check.That(result.Project?.Generated == false, "普通目录应标记为非生成项目");
        Check.That(result.Project?.GitInitialized == false, "无 .git 时不应标记已初始化");
        Check.Equal(
            Path.TrimEndingDirectorySeparator(target),
            result.Project?.Path,
            "落库路径应为规范化的绝对路径");
        Check.Equal(1, repository.LoadAsync().GetAwaiter().GetResult().Projects.Count, "清单应有 1 条");
    }

    private static void AdoptsScaffoldMetadata()
    {
        using var workspace = new TempWorkspace("import-metadata");
        var importer = NewImporter(workspace, out var repository);
        var target = workspace.NewTarget("generated");
        File.WriteAllText(
            Path.Combine(target, ".pm-scaffold.json"),
            """
            {
              "engine": "ProjectManager.Scaffolding",
              "template": "单体桌面工具",
              "projectName": "OrderDesk",
              "modules": ["主程序"],
              "generatedAt": "2026-10-23T10:00:00+08:00"
            }
            """);

        var result = importer.ImportAsync(target).GetAwaiter().GetResult();

        Check.Equal(ImportStatus.Added, result.Status, "带元数据的目录应加入清单");
        Check.Equal("OrderDesk", result.Project?.Name, "项目名应采信 .pm-scaffold.json");
        Check.Equal("单体桌面工具", result.Project?.TemplateName, "模板名应采信 .pm-scaffold.json");
        Check.That(result.Project?.Generated == true, "有元数据应视为生成项目");
        Check.That(
            result.Message.Contains("单体桌面工具", StringComparison.Ordinal),
            $"成功文案应说明模板来源，实际：{result.Message}");
    }

    private static void DetectsGitRepository()
    {
        using var workspace = new TempWorkspace("import-git");
        var importer = NewImporter(workspace, out _);
        var target = workspace.NewTarget("withGit");
        Directory.CreateDirectory(Path.Combine(target, ".git"));

        var result = importer.ImportAsync(target).GetAwaiter().GetResult();

        Check.That(result.Project?.GitInitialized == true, "存在 .git 目录时应标记已初始化");
    }

    private static void ReimportSamePathIsNotDuplicate()
    {
        using var workspace = new TempWorkspace("import-dedupe");
        var importer = NewImporter(workspace, out var repository);
        var target = workspace.NewTarget("Alpha");

        var first = importer.ImportAsync(target).GetAwaiter().GetResult();
        var second = importer.ImportAsync(target).GetAwaiter().GetResult();

        Check.Equal(ImportStatus.Added, first.Status, "首次导入应为 Added");
        Check.Equal(ImportStatus.AlreadyManaged, second.Status, "同路径再次导入应为 AlreadyManaged");
        Check.Equal(first.Project?.Id, second.Project?.Id, "同路径应复用同一 Id");
        Check.Equal(1, repository.LoadAsync().GetAwaiter().GetResult().Projects.Count, "重复导入不应产生第二条");

        // 尾部分隔符与大小写不构成"新项目"
        var variant = target + Path.DirectorySeparatorChar;
        var third = importer.ImportAsync(variant).GetAwaiter().GetResult();
        Check.Equal(ImportStatus.AlreadyManaged, third.Status, "带尾分隔符的同路径应被识别为已存在");

        var upper = target.ToUpperInvariant();
        var fourth = importer.ImportAsync(upper).GetAwaiter().GetResult();
        Check.Equal(ImportStatus.AlreadyManaged, fourth.Status, "仅大小写不同的同路径应被识别为已存在");
        Check.Equal(1, repository.LoadAsync().GetAwaiter().GetResult().Projects.Count, "清单仍应只有 1 条");
    }

    private static void CorruptMetadataFallsBackToDirectoryName()
    {
        using var workspace = new TempWorkspace("import-corrupt");
        var importer = NewImporter(workspace, out _);
        var target = workspace.NewTarget("BrokenMeta");
        File.WriteAllText(Path.Combine(target, ".pm-scaffold.json"), "{ 这不是 JSON");

        var result = importer.ImportAsync(target).GetAwaiter().GetResult();

        Check.Equal(ImportStatus.Added, result.Status, "元数据损坏不应阻断导入");
        Check.Equal("BrokenMeta", result.Project?.Name, "元数据损坏时应回退到目录名");
        Check.That(result.Project?.Generated == false, "元数据不可用时应视为非生成项目");
    }
}
