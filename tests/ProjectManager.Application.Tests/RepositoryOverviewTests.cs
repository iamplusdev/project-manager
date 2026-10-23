using ProjectManager.Application.Repositories;
using ProjectManager.CliInterop;
using ProjectManager.Storage;

namespace ProjectManager.Application.Tests;

/// <summary>
/// 跨项目仓库清单（G3 第 2 项）。全部用假 Git，不依赖本机装没装 git；
/// 真 git 行为由 <see cref="GitStatusTests"/> 负责。
/// </summary>
public static class RepositoryOverviewTests
{
    public static void Run()
    {
        Check.Section("Repositories · 仓库清单");
        MapsStatesToDisplayText();
        KeepsInputOrder();
        LimitsConcurrency();
        SurvivesSingleFailure();
        SummarizesCounts();
        HandlesEmptyList();
        ShowsFolderName();
    }

    /// <summary>五种状态 → 徽标与说明文字。视图只读这两行字，判断全在这一层收口。</summary>
    private static void MapsStatesToDisplayText()
    {
        var project = Project("p1", "Demo", @"D:\work\demo");

        var clean = RepositoryEntry.FromStatus(project, new GitStatusSnapshot(true, "main", 0, null));
        Check.Equal("干净", clean.StatusText, "干净仓库的徽标");
        Check.Equal("main · 工作区干净", clean.DetailText, "干净仓库的说明");
        Check.That(clean.IsRepository, "干净仓库应标记为仓库");
        Check.That(!clean.HasChanges, "干净仓库不应算有改动");

        var dirty = RepositoryEntry.FromStatus(project, new GitStatusSnapshot(true, "dev", 3, null));
        Check.Equal("有改动", dirty.StatusText, "有改动仓库的徽标");
        Check.Equal("dev · 3 处未提交改动", dirty.DetailText, "有改动仓库的说明");
        Check.That(dirty.HasChanges, "3 处改动应算有改动");

        var none = RepositoryEntry.FromStatus(project, GitStatusSnapshot.NotRepository());
        Check.Equal("未初始化", none.StatusText, "非仓库的徽标");
        Check.Equal("尚未 git init", none.DetailText, "非仓库的说明");
        Check.That(!none.IsRepository, "非仓库不应标记为仓库");

        var parent = RepositoryEntry.FromStatus(
            project,
            GitStatusSnapshot.InParentRepository(@"D:\work\monorepo"));
        Check.Equal("上层仓库", parent.StatusText, "位于上层仓库时的徽标");
        Check.Equal("位于上层仓库 monorepo 内", parent.DetailText, "应说明上层仓库名，而不是显示上层的分支与改动数");
        Check.That(parent.ParentRepositoryShownByState(), "上层仓库不应被当成独立仓库");

        var broken = RepositoryEntry.FromStatus(project, GitStatusSnapshot.Unavailable("未找到 git"));
        Check.Equal("不可用", broken.StatusText, "查询失败的徽标");
        Check.Equal("未找到 git", broken.DetailText, "失败原因要原样带给用户");
    }

    /// <summary>清单顺序必须与项目清单一致：按状态重排会让用户每次刷新都找不到刚才那行。</summary>
    private static void KeepsInputOrder()
    {
        var projects = new[]
        {
            Project("a", "Alpha", @"D:\w\a"),
            Project("b", "Beta", @"D:\w\b"),
            Project("c", "Gamma", @"D:\w\c"),
        };

        var git = new FakeGitRunner();
        git.StatusByPath[@"D:\w\a"] = GitStatusSnapshot.NotRepository();
        git.StatusByPath[@"D:\w\b"] = new GitStatusSnapshot(true, "main", 5, null);
        git.StatusByPath[@"D:\w\c"] = new GitStatusSnapshot(true, "main", 0, null);

        var entries = new RepositoryOverview(git).QueryAsync(projects).GetAwaiter().GetResult();

        Check.Equal(3, entries.Count, "应有 3 行");
        Check.Equal("Alpha", entries[0].Name, "第一行应是传入顺序的第一个项目");
        Check.Equal("Beta", entries[1].Name, "第二行不应因为改动多而被提前");
        Check.Equal("Gamma", entries[2].Name, "第三行应是传入顺序的第三个项目");
    }

    /// <summary>
    /// 限流：单项目一次查询要起 4 个 git 进程，N 个项目不设上限会同时拉起 4N 个进程。
    /// 并发度必须 ≤ 设定值，且确实并发了（否则"限流"退化成串行，慢得没有意义）。
    /// </summary>
    private static void LimitsConcurrency()
    {
        var projects = Enumerable.Range(0, 10)
            .Select(index => Project($"p{index}", $"P{index}", $@"D:\w\p{index}"))
            .ToArray();

        var git = new FakeGitRunner
        {
            Status = new GitStatusSnapshot(true, "main", 0, null),
            Delay = TimeSpan.FromMilliseconds(15),
        };

        var entries = new RepositoryOverview(git, maxConcurrency: 3).QueryAsync(projects).GetAwaiter().GetResult();

        Check.Equal(10, entries.Count, "10 个项目应查 10 次");
        Check.Equal(10, git.QueriedPaths.Count, "每个项目都应被查询一次");
        Check.That(
            git.MaxObservedConcurrency <= 3,
            $"并发度应被限制在 3 以内，实际观测到 {git.MaxObservedConcurrency}");
        Check.That(
            git.MaxObservedConcurrency > 1,
            $"应确实并发执行（否则限流退化为串行），实际观测到 {git.MaxObservedConcurrency}");
    }

    /// <summary>一个项目查不出来（目录被删、权限不足）不该让整张清单变成空。</summary>
    private static void SurvivesSingleFailure()
    {
        var projects = new[]
        {
            Project("ok", "Ok", @"D:\w\ok"),
            Project("bad", "Bad", @"D:\w\bad"),
        };

        var git = new FakeGitRunner
        {
            Status = new GitStatusSnapshot(true, "main", 0, null),
        };
        git.ThrowFor = path => path.EndsWith("bad", StringComparison.OrdinalIgnoreCase)
            ? new IOException("模拟：目录不可读")
            : null;

        var entries = new RepositoryOverview(git).QueryAsync(projects).GetAwaiter().GetResult();

        Check.Equal(2, entries.Count, "失败的项目也要占一行，否则用户以为项目消失了");
        Check.Equal("Ok", entries[0].Name, "正常项目仍在原位");
        Check.Equal("干净", entries[0].StatusText, "正常项目不受影响");
        Check.Equal("不可用", entries[1].StatusText, "失败项目标为不可用");
        Check.That(
            entries[1].DetailText.Contains("目录不可读", StringComparison.Ordinal),
            $"失败原因应可见，实际：{entries[1].DetailText}");
    }

    /// <summary>
    /// 统计口径：未初始化与不可用必须分开数。
    /// 把"目录已被删掉"算进"未初始化"，会让人以为点一下 init 就能好。
    /// </summary>
    private static void SummarizesCounts()
    {
        var projects = new[]
        {
            Project("a", "A", @"D:\w\a"),
            Project("b", "B", @"D:\w\b"),
            Project("c", "C", @"D:\w\c"),
            Project("d", "D", @"D:\w\d"),
            Project("e", "E", @"D:\w\e"),
        };

        var git = new FakeGitRunner();
        git.StatusByPath[@"D:\w\a"] = new GitStatusSnapshot(true, "main", 0, null);
        git.StatusByPath[@"D:\w\b"] = new GitStatusSnapshot(true, "main", 4, null);
        git.StatusByPath[@"D:\w\c"] = GitStatusSnapshot.NotRepository();
        git.StatusByPath[@"D:\w\d"] = GitStatusSnapshot.InParentRepository(@"D:\w");
        git.StatusByPath[@"D:\w\e"] = GitStatusSnapshot.Unavailable("目录不存在：D:\\w\\e");

        var entries = new RepositoryOverview(git).QueryAsync(projects).GetAwaiter().GetResult();
        var stats = RepositoryStats.From(entries);

        Check.Equal(5, stats.Total, "总数应包含所有行");
        Check.Equal(2, stats.Repositories, "两个项目自身是仓库");
        Check.Equal(1, stats.Dirty, "只有 B 有改动");
        Check.Equal(2, stats.Pending, "C 未初始化 + D 位于上层仓库，都算未初始化");
        Check.Equal(1, stats.Unavailable, "E 是查询失败，不该混进未初始化");
        Check.Equal("2 个仓库 · 1 个有改动 · 2 个未初始化 · 1 个不可用", stats.Text, "统计文案");

        Check.Equal(string.Empty, RepositoryStats.From(Array.Empty<RepositoryEntry>()).Text, "空清单不显示统计");
        Check.Equal(
            "0 个仓库 · 1 个未初始化",
            RepositoryStats.From(new[] { RepositoryEntry.FromStatus(projects[2], GitStatusSnapshot.NotRepository()) }).Text,
            "没有改动/不可用时不显示这两个 0");
    }

    private static void HandlesEmptyList()
    {
        var git = new FakeGitRunner();
        var entries = new RepositoryOverview(git).QueryAsync(Array.Empty<StoredProject>()).GetAwaiter().GetResult();

        Check.Equal(0, entries.Count, "空清单应是空结果");
        Check.Equal(0, git.QueriedPaths.Count, "空清单不应发起任何查询");
    }

    private static void ShowsFolderName()
    {
        Check.Equal(
            "demo",
            Project("x", "X", @"D:\work\demo\").FolderSegment(),
            "带尾分隔符的路径应取到最后一段");

        Check.Equal(
            "demo",
            Project("x", "X", @"D:\work\demo").FolderSegment(),
            "普通路径应取到最后一段");
    }

    private static StoredProject Project(string id, string name, string path) => new()
    {
        Id = id,
        Name = name,
        Path = path,
    };
}

/// <summary>断言用的便捷读取（避免在用例里写一串属性判断）。</summary>
internal static class RepositoryEntryAssertions
{
    public static bool ParentRepositoryShownByState(this RepositoryEntry entry)
        => entry is { IsRepository: false, State: RepositoryState.InParentRepository };

    public static string FolderSegment(this StoredProject project)
        => RepositoryEntry.FromStatus(project, GitStatusSnapshot.NotRepository()).FolderName;
}
