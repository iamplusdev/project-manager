using ProjectManager.CliInterop;

namespace ProjectManager.Application.Git;

/// <summary>工作区的展示态：把 git 的原始清单整理成"用户该按什么顺序看"。</summary>
public sealed record GitWorkspaceView
{
    public required GitWorkingTree Tree { get; init; }

    /// <summary>排序后的变更清单：冲突最前，其次有暂存内容的，再次只有工作区改动的，最后未跟踪。</summary>
    public IReadOnlyList<GitChangeEntry> Changes => GitWorkspaceService.OrderForDisplay(Tree.Changes);

    public bool IsRepository => Tree.IsRepository;

    public bool HasChanges => Changes.Count > 0;

    public string Summary => Tree.Summary;

    /// <summary>空清单时给一句话解释为什么空（而不是留白）。</summary>
    public string EmptyHint
    {
        get
        {
            if (Tree.Error is not null)
            {
                return Tree.Error;
            }

            if (!Tree.IsRepository)
            {
                return "这个目录还不是独立仓库。生成项目时勾选「初始化 Git」，或在项目页对目录执行 git init。";
            }

            return "工作区没有未提交的改动。";
        }
    }

    public static GitWorkspaceView From(GitWorkingTree tree) => new() { Tree = tree };
}

/// <summary>提交结果：成功 / 失败都带一句可直接展示的话。</summary>
public sealed record GitCommitOutcome(bool Succeeded, string Message, string Hash)
{
    public static GitCommitOutcome Failed(string message) => new(false, message, string.Empty);
}

/// <summary>
/// Git 工作台的编排层。
/// </summary>
/// <remarks>
/// 放在应用层而不是直接让视图模型调 <see cref="IGitWorkspaceRunner"/>，是为了把三件事集中起来：
/// ① 提交前校验（空标题、索引区为空）；
/// ② 提交后**回读**工作区，确认索引真的被清掉（hook 可能重建索引，退出码 0 不代表干净）；
/// ③ 清单排序规则——它是纯函数，可以单测，不该埋在 XAML 的 CollectionViewSource 里。
/// </remarks>
public sealed class GitWorkspaceService
{
    /// <summary>标题上限。git 本身不限，但整段文字粘进来会把提交历史糊掉，拦在这里。</summary>
    public const int MaxTitleLength = 200;

    private readonly IGitWorkspaceRunner _runner;

    public GitWorkspaceService(IGitWorkspaceRunner runner) => _runner = runner;

    public async Task<GitWorkspaceView> LoadAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        var tree = await _runner.GetWorkingTreeAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        return GitWorkspaceView.From(tree);
    }

    /// <summary>
    /// 取差异。<paramref name="staged"/> 优先取索引区——UI 默认看"我准备提交什么"，
    /// 而不是"我还没加进去什么"。
    /// </summary>
    public async Task<FileDiff> LoadDiffAsync(
        string repositoryRoot,
        GitChangeEntry change,
        bool? staged = null,
        CancellationToken cancellationToken = default)
    {
        // 未跟踪文件在 git 眼里没有"旧版本"，diff 必然为空；直接给空 diff 并说明，
        // 不去凑 --no-index（Windows 上依赖 NUL 设备与退出码 1，为预览引入平台差异不值）
        if (change.Staged == GitChangeKind.Untracked || change.Unstaged == GitChangeKind.Untracked)
        {
            return new FileDiff { Path = change.Path, IsNew = true, Error = null };
        }

        // 未指定时：有暂存内容就看索引区，否则看工作区
        var useStaged = staged ?? change.HasStagedChanges;

        return await _runner.GetDiffAsync(repositoryRoot, change.Path, useStaged, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>暂存/取消暂存。返回可展示的结果文案。</summary>
    public async Task<GitCommandResult> SetStagedAsync(
        string repositoryRoot,
        IReadOnlyList<string> paths,
        bool staged,
        CancellationToken cancellationToken = default)
    {
        return staged
            ? await _runner.StageAsync(repositoryRoot, paths, cancellationToken).ConfigureAwait(false)
            : await _runner.UnstageAsync(repositoryRoot, paths, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 提交。校验 → 执行 → 回读三段式。
    /// </summary>
    public async Task<GitCommitOutcome> CommitAsync(
        string repositoryRoot,
        string title,
        string? body,
        CancellationToken cancellationToken = default)
    {
        var trimmed = title?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return GitCommitOutcome.Failed("请填写提交标题。");
        }

        if (trimmed.Length > MaxTitleLength)
        {
            return GitCommitOutcome.Failed($"提交标题过长（{trimmed.Length} 字），请控制在 {MaxTitleLength} 字以内。");
        }

        // 先确认索引区真有内容：直接 commit 会得到 git 自己的一句 "nothing to commit"，
        // 但那句话在中文环境下可能是本地化的、也可能被 hook 干扰，自己判更稳
        var before = await _runner.GetWorkingTreeAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (!before.IsRepository)
        {
            return GitCommitOutcome.Failed(before.Error ?? "当前目录不是 Git 仓库。");
        }

        if (before.StagedCount == 0)
        {
            return GitCommitOutcome.Failed("索引区没有内容可提交，请先勾选文件并暂存。");
        }

        var result = await _runner.CommitAsync(repositoryRoot, trimmed, body, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return GitCommitOutcome.Failed(result.Message);
        }

        // 回读：hook 可以在提交后重新改索引（例如格式化后重新 add），
        // 所以"退出码 0"不等于"索引干净"，要拿事实说话
        var after = await _runner.GetWorkingTreeAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var message = result.Describe();

        if (after.IsRepository && after.StagedCount > 0)
        {
            message += $"（索引区仍有 {after.StagedCount} 项待提交）";
        }

        return new GitCommitOutcome(true, message, result.Hash);
    }

    /// <summary>
    /// 清单排序：冲突 → 已暂存 → 未暂存 → 未跟踪；同类按路径序数比较。
    /// </summary>
    /// <remarks>
    /// 冲突排最前是因为它**阻塞提交**：只要有一个未解决冲突，git 就不让 commit。
    /// 顺序必须稳定（不依赖 git 输出顺序），否则每次刷新列表都在跳，用户勾选会勾错行。
    /// </remarks>
    public static IReadOnlyList<GitChangeEntry> OrderForDisplay(IReadOnlyList<GitChangeEntry> changes)
    {
        if (changes.Count == 0)
        {
            return Array.Empty<GitChangeEntry>();
        }

        return changes
            .OrderBy(Rank)
            .ThenBy(change => change.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int Rank(GitChangeEntry change)
    {
        if (change.IsConflicted)
        {
            return 0;
        }

        if (change.Staged == GitChangeKind.Untracked || change.Unstaged == GitChangeKind.Untracked)
        {
            return 3;
        }

        return change.HasStagedChanges ? 1 : 2;
    }

    /// <summary>
    /// 勾选文件的公共前缀目录，作为提交标题的默认建议；没有公共前缀就返回空串。
    /// </summary>
    /// <remarks>
    /// 只是**建议**：用户不写标题时这里给一个起点，写错了也没有后果。
    /// 因此刻意保守——算不出公共父目录时宁可留空，也好过编一个"更新项目"这种没有信息量的标题。
    /// </remarks>
    public static string SuggestTitle(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return string.Empty;
        }

        if (paths.Count == 1)
        {
            return Path.GetFileName(paths[0].TrimEnd('/'));
        }

        var segments = paths
            .Select(path => path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        var common = new List<string>();

        // 最后一段是文件名，不参与公共前缀比较
        var shortest = segments.Min(parts => parts.Length) - 1;
        for (var i = 0; i < shortest; i++)
        {
            var candidate = segments[0][i];
            if (!segments.All(parts => string.Equals(parts[i], candidate, StringComparison.OrdinalIgnoreCase)))
            {
                break;
            }

            common.Add(candidate);
        }

        return common.Count == 0 ? string.Empty : $"更新 {common[^1]}";
    }
}
