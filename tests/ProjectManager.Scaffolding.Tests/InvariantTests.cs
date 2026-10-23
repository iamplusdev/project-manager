using System.Text;
using System.Text.Json;
using ProjectManager.Scaffolding.IO;
using ProjectManager.Scaffolding.Models;
using ProjectManager.Scaffolding.Scaffolding;

namespace ProjectManager.Scaffolding.Tests;

/// <summary>G1–G10 每条不变量至少一个针对性用例（出口门禁 G1 的硬要求）。</summary>
public static class InvariantTests
{
    private static readonly DateTimeOffset Fixed = new(2026, 10, 23, 10, 0, 0, TimeSpan.FromHours(8));

    public static void Run(IReadOnlyDictionary<string, Models.Template> templates)
    {
        var light = templates["轻量工具"];
        var desktop = templates["单体桌面工具"];

        G1_IdempotentWithSequencedBackup(light);
        G2_PreserveUserGitignore(light);
        G3_PlaceholderRules(light, desktop);
        G4_PreflightGuards(light);
        G5_RollbackOnIoFailure(light);
        G6_EncodingAndSeparators(light);
        G7_NoUiDependency();
        G8_CancellationOnlyAtPhaseBoundary(light);
        G8b_RollbackFailureDegrades(light);
        G9_InjectedFilesNeverOverwrite();
        G10_MetadataWritten();
        DryRunWritesNothing(light);
        PreviewMatchesGeneration(light);
        TemplateSourcePriority();
    }

    private static ScaffoldGenerator Generator(IFileSystem? fs = null, IClock? clock = null)
        => new(fs, clock ?? new FixedClock(Fixed));

    // ── G1 幂等 + 备份序号 ────────────────────────────────────────────────
    private static void G1_IdempotentWithSequencedBackup(Models.Template template)
    {
        Check.Section("G1 幂等重跑与 .bak 序号");

        using var context = new ScaffoldContext();
        var generator = Generator();

        generator.Generate(context.Request(), template);
        var readmePath = Path.Combine(context.ProjectPath, "README.md");
        var firstContent = File.ReadAllText(readmePath);
        Check.That(firstContent.StartsWith("# MyProject", StringComparison.Ordinal), "G1：首次生成 README 标题正确");

        var second = generator.Generate(context.Request(), template);
        Check.That(
            second.Backups.Contains("README.md.20261023-100000.bak"),
            $"G1：第二次生成应备份为 .bak，实际：{string.Join(",", second.Backups)}");

        var third = generator.Generate(context.Request(), template);
        Check.That(
            third.Backups.Contains("README.md.20261023-100000.bak-1"),
            $"G1：同一秒第三次生成应加序号 .bak-1，实际：{string.Join(",", third.Backups)}");

        // 既有备份不能被覆盖：内容仍是第二次生成前的那份
        var backup = Path.Combine(context.ProjectPath, "README.md.20261023-100000.bak");
        Check.Equal(firstContent, File.ReadAllText(backup), "G1：备份内容应为上一次的 README");
    }

    // ── G2 保留用户 .gitignore ───────────────────────────────────────────
    private static void G2_PreserveUserGitignore(Models.Template template)
    {
        Check.Section("G2 保留既有 .gitignore");

        using var context = new ScaffoldContext();
        Directory.CreateDirectory(context.ProjectPath);
        var gitignorePath = Path.Combine(context.ProjectPath, ".gitignore");
        const string userRules = "# 用户既有规则\n*.tmp\n";
        File.WriteAllText(gitignorePath, userRules);

        var result = Generator().Generate(context.Request(), template);

        Check.Equal(userRules, File.ReadAllText(gitignorePath), "G2：用户 .gitignore 不应被覆盖");
        Check.That(result.Preserved.Contains(".gitignore"), "G2：结果应把 .gitignore 计入「保留」");
    }

    // ── G3 占位规则 ─────────────────────────────────────────────────────
    private static void G3_PlaceholderRules(Models.Template light, Models.Template desktop)
    {
        Check.Section("G3 .gitkeep 占位规则");

        using (var context = new ScaffoldContext())
        {
            Directory.CreateDirectory(Path.Combine(context.ProjectPath, "src"));
            var existing = Path.Combine(context.ProjectPath, "src", ".gitkeep");
            File.WriteAllText(existing, "用户在此写过说明");

            Generator().Generate(context.Request(), light);

            Check.Equal("用户在此写过说明", File.ReadAllText(existing), "G3：已存在的 .gitkeep 不应被清空重写");
            Check.That(
                File.Exists(Path.Combine(context.ProjectPath, "docs", ".gitkeep")),
                "G3：空叶子目录应写 .gitkeep");
        }

        using (var context = new ScaffoldContext())
        {
            Generator().Generate(
                context.Request(desktop.Modules.Select(module => module.Key)),
                desktop);

            Check.That(
                !File.Exists(Path.Combine(context.ProjectPath, "output", ".gitkeep")),
                "G3：被 .gitignore 忽略的目录不应写占位文件");
            Check.That(
                !File.Exists(Path.Combine(context.ProjectPath, "output", "installer", ".gitkeep")),
                "G3：被忽略目录下的子目录同样不占位");
            Check.That(
                File.Exists(Path.Combine(context.ProjectPath, "src", "App", ".gitkeep")),
                "G3：非忽略的空叶子目录仍应占位");
        }
    }

    // ── G4 预检 ─────────────────────────────────────────────────────────
    private static void G4_PreflightGuards(Models.Template template)
    {
        Check.Section("G4 预检（冲突 / 父目录 / 不可写）");

        using (var context = new ScaffoldContext())
        {
            Directory.CreateDirectory(context.ProjectPath);
            File.WriteAllText(Path.Combine(context.ProjectPath, "src"), "我是文件，不是目录");

            Check.Throws<ScaffoldException>(
                () => Generator().Generate(context.Request(), template),
                "G4：同名文件占用应整体拒绝",
                ex => ex.Code == ScaffoldErrorCode.PathOccupiedByFile && ex.Details == "src");
        }

        using (var context = new ScaffoldContext())
        {
            var request = new GenerationRequest
            {
                ProjectName = "MyProject",
                Description = "测试",
                TargetDirectory = Path.Combine(context.Target, "不存在的子目录"),
            };

            Check.Throws<ScaffoldException>(
                () => Generator().Generate(request, template),
                "G4：父目录不存在应报 PM-SCF-001",
                ex => ex.ErrorCode == "PM-SCF-001");
        }

        using (var context = new ScaffoldContext())
        {
            var readOnly = new FaultInjectingFileSystem { Writable = false };
            Check.Throws<ScaffoldException>(
                () => Generator(readOnly).Generate(context.Request(), template),
                "G4：父目录不可写应报 PM-SCF-002",
                ex => ex.ErrorCode == "PM-SCF-002");
        }
    }

    // ── G5 回滚 ─────────────────────────────────────────────────────────
    private static void G5_RollbackOnIoFailure(Models.Template template)
    {
        Check.Section("G5 失败回滚（不动用户既有文件）");

        using var context = new ScaffoldContext();
        Directory.CreateDirectory(context.ProjectPath);
        var userFile = Path.Combine(context.ProjectPath, "我的笔记.txt");
        File.WriteAllText(userFile, "用户手工创建");

        var faulty = new FaultInjectingFileSystem { WritesBeforeFailure = 1 };
        Check.Throws<ScaffoldException>(
            () => Generator(faulty).Generate(context.Request(), template),
            "G5：写入失败应抛 PM-SCF-020",
            ex => ex.ErrorCode == "PM-SCF-020");

        Check.That(File.Exists(userFile), "G5：用户既有文件必须保留");
        Check.That(!File.Exists(Path.Combine(context.ProjectPath, "README.md")), "G5：本次写入的 README 应被撤销");
        Check.That(
            !File.Exists(Path.Combine(context.ProjectPath, "src", ".gitkeep")),
            "G5：本次新建的占位文件应被撤销");
    }

    // ── G6 编码与分隔符 ─────────────────────────────────────────────────
    private static void G6_EncodingAndSeparators(Models.Template template)
    {
        Check.Section("G6 UTF-8 无 BOM 与 '/' 分隔");

        using var context = new ScaffoldContext();
        var result = Generator().Generate(context.Request(), template);

        foreach (var relative in new[] { "README.md", "structure.md", ".gitignore" })
        {
            var bytes = File.ReadAllBytes(Path.Combine(context.ProjectPath, relative));
            var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            Check.That(!hasBom, $"G6：{relative} 不应带 BOM");
        }

        var allPaths = result.DirectoriesCreated
            .Concat(result.FilesWritten)
            .Concat(result.Placeholders)
            .Concat(result.Backups);
        Check.That(allPaths.All(path => !path.Contains('\\')), "G6：结果中的路径应统一使用 '/'");
    }

    // ── G7 纯逻辑 ───────────────────────────────────────────────────────
    private static void G7_NoUiDependency()
    {
        Check.Section("G7 引擎零 UI / 零存储依赖");

        var referenced = typeof(ScaffoldGenerator).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        foreach (var forbidden in new[] { "PresentationFramework", "PresentationCore", "WindowsBase", "System.Windows.Forms" })
        {
            Check.That(
                !referenced.Contains(forbidden, StringComparer.Ordinal),
                $"G7：引擎不应引用 {forbidden}，实际引用：{string.Join(",", referenced)}");
        }
    }

    // ── G8 取消只在阶段间隙生效 ──────────────────────────────────────────
    private static void G8_CancellationOnlyAtPhaseBoundary(Models.Template template)
    {
        Check.Section("G8 取消只在阶段间隙生效，回滚不可中断");

        using var context = new ScaffoldContext();
        using var cts = new CancellationTokenSource();
        var faulty = new FaultInjectingFileSystem();
        var directoriesCreated = 0;
        faulty.OnDirectoryCreated = _ =>
        {
            directoriesCreated++;
            if (directoriesCreated == 2)
            {
                cts.Cancel();
            }
        };

        var cancelled = false;
        try
        {
            Generator(faulty).Generate(context.Request(), template, cts.Token);
        }
        catch (ScaffoldException ex) when (ex.Code == ScaffoldErrorCode.Cancelled)
        {
            cancelled = true;
            Check.That(ex.Message.Contains("已回滚", StringComparison.Ordinal), "G8：取消后应完成回滚");
        }

        Check.That(cancelled, "G8：阶段间隙检测到取消应抛 PM-SCF-030");
        Check.That(!Directory.Exists(context.ProjectPath), "G8：取消后不应留下半生成目录");
    }

    // ── G8 降级：回滚失败 ───────────────────────────────────────────────
    private static void G8b_RollbackFailureDegrades(Models.Template template)
    {
        Check.Section("G8 降级：回滚失败输出清单（PM-SCF-021）");

        using var context = new ScaffoldContext();
        var faulty = new FaultInjectingFileSystem { WritesBeforeFailure = 1, FailDeletes = true };

        Check.Throws<ScaffoldException>(
            () => Generator(faulty).Generate(context.Request(), template),
            "回滚失败应报 PM-SCF-021 并给出清理清单",
            ex => ex.Code == ScaffoldErrorCode.RollbackFailed && ex.ManualCleanupList.Count > 0);
    }

    // ── G9 files 注入不覆盖用户文件 ──────────────────────────────────────
    private static void G9_InjectedFilesNeverOverwrite()
    {
        Check.Section("G9 files 注入与占位符");

        const string json = "{"
            + "\"带文件模板\": {"
            + "\"structure\": { \"src\": [] },"
            + "\"modules\": [],"
            + "\"descriptions\": { \"src\": \"源码\" },"
            + "\"readme_quickstart\": \"1. 编译。\","
            + "\"gitignore\": [],"
            + "\"files\": {"
            + "\"{{name}}.sln\": \"files/app.sln.tpl\","
            + "\"src/{{name}}/Program.cs\": \"files/program.cs.tpl\""
            + "}"
            + "}"
            + "}";

        var templateFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["files/app.sln.tpl"] = "Solution: {{name}}\nTemplate: {{template}}\n",
            ["files/program.cs.tpl"] =
                "// {{name}}\n// {{description}}\n// {{date}}\n"
                + "namespace {{name}};\n"
                + "// 未知占位符必须原样保留：{{unknown}}\n",
        };

        using var fixture = TemplateFixture.Create(json, templateFiles);
        using var context = new ScaffoldContext();
        var generator = Generator(clock: new FixedClock(Fixed));

        var result = generator.Generate(context.Request(description: "带文件的项目"), fixture.Template);

        var sln = Path.Combine(context.ProjectPath, "MyProject.sln");
        var program = Path.Combine(context.ProjectPath, "src", "MyProject", "Program.cs");

        Check.That(File.Exists(sln), "G9：键上的 {{name}} 应替换后建文件（MyProject.sln）");
        Check.That(File.Exists(program), "G9：嵌套路径的注入文件应创建（src/MyProject/Program.cs）");
        Check.Equal(2, result.InjectedFiles.Count, "G9：注入文件数应为 2");

        var slnText = File.ReadAllText(sln);
        Check.That(slnText.Contains("Solution: MyProject", StringComparison.Ordinal), "G9：内容中的 {{name}} 应替换");
        Check.That(slnText.Contains("Template: 带文件模板", StringComparison.Ordinal), "G9：{{template}} 应替换为模板名");

        var programText = File.ReadAllText(program);
        Check.That(programText.Contains("// 带文件的项目", StringComparison.Ordinal), "G9：{{description}} 应替换");
        Check.That(programText.Contains("// 2026-10-23", StringComparison.Ordinal), "G9：{{date}} 应替换为 yyyy-MM-dd");
        Check.That(
            programText.Contains("{{unknown}}", StringComparison.Ordinal),
            "G9：未识别的占位符必须原样保留（模板内容可能本身就含双花括号）");

        // 二次生成：目标已存在 → 跳过，绝不覆盖
        File.WriteAllText(sln, "用户改过的内容");
        var second = generator.Generate(context.Request(description: "带文件的项目"), fixture.Template);
        Check.Equal("用户改过的内容", File.ReadAllText(sln), "G9：已存在的目标文件不应被覆盖");
        Check.That(second.SkippedExisting.Contains("MyProject.sln"), "G9：跳过的文件应计入「已保留」");
    }

    // ── G10 元数据 ──────────────────────────────────────────────────────
    private static void G10_MetadataWritten()
    {
        Check.Section("G10 .pm-scaffold.json 元数据");

        var json = TemplateFixture.MinimalJson("元数据模板");
        using var fixture = TemplateFixture.Create(json);
        using var context = new ScaffoldContext();

        Generator(clock: new FixedClock(Fixed)).Generate(context.Request(), fixture.Template);

        var metadataPath = Path.Combine(context.ProjectPath, ".pm-scaffold.json");
        if (!Check.Ok(File.Exists(metadataPath), "G10：项目根应写 .pm-scaffold.json"))
        {
            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
        var root = document.RootElement;

        Check.Equal("ProjectManager.Scaffolding", root.GetProperty("engine").GetString(), "G10：engine 字段");
        Check.Equal("元数据模板", root.GetProperty("template").GetString(), "G10：template 字段");
        Check.Equal("MyProject", root.GetProperty("projectName").GetString(), "G10：projectName 字段");
        Check.That(root.GetProperty("engineVersion").GetString() == "0.1.0", "G10：engineVersion 字段");
        Check.That(
            root.GetProperty("templateHash").GetString()?.Length == 64,
            "G10：templateHash 应为 SHA256 十六进制");
        Check.That(
            DateTimeOffset.TryParse(root.GetProperty("generatedAt").GetString(), out _),
            "G10：generatedAt 应为可解析时间");

        var gitignorePath = Path.Combine(context.ProjectPath, ".gitignore");
        Check.That(
            !File.ReadAllText(gitignorePath).Contains(".pm-scaffold.json", StringComparison.Ordinal),
            "G10：元数据文件不应加入 .gitignore（它是项目的溯源事实）");
    }

    // ── DryRun / Preview ───────────────────────────────────────────────
    private static void DryRunWritesNothing(Models.Template template)
    {
        Check.Section("DryRun 不落盘 / Preview 与生成一致");

        using var context = new ScaffoldContext();
        var generator = Generator();
        var result = generator.Generate(context.Request(dryRun: true), template);

        Check.That(result.DryRun, "DryRun：结果应标记为 DryRun");
        Check.That(!Directory.Exists(context.ProjectPath), "DryRun：不应创建任何目录");
        Check.That(result.DirectoriesCreated.Count > 0, "DryRun：仍应返回计划创建的目录清单");
    }

    private static void PreviewMatchesGeneration(Models.Template template)
    {
        using var context = new ScaffoldContext();
        var generator = Generator();
        var plan = generator.Preview(context.Request(), template);

        var result = generator.Generate(context.Request(), template);

        Check.SequenceEqual(
            plan.DirectoriesToCreate.OrderBy(item => item, StringComparer.Ordinal),
            result.DirectoriesCreated.Where(item => item != ".").OrderBy(item => item, StringComparer.Ordinal),
            "Preview：目录清单应与实际创建一致");

        Check.SequenceEqual(
            plan.PlaceholdersToCreate.OrderBy(item => item, StringComparer.Ordinal),
            result.Placeholders.OrderBy(item => item, StringComparer.Ordinal),
            "Preview：占位清单应与实际一致");
    }

    // ── 模板三源优先级（WP1.2）─────────────────────────────────────────
    private static void TemplateSourcePriority()
    {
        Check.Section("模板来源优先级：%APPDATA% > 安装目录 > 内置");

        var root = FixtureLocator.CreateTempDirectory("sources");
        try
        {
            var userPath = Path.Combine(root, "user", "templates.json");
            var installPath = Path.Combine(root, "install", "config", "templates.json");
            Directory.CreateDirectory(Path.GetDirectoryName(userPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(installPath)!);

            File.WriteAllText(installPath, TemplateFixture.MinimalJson("安装模板"));
            File.WriteAllText(userPath, TemplateFixture.MinimalJson("用户模板"));

            var loader = new ProjectManager.Scaffolding.Templates.TemplateLoader(userPath, installPath);
            var loaded = loader.Load();
            Check.Equal(TemplateSource.User, loaded.Source, "来源：用户目录应优先");
            Check.That(loaded.Templates.ContainsKey("用户模板"), "来源：应读到用户模板");

            // 用户配置损坏 → 回退安装目录，且失败原因不静默
            File.WriteAllText(userPath, "{ 这不是合法 JSON");
            var fallback = loader.Load();
            Check.Equal(TemplateSource.Install, fallback.Source, "来源：用户配置损坏应回退安装目录");
            Check.That(
                fallback.Warnings.Any(warning => warning.Contains("PM-SCF-010", StringComparison.Ordinal)),
                "来源：回退时应把失败原因作为警告带出");

            // 首次启动复制，而不是引用安装目录（安装目录会被更新整体替换）
            File.Delete(userPath);
            var seeded = loader.TrySeedUserCopy();
            Check.That(seeded && File.Exists(userPath), "来源：首次启动应把安装目录模板复制到用户目录");
        }
        finally
        {
            FixtureLocator.TryDelete(root);
        }
    }
}
