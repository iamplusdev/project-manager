using ProjectManager.CliInterop;

namespace ProjectManager.Application.Tests;

/// <summary>Git 状态：输出解析规则 + 真 git 走一遍（无 git 环境自动跳过真机部分）。</summary>
public static class GitStatusTests
{
    public static void Run()
    {
        Check.Section("Git · 仓库状态");
        ParsesCleanRepository();
        CountsChangedFiles();
        HandlesEmptyBranch();
        SummaryTexts();
        SamePathTolerance();
        RealRepositoryRoundTrip();
        DoesNotInheritParentRepository();
        NestedRepositoryWins();
    }

    /// <summary>
    /// 判定「本目录是不是仓库根」靠路径比较，而 git 回来的路径与用户给的路径在
    /// 尾分隔符、分隔符风格、盘符大小写上都不保证一致——比较必须容忍这三点。
    /// </summary>
    private static void SamePathTolerance()
    {
        Check.That(
            GitStatusSnapshot.IsSamePath(@"D:\work\demo", @"D:\work\demo\"),
            "尾分隔符不同应视为同一目录");

        Check.That(
            GitStatusSnapshot.IsSamePath(@"D:\work\demo", "D:/work/demo"),
            "分隔符风格不同应视为同一目录");

        Check.That(
            GitStatusSnapshot.IsSamePath(@"D:\work\demo", @"d:\WORK\Demo"),
            "Windows 上大小写不同应视为同一目录");

        Check.That(
            !GitStatusSnapshot.IsSamePath(@"D:\work\demo", @"D:\work\demo2"),
            "前缀相同的不同目录不算同一目录");

        Check.That(
            !GitStatusSnapshot.IsSamePath(@"D:\work\demo", @"D:\work"),
            "父目录不等于子目录");
    }

    private static void ParsesCleanRepository()
    {
        var status = GitStatusSnapshot.FromOutputs("main\n", "");

        Check.That(status.IsRepository, "有分支输出即视为仓库");
        Check.Equal("main", status.Branch, "分支名应去掉换行");
        Check.Equal(0, status.ChangedFiles, "空 porcelain 应为 0 处改动");
        Check.That(status.Error is null, "正常状态不应带错误");
    }

    private static void CountsChangedFiles()
    {
        var status = GitStatusSnapshot.FromOutputs(
            "feature/x\r\n",
            " M src/App.cs\r\n?? notes.txt\r\n\r\n");

        Check.Equal("feature/x", status.Branch, "CRLF 输出也应正确去空白");
        Check.Equal(2, status.ChangedFiles, "应统计已跟踪改动与未跟踪文件，忽略空行");
    }

    private static void HandlesEmptyBranch()
    {
        var status = GitStatusSnapshot.FromOutputs("\n\n", " M a.cs\n");

        Check.Equal("未知分支", status.Branch, "分支输出为空时应回退为『未知分支』而不是抛异常");
        Check.Equal(1, status.ChangedFiles, "改动数仍应统计");
    }

    private static void SummaryTexts()
    {
        Check.Equal(
            "未初始化 Git",
            GitStatusSnapshot.NotRepository().Summary,
            "非仓库的摘要");

        Check.Equal(
            "main · 工作区干净",
            GitStatusSnapshot.FromOutputs("main", string.Empty).Summary,
            "干净仓库的摘要");

        Check.Equal(
            "dev · 3 处未提交改动",
            GitStatusSnapshot.FromOutputs("dev", " M a\n M b\n?? c\n").Summary,
            "有改动的摘要");

        Check.Equal(
            "Git 状态不可用：目录不存在：D:\\nope",
            GitStatusSnapshot.Unavailable("目录不存在：D:\\nope").Summary,
            "不可用时应把原因带给用户");
    }

    /// <summary>真机链路：init → 查状态 → 有改动 → 全部提交后变干净。没有 git 就跳过。</summary>
    private static void RealRepositoryRoundTrip()
    {
        var git = new GitRunner();
        if (!git.IsAvailableAsync().GetAwaiter().GetResult())
        {
            Console.WriteLine("  · 已跳过真 git 链路（本机无 git）");
            return;
        }

        using var workspace = new TempWorkspace("git-status");
        var init = git.InitAsync(workspace.Root).GetAwaiter().GetResult();
        Check.That(init.Success, $"git init 应成功，实际：{init.Message}");

        File.WriteAllText(Path.Combine(workspace.Root, "README.md"), "# demo");

        var dirty = git.GetStatusAsync(workspace.Root).GetAwaiter().GetResult();
        Check.That(dirty.IsRepository, "init 后应识别为仓库");
        Check.Equal("main", dirty.Branch, "init -b main 后分支应为 main");
        Check.Equal(1, dirty.ChangedFiles, "新增 README 应计为 1 处改动");

        // 非仓库目录
        using var plain = new TempWorkspace("git-plain");
        var none = git.GetStatusAsync(plain.Root).GetAwaiter().GetResult();
        Check.That(!none.IsRepository, "普通目录不应被识别为仓库");
        Check.Equal("未初始化 Git", none.Summary, "普通目录的摘要应为未初始化 Git");
    }

    /// <summary>
    /// 真缺陷回归：项目放在别的仓库目录里（monorepo 子目录、或本项目自己被当成演示项目打开时），
    /// <c>rev-parse --is-inside-work-tree</c> 会返回 true，而上层的分支与改动数会被当成本项目的状态。
    /// 必须靠 <c>--show-toplevel</c> 比对识别出来。
    /// </summary>
    private static void DoesNotInheritParentRepository()
    {
        var git = new GitRunner();
        if (!git.IsAvailableAsync().GetAwaiter().GetResult())
        {
            Console.WriteLine("  · 已跳过上层仓库用例（本机无 git）");
            return;
        }

        using var workspace = new TempWorkspace("git-parent");
        var init = git.InitAsync(workspace.Root).GetAwaiter().GetResult();
        Check.That(init.Success, $"上层仓库 init 应成功，实际：{init.Message}");

        // 让上层仓库变脏：如果实现有缺陷，这个改动数会漏到子目录的查询结果里
        File.WriteAllText(Path.Combine(workspace.Root, "parent-dirty.txt"), "x");

        var child = workspace.NewTarget("packages/app");
        var snapshot = git.GetStatusAsync(child).GetAwaiter().GetResult();

        Check.That(!snapshot.IsRepository, "子目录不应被识别为独立仓库");
        Check.That(snapshot.ParentRepository is not null, "应记录上层仓库根以便向用户解释");
        Check.That(
            snapshot.ParentRepository is not null
                && GitStatusSnapshot.IsSamePath(snapshot.ParentRepository, workspace.Root),
            "上层仓库根应指向真实顶层目录");
        Check.Equal(0, snapshot.ChangedFiles, "不得继承上层仓库的改动数");
        Check.Equal(string.Empty, snapshot.Branch, "不得冒用上层仓库的分支名");

        var summary = snapshot.Summary;
        Check.That(
            summary.Contains("位于上层仓库", StringComparison.Ordinal),
            $"摘要应说明原因，实际：{summary}");
        Check.That(
            summary.Contains(Path.GetFileName(workspace.Root), StringComparison.Ordinal),
            $"摘要应带上层仓库名，实际：{summary}");
    }

    /// <summary>反之：子目录里自己 <c>git init</c> 过的，必须认成独立仓库。</summary>
    private static void NestedRepositoryWins()
    {
        var git = new GitRunner();
        if (!git.IsAvailableAsync().GetAwaiter().GetResult())
        {
            Console.WriteLine("  · 已跳过嵌套仓库用例（本机无 git）");
            return;
        }

        using var workspace = new TempWorkspace("git-nested");
        Check.That(git.InitAsync(workspace.Root).GetAwaiter().GetResult().Success, "外层 init 应成功");

        var nestedRoot = workspace.NewTarget("nested");
        Check.That(git.InitAsync(nestedRoot).GetAwaiter().GetResult().Success, "内层 init 应成功");

        var snapshot = git.GetStatusAsync(nestedRoot).GetAwaiter().GetResult();

        Check.That(snapshot.IsRepository, "自建仓库的子目录应识别为独立仓库");
        Check.That(snapshot.ParentRepository is null, "独立仓库不应带上层仓库标记");
        Check.Equal("main", snapshot.Branch, "应读到内层仓库的分支");
    }
}
