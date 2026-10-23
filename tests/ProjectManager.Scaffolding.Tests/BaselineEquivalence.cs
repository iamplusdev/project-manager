using System.Diagnostics;
using ProjectManager.Scaffolding.Models;
using ProjectManager.Scaffolding.Scaffolding;
using ProjectManager.Scaffolding.Templates;

namespace ProjectManager.Scaffolding.Tests;

/// <summary>
/// 等价性测试（WP1.10 / G0）。用引擎重现 S0 冻结的 8 组 Forge 产出并逐条比对。
/// </summary>
/// <remarks>
/// 用例定义必须与 <c>tests/fixtures/tools/forge_baseline.py</c> 的 <c>CASES</c> 一致：
/// 用例 ID、模板、模块选择、预置文件、项目名、简介文案缺一不可——
/// 基线是"参考实现的事实"，改变输入等于换了一份基线，比对就失去意义。
/// </remarks>
public static class BaselineEquivalence
{
    private const string ProjectName = "MyProject";
    private const string BaselineDescription = "S0 基线项目：仅用于 Forge 等价性比对";

    private sealed record BaselineCase(
        string Id,
        string TemplateName,
        ModuleSelection Modules,
        IReadOnlyDictionary<string, string>? Seed);

    private enum ModuleSelection
    {
        All,
        None,
        DesktopOnly,
    }

    private static readonly BaselineCase[] Cases =
    {
        new("case-01-fullstack-all", "全栈项目", ModuleSelection.All, null),
        new("case-02-fullstack-none", "全栈项目", ModuleSelection.None, null),
        new("case-03-light-tool", "轻量工具", ModuleSelection.None, null),
        new("case-04-light-tool-repeat", "轻量工具", ModuleSelection.None, null),
        new("case-05-desktop-all", "单体桌面工具", ModuleSelection.All, null),
        new("case-06-desktop-main-only", "单体桌面工具", ModuleSelection.DesktopOnly, null),
        new(
            "case-07-fullstack-rerun",
            "全栈项目",
            ModuleSelection.All,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["README.md"] = "# 既有项目\n\n> 手工创建的说明，用于验证幂等与备份\n",
                [".gitignore"] = "# 用户既有规则\n*.tmp\n",
            }),
        new(
            "case-08-desktop-rerun",
            "单体桌面工具",
            ModuleSelection.All,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["README.md"] = "# 既有桌面项目\n\n> 预置 README，验证新模板的幂等与备份\n",
            }),
    };

    public static IReadOnlyDictionary<string, Template> Run()
    {
        Check.Section("等价基线（G0）：8 组用例对照 Forge 快照");

        var templates = LoadBuiltinTemplates();
        Check.Equal(3, templates.Count, "内置模板数量应为 3 套");

        var totalEolOnly = 0;
        foreach (var baselineCase in Cases)
        {
            if (!templates.TryGetValue(baselineCase.TemplateName, out var template))
            {
                Check.That(false, $"{baselineCase.Id}：内置模板缺少「{baselineCase.TemplateName}」");
                continue;
            }

            var target = FixtureLocator.CreateTempDirectory(baselineCase.Id);
            try
            {
                var projectDirectory = Path.Combine(target, ProjectName);
                if (baselineCase.Seed is { Count: > 0 } seed)
                {
                    Directory.CreateDirectory(projectDirectory);
                    foreach (var (relative, content) in seed)
                    {
                        var file = Path.Combine(projectDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        File.WriteAllText(file, content);
                    }
                }

                var generator = new ScaffoldGenerator();
                var request = new GenerationRequest
                {
                    ProjectName = ProjectName,
                    Description = BaselineDescription,
                    TargetDirectory = target,
                    SelectedModules = SelectModules(template, baselineCase.Modules),
                };

                generator.Generate(request, template);

                var report = SnapshotComparer.Compare(
                    FixtureLocator.CaseDirectory(baselineCase.Id),
                    projectDirectory,
                    TreeBuilder.IgnoredDirectoryPrefixes(template.Gitignore));

                totalEolOnly += report.EolOnlyFiles.Count;
                var label = report.Passed
                    ? $"OK（行尾归一 {report.EolOnlyFiles.Count} 个文件）"
                    : "不一致" + Environment.NewLine + report.Describe();
                Check.That(report.Passed, $"{baselineCase.Id}：{label}");
                Console.WriteLine($"  {baselineCase.Id,-28} {(report.Passed ? "一致" : "不一致")}");
            }
            finally
            {
                FixtureLocator.TryDelete(target);
            }
        }

        // 行尾差异必须被"度量"：若为 0，说明比较器没走到归一分支（可能被跳过而非通过）
        Check.That(totalEolOnly > 0, "行尾归一文件数为 0，说明 CRLF/LF 归一分支未被覆盖");
        return templates;
    }

    private static IReadOnlyList<string> SelectModules(Template template, ModuleSelection selection)
        => selection switch
        {
            ModuleSelection.All => template.Modules.Select(module => module.Key).ToArray(),
            ModuleSelection.DesktopOnly => template.Modules
                .Where(module => module.Key == "desktop")
                .Select(module => module.Key)
                .ToArray(),
            _ => Array.Empty<string>(),
        };

    /// <summary>
    /// 用「不存在的用户/安装路径」加载：强制走内置资源，同时顺带验证三源降级链（WP1.2）。
    /// </summary>
    public static IReadOnlyDictionary<string, Template> LoadBuiltinTemplates()
    {
        var loader = new TemplateLoader(
            userPath: Path.Combine(Path.GetTempPath(), "__pm_no_user_templates__", "templates.json"),
            installPath: Path.Combine(Path.GetTempPath(), "__pm_no_install_templates__", "templates.json"));

        var stopwatch = Stopwatch.StartNew();
        var result = loader.Load();
        stopwatch.Stop();

        Check.That(result.Source == TemplateSource.Builtin, "模板应回退到内置资源");
        Check.That(
            stopwatch.ElapsedMilliseconds < 500,
            $"模板解析耗时 {stopwatch.ElapsedMilliseconds} ms（预算 <50 ms，含 JIT 放宽到 500 ms）");
        Console.WriteLine($"  内置模板解析：{stopwatch.ElapsedMilliseconds} ms（{result.Templates.Count} 套）");

        return result.Templates;
    }
}
