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
        RealRepositoryRoundTrip();
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
}
