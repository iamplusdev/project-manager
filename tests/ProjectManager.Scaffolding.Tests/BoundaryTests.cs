using ProjectManager.Scaffolding.Models;
using ProjectManager.Scaffolding.Scaffolding;
using ProjectManager.Scaffolding.Validation;

namespace ProjectManager.Scaffolding.Tests;

/// <summary>边界表：名称 49/50/51、路径 259/260、保留名、emoji、结尾点、超长简介、中文路径。</summary>
public static class BoundaryTests
{
    public static void Run(IReadOnlyDictionary<string, Template> templates)
    {
        var light = templates["轻量工具"];

        var name = new NameBoundaries();
        name.Run();

        var description = new DescriptionBoundaries();
        description.Run();

        PathBoundaries(light);
        ChinesePath(light);
    }

    private sealed class NameBoundaries
    {
        public void Run()
        {
            Check.Section("边界：项目名称");

            Check.That(ProjectValidator.ValidateName(new string('a', 49)) is null, "名称 49 字应通过");
            Check.That(ProjectValidator.ValidateName(new string('a', 50)) is null, "名称 50 字应通过");
            Check.That(ProjectValidator.ValidateName(new string('a', 51)) is not null, "名称 51 字应拒绝");

            // 长度按 Unicode 码点计（与 Python len 一致）：50 个 emoji 是 50 字，不是 100
            Check.That(ProjectValidator.ValidateName(string.Concat(Enumerable.Repeat("😀", 50))) is null,
                "50 个 emoji 应按 50 字通过（码点计数）");
            Check.That(ProjectValidator.ValidateName(string.Concat(Enumerable.Repeat("😀", 51))) is not null,
                "51 个 emoji 应拒绝");

            Check.That(ProjectValidator.ValidateName(string.Concat(Enumerable.Repeat("中", 50))) is null,
                "50 个中文字应通过");

            foreach (var reserved in new[] { "con", "CON", "prn", "aux", "nul", "com1", "lpt9", "con.txt", "NUL.log" })
            {
                Check.That(ProjectValidator.ValidateName(reserved) is not null, $"Windows 保留名应拒绝：{reserved}");
            }

            Check.That(ProjectValidator.ValidateName("console") is null, "console 不是保留名，应通过");
            Check.That(ProjectValidator.ValidateName("lpt10") is null, "lpt10 不是保留名，应通过");

            foreach (var illegal in new[] { @"a\b", "a/b", "a:b", "a*b", "a?b", "a\"b", "a<b", "a>b", "a|b" })
            {
                Check.That(ProjectValidator.ValidateName(illegal) is not null, $"非法字符应拒绝：{illegal}");
            }

            Check.That(ProjectValidator.ValidateName(".") is not null, ". 应拒绝");
            Check.That(ProjectValidator.ValidateName("..") is not null, ".. 应拒绝");
            Check.That(ProjectValidator.ValidateName("项目.") is not null, "以点结尾应拒绝");
            Check.That(ProjectValidator.ValidateName("   ") is not null, "全空白应拒绝");
            Check.Equal(null, ProjectValidator.ValidateName("  前后空白会被 trim  "), "trim 后合法应通过");

            Check.That(ProjectValidator.ValidateName("my-project_2") is null, "连字符下划线数字应通过");
        }
    }

    private sealed class DescriptionBoundaries
    {
        public void Run()
        {
            Check.Section("边界：项目简介");

            Check.That(ProjectValidator.ValidateDescription(new string('a', 200)) is null, "简介 200 字应通过");
            Check.That(ProjectValidator.ValidateDescription(new string('a', 201)) is not null, "简介 201 字应拒绝");
            Check.That(ProjectValidator.ValidateDescription("  ") is not null, "空白简介应拒绝");
        }
    }

    private static void PathBoundaries(Template template)
    {
        Check.Section("边界：路径长度 259 / 260");

        // 最长计划路径 = 项目根 + 最长的候选：可能是某空叶子目录下的 .gitkeep，
        // 也可能是根目录的 .pm-scaffold.json（元数据名更长，实测就是它决定了上限）
        var longestLeaf = TreeBuilder.EmptyLeafDirectories(template.Structure)
            .Select(leaf => leaf.Length)
            .DefaultIfEmpty(0)
            .Max();
        var suffix = Math.Max(
            longestLeaf + "/.gitkeep".Length + 1,
            ScaffoldMetadata.FileName.Length + 1);

        var target = FixtureLocator.CreateTempDirectory("len");
        try
        {
            if (!TryGenerateWithLongestPath(target, template, 259, suffix, out var ok259))
            {
                Console.WriteLine("  （跳过精确边界：本机临时目录路径过长，无法构造 259 字符的目标）");
                return;
            }

            Check.That(ok259, "最长路径 259 字符应允许生成");

            TryGenerateWithLongestPath(target, template, 260, suffix, out var ok260);
            Check.That(!ok260, "最长路径 260 字符应拒绝（PM-SCF-040）");

            var tooLong = TryGenerateWithLongestPath(target, template, 300, suffix, out var ok300);
            Check.That(tooLong && !ok300, "最长路径 300 字符应拒绝（PM-SCF-040）");
        }
        finally
        {
            FixtureLocator.TryDelete(target);
        }
    }

    /// <summary>
    /// 通过加长项目名，使「最长计划路径」恰好等于 <paramref name="desiredLength"/>，再尝试生成。
    /// 返回 false 表示本机路径条件下无法构造该长度（此时跳过，不伪装成通过）。
    /// </summary>
    private static bool TryGenerateWithLongestPath(
        string target,
        Template template,
        int desiredLength,
        int suffix,
        out bool succeeded)
    {
        succeeded = false;
        var projectPathLength = desiredLength - suffix;
        var nameLength = projectPathLength - target.Length - 1;
        if (nameLength < 1 || nameLength > 200)
        {
            return false;
        }

        var projectName = new string('p', nameLength);
        var request = new GenerationRequest
        {
            ProjectName = projectName,
            Description = "路径长度边界",
            TargetDirectory = target,
        };

        var generator = new ScaffoldGenerator();
        try
        {
            generator.Generate(request, template);
            succeeded = true;
            var actual = Path.Combine(target, projectName);
            if (Directory.Exists(actual))
            {
                Directory.Delete(actual, recursive: true);
            }
        }
        catch (ScaffoldException ex) when (ex.Code == ScaffoldErrorCode.PathTooLong)
        {
            succeeded = false;
        }
        catch (PathTooLongException)
        {
            succeeded = false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  （{desiredLength} 字符路径生成异常：{ex.GetType().Name}: {FirstLine(ex.Message)}）");
            succeeded = false;
        }

        return true;
    }

    private static string FirstLine(string message)
        => message.Split('\n')[0];

    private static void ChinesePath(Template template)
    {
        Check.Section("边界：中文路径");

        using var context = new ScaffoldContext(projectName: "中文项目名");
        var chineseTarget = Path.Combine(context.Target, "新建 项目");
        Directory.CreateDirectory(chineseTarget);

        var request = new GenerationRequest
        {
            ProjectName = "中文项目名",
            Description = "验证中文路径与中文名的完整链路",
            TargetDirectory = chineseTarget,
        };

        var generator = new ScaffoldGenerator();
        var result = generator.Generate(request, template);

        Check.That(Directory.Exists(result.ProjectPath), "中文路径下应成功生成");
        Check.That(
            File.Exists(Path.Combine(result.ProjectPath, "README.md")),
            "中文路径下 README 应落盘");
        Check.That(
            File.ReadAllText(Path.Combine(result.ProjectPath, "README.md")).Contains("中文项目名", StringComparison.Ordinal),
            "README 应包含中文项目名");
    }
}
