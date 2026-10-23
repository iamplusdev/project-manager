namespace ProjectManager.CliInterop;

/// <summary>读历史的能力。与写能力分开：历史是只读的，失败只需一句提示，不该和「留下半个索引」共享错误处理。</summary>
public interface IGitHistoryRunner
{
    /// <summary>
    /// 读一页提交历史（新到旧）。<paramref name="limit"/> 是**本页最多几条**，
    /// <paramref name="skip"/> 是已经读过多少条（首页传 0）。
    /// </summary>
    Task<GitHistoryPage> GetHistoryAsync(
        string repositoryRoot,
        int limit,
        int skip = 0,
        CancellationToken cancellationToken = default);

    /// <summary>读单条提交的完整信息与文件改动统计。</summary>
    Task<GitCommitDetail> GetCommitDetailAsync(
        string repositoryRoot,
        GitCommitEntry commit,
        CancellationToken cancellationToken = default);
}

/// <summary>一页提交历史。空态原因与错误分开记，界面才能给出不同的下一步建议。</summary>
public sealed record GitHistoryPage
{
    public bool IsRepository { get; init; }

    /// <summary>仓库存在但 HEAD 还没有任何提交（刚 <c>init</c>）。</summary>
    public bool HasNoCommits { get; init; }

    /// <summary>查询失败的原因（目录不存在、git 不可用、位于上层仓库内等）。</summary>
    public string? Error { get; init; }

    /// <summary>本页的提交，新到旧。</summary>
    public IReadOnlyList<GitCommitEntry> Commits { get; init; } = Array.Empty<GitCommitEntry>();

    /// <summary>后面还有更多（本次取回的行数超过请求的条数）。</summary>
    public bool HasMore { get; init; }

    /// <summary>当前已加载的总数，作为下一次的 skip。</summary>
    public int LoadedCount { get; init; }

    public static GitHistoryPage Unavailable(string error) => new() { IsRepository = false, Error = error };

    public static GitHistoryPage NotRepository() => new() { IsRepository = false };

    public static GitHistoryPage Empty() => new() { IsRepository = true, HasNoCommits = true };
}

public sealed partial class GitRunner
{
    /// <summary>单页上限。一次要一百条以上说明用户在翻旧账，用「加载更多」比一次全取更省事也更省内存。</summary>
    public const int MaxHistoryPageSize = 200;

    public async Task<GitHistoryPage> GetHistoryAsync(
        string repositoryRoot,
        int limit,
        int skip = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        if (!Directory.Exists(repositoryRoot))
        {
            return GitHistoryPage.Unavailable($"目录不存在：{repositoryRoot}");
        }

        var probe = await ProbeRepositoryAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (probe.State != GitProbeState.Repository)
        {
            // 子目录要说清是"在别人的仓库里"：用户看到空历史会以为仓库是空的，
            // 而事实是他的提交记在上层仓库的账上
            return probe.State switch
            {
                GitProbeState.ParentRepository => GitHistoryPage.Unavailable(
                    $"未独立初始化 Git · 位于上层仓库 {probe.ParentRoot} 内"),
                GitProbeState.Unavailable => GitHistoryPage.Unavailable(probe.Error ?? "Git 不可用。"),
                _ => GitHistoryPage.NotRepository(),
            };
        }

        // 先用 rev-parse 判"有没有提交"：空仓库上 git log 会以 128 退出并打一句
        // "your current branch 'x' does not have any commits yet"，
        // 但那句话是**本地化**的，靠匹配文案判空态在中文 git 上必然失效
        var head = await RunAsync(new[] { "rev-parse", "--verify", "--quiet", "HEAD" }, repositoryRoot, cancellationToken)
            .ConfigureAwait(false);
        if (!head.Success)
        {
            return GitHistoryPage.Empty();
        }

        var clampedLimit = Math.Clamp(limit, 1, MaxHistoryPageSize);
        var clampedSkip = Math.Max(skip, 0);

        // 多取一条用来判「还有更多」，避免为了一个布尔值再跑一次 git
        var arguments = new List<string>
        {
            "log",
            "--no-color",
            "--no-show-signature",
            "--decorate=full",
            $"--max-count={clampedLimit + 1}",
            $"--skip={clampedSkip}",
            $"--format={GitHistoryParser.LogFormat}",
        };

        var result = await RunAsync(arguments, repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return GitHistoryPage.Unavailable(result.Message.Length > 0 ? result.Message : "读取提交历史失败。");
        }

        var parsed = GitHistoryParser.ParseLog(result.StandardOutput);
        var hasMore = parsed.Count > clampedLimit;
        var page = hasMore ? parsed.Take(clampedLimit).ToList() : parsed;

        return new GitHistoryPage
        {
            IsRepository = true,
            Commits = page,
            HasMore = hasMore,
            LoadedCount = clampedSkip + page.Count,
        };
    }

    public async Task<GitCommitDetail> GetCommitDetailAsync(
        string repositoryRoot,
        GitCommitEntry commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(commit);

        // 只接受十六进制摘要：这个值会被拼进命令行，虽然我们从不经 shell，
        // 但一个形如 `--upload-pack=…` 的字符串仍会被 git 当成选项
        if (!IsCommitHash(commit.Hash))
        {
            return new GitCommitDetail { Commit = commit, Error = "提交标识不合法。" };
        }

        // --format=%B%x00：先给完整信息，再给 numstat。一次进程拿全，避免两次调用之间
        // 仓库状态变化导致正文与文件清单对不上。
        //
        // 合并提交的 numstat 口径必须钉死，否则"这次提交改了几行"没有唯一答案：
        //   · 不加任何选项：现代 git（实测 2.55）等价于"对第一父提交求差异"；
        //   · 加 -m：每个父提交各列一遍，同一文件出现两行，数字直接翻倍；
        //   · 加 --first-parent：显式声明口径，且**让 -m 失效**（两者同时给时只展开第一父）。
        // 所以这里两个都要：--first-parent 说明口径，同时把"后人顺手加 -m"这条错误路径堵死。
        var arguments = new List<string>
        {
            "-c",
            "core.quotepath=false",
            "show",
            "--no-color",
            "--no-ext-diff",
            "--numstat",
            "--first-parent",
            "--format=%B%x00",
            commit.Hash,
        };

        var result = await RunAsync(arguments, repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return new GitCommitDetail
            {
                Commit = commit,
                Error = result.Message.Length > 0 ? result.Message : "读取提交详情失败。",
            };
        }

        return GitHistoryParser.ParseDetail(result.StandardOutput, commit);
    }

    /// <summary>
    /// 提交摘要：7~64 位十六进制。比这更短的缩写 git 自己也会拒绝。
    /// </summary>
    /// <remarks>
    /// 公开是为了能直接断言：这个判断是"别把用户可控字符串当选项传给 git"的唯一闸门，
    /// 靠集成测试去覆盖它，得先造一个能返回恶意字符串的仓库——造不出来。
    /// </remarks>
    public static bool IsCommitHash(string? hash)
    {
        if (hash is null || hash.Length is < 7 or > 64)
        {
            return false;
        }

        foreach (var ch in hash)
        {
            if (!char.IsAsciiHexDigit(ch))
            {
                return false;
            }
        }

        return true;
    }
}
