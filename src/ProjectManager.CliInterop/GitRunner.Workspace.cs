using System.Collections.Concurrent;
using System.Globalization;

namespace ProjectManager.CliInterop;

/// <summary>
/// 工作区读写能力。与 <see cref="IGitRunner"/>（探测与 init）分开，
/// 是因为这两组操作的**失败后果不同**：探测失败只是"状态没测到"，写操作失败可能留下半个索引。
/// </summary>
public interface IGitWorkspaceRunner
{
    /// <summary>读取变更清单（含分支名）。不落库、不缓存，每次现查。</summary>
    Task<GitWorkingTree> GetWorkingTreeAsync(string repositoryRoot, CancellationToken cancellationToken = default);

    /// <summary>取单个文件的差异。<paramref name="staged"/> 为 true 时看索引区（暂存了什么）。</summary>
    Task<FileDiff> GetDiffAsync(string repositoryRoot, string path, bool staged, CancellationToken cancellationToken = default);

    /// <summary>把指定路径加入索引区。</summary>
    Task<GitCommandResult> StageAsync(string repositoryRoot, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>把指定路径移出索引区（不回退工作区内容）。</summary>
    Task<GitCommandResult> UnstageAsync(string repositoryRoot, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>提交索引区内容。<paramref name="body"/> 为空时只写标题。</summary>
    Task<GitCommitResult> CommitAsync(string repositoryRoot, string title, string? body, CancellationToken cancellationToken = default);
}

/// <summary>工作区快照：分支 + 变更清单。</summary>
public sealed record GitWorkingTree
{
    public bool IsRepository { get; init; }

    public string Branch { get; init; } = string.Empty;

    /// <summary>非空表示查询失败的原因（目录不存在、git 不可用等）。</summary>
    public string? Error { get; init; }

    public IReadOnlyList<GitChangeEntry> Changes { get; init; } = Array.Empty<GitChangeEntry>();

    /// <summary>有内容可提交的条目数。</summary>
    public int StagedCount => Changes.Count(change => change.HasStagedChanges);

    /// <summary>还要再暂存的条目数。</summary>
    public int UnstagedCount => Changes.Count(change => change.HasUnstagedChanges);

    public bool HasChanges => Changes.Count > 0;

    public static GitWorkingTree Unavailable(string error) => new() { IsRepository = false, Error = error };

    public static GitWorkingTree NotRepository() => new() { IsRepository = false };

    /// <summary>页面顶部一行摘要。</summary>
    public string Summary
    {
        get
        {
            if (Error is not null)
            {
                return $"Git 状态不可用：{Error}";
            }

            if (!IsRepository)
            {
                return "未初始化 Git";
            }

            if (Changes.Count == 0)
            {
                return $"{Branch} · 工作区干净";
            }

            return $"{Branch} · {Changes.Count} 个文件有改动（已暂存 {StagedCount}）";
        }
    }
}

/// <summary>一次提交的结果。<see cref="Subject"/> 是**回读**到的实际标题，不是用户输入。</summary>
public sealed record GitCommitResult(bool Success, string Hash, string Subject, string Message)
{
    public static GitCommitResult Failed(string message) => new(false, string.Empty, string.Empty, message);

    /// <summary>成功文案。短 hash 只取前 7 位，与 git 自己的习惯一致。</summary>
    public string Describe() => Success
        ? $"已提交 {ShortHash}：{Subject}"
        : Message;

    public string ShortHash => Hash.Length > 7 ? Hash[..7] : Hash;
}

public sealed partial class GitRunner
{
    /// <summary>仓库探测的四种结果。调用方对它们的处理不同，所以不能压成 bool。</summary>
    internal enum GitProbeState
    {
        /// <summary>本目录就是仓库根。</summary>
        Repository,

        /// <summary>不是仓库。</summary>
        NotRepository,

        /// <summary>本目录在某个上层仓库里（工作台不能对它动手，否则会改到别人的仓库）。</summary>
        ParentRepository,

        /// <summary>探测本身失败：git 不可用、目录读不了。</summary>
        Unavailable,
    }

    internal readonly record struct GitProbe(GitProbeState State, string? Error = null, string? ParentRoot = null);

    /// <summary>
    /// 判断「本目录是不是一个独立仓库的根」。
    /// </summary>
    /// <remarks>
    /// <c>rev-parse --is-inside-work-tree</c> 是**向上查找**的：任何位于某个仓库子目录下的路径都会返回 true。
    /// 只问这一句，就会把"项目放在别人的仓库里"误判成"项目自己是仓库"——变更页会把上层仓库的所有改动
    /// 当成本项目的，暂存/提交更会直接作用到那个上层仓库上。所以必须再比 <c>--show-toplevel</c>。
    /// </remarks>
    private async Task<GitProbe> ProbeRepositoryAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(repositoryRoot))
        {
            return new GitProbe(GitProbeState.Unavailable, $"目录不存在：{repositoryRoot}");
        }

        var inside = await RunAsync(new[] { "rev-parse", "--is-inside-work-tree" }, repositoryRoot, cancellationToken)
            .ConfigureAwait(false);
        if (!inside.Success || !inside.StandardOutput.Contains("true", StringComparison.OrdinalIgnoreCase))
        {
            return new GitProbe(GitProbeState.NotRepository);
        }

        var topLevel = await RunAsync(new[] { "rev-parse", "--show-toplevel" }, repositoryRoot, cancellationToken)
            .ConfigureAwait(false);
        if (!topLevel.Success)
        {
            return new GitProbe(GitProbeState.NotRepository);
        }

        var root = topLevel.StandardOutput.Trim();
        if (root.Length > 0 && !GitStatusSnapshot.IsSamePath(root, repositoryRoot))
        {
            return new GitProbe(GitProbeState.ParentRepository, null, root);
        }

        return new GitProbe(GitProbeState.Repository);
    }

    /// <summary>
    /// 每仓库一把写锁。设计方案 §2.1 的"双引擎互斥"在本项目退化为"同进程并发写同一 index"：
    /// 两个 <c>git add</c> 同时跑会撞 <c>index.lock</c>，所以写操作必须串行。
    /// 锁按**仓库根**分配而非全局一把——全局锁会让多仓库操作互相排队，仓库越多越慢。
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<GitWorkingTree> GetWorkingTreeAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var probe = await ProbeRepositoryAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);

        if (probe.State == GitProbeState.Unavailable)
        {
            return GitWorkingTree.Unavailable(probe.Error ?? "Git 不可用。");
        }

        // 子目录与"不是仓库"在工作台上是同一种处境：不能在这里动手
        if (probe.State != GitProbeState.Repository)
        {
            return GitWorkingTree.NotRepository();
        }

        var branch = await RunAsync(new[] { "symbolic-ref", "--short", "HEAD" }, repositoryRoot, cancellationToken)
            .ConfigureAwait(false);
        var branchText = branch.Success
            ? branch.StandardOutput.Trim()
            : (await RunAsync(new[] { "rev-parse", "--abbrev-ref", "HEAD" }, repositoryRoot, cancellationToken)
                .ConfigureAwait(false)).StandardOutput.Trim();

        // porcelain v2 + -z：不转义路径（中文、空格安全），重命名源路径独立成段
        // --untracked-files=all：不折叠未跟踪目录。工作台的用途就是"挑文件提交"，
        // 只给一个 `docs/` 等于让用户要么全提交要么全不提交，没法挑单个文件
        var status = await RunAsync(
            new[] { "status", "--porcelain=v2", "-z", "--untracked-files=all" },
            repositoryRoot,
            cancellationToken).ConfigureAwait(false);

        if (!status.Success)
        {
            return GitWorkingTree.Unavailable(status.Message);
        }

        return new GitWorkingTree
        {
            IsRepository = true,
            Branch = branchText.Length > 0 ? branchText : "未知分支",
            Changes = GitStatusParser.ParsePorcelainV2(status.StandardOutput),
        };
    }

    public async Task<FileDiff> GetDiffAsync(
        string repositoryRoot,
        string path,
        bool staged,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // core.quotepath=false：默认会把非 ASCII 路径输出成 \346\226\207 这样的八进制转义，
        // 头部路径虽不影响正文，但用户会看到一坨乱码，白给的坑不踩
        var arguments = new List<string> { "-c", "core.quotepath=false", "diff", "--no-color", "--no-ext-diff" };
        if (staged)
        {
            arguments.Add("--cached");
        }

        arguments.Add("--");
        arguments.Add(path);

        var result = await RunAsync(arguments, repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return EmptyDiff(path, result.Message);
        }

        var parsed = UnifiedDiffParser.Parse(result.StandardOutput);
        if (parsed.Count == 0)
        {
            // 未跟踪文件：git diff 不输出内容（要 --no-index 才行），如实告诉调用方"没有差异可看"
            return EmptyDiff(path, null);
        }

        // 只取第一个：一次只请求一个文件的 diff，多段说明路径过滤没生效，取其首段仍是对的
        var diff = parsed[0];

        // 用请求路径校正头部解析结果：路径含引号/空格时 git 会给头部加引号，
        // 解析出来带引号也不影响正文，但 UI 标题会难看
        return diff.Path == path ? diff : diff with { Path = path };
    }

    public async Task<GitCommandResult> StageAsync(
        string repositoryRoot,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        // 空列表直接返回成功：UI 上"什么都没选就点暂存"不该报错
        if (paths.Count == 0)
        {
            return new GitCommandResult(true, 0, string.Empty, string.Empty);
        }

        var arguments = new List<string> { "add", "--" };
        arguments.AddRange(paths);

        return await RunWriteAsync(repositoryRoot, arguments, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitCommandResult> UnstageAsync(
        string repositoryRoot,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0)
        {
            return new GitCommandResult(true, 0, string.Empty, string.Empty);
        }

        var hasHead = await RunAsync(new[] { "rev-parse", "--verify", "--quiet", "HEAD" }, repositoryRoot, cancellationToken)
            .ConfigureAwait(false);

        var arguments = UnstageArguments(hasHead.Success).ToList();
        arguments.AddRange(paths);

        return await RunWriteAsync(repositoryRoot, arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 取消暂存要执行的 git 参数（不含路径）。
    /// </summary>
    /// <remarks>
    /// 有 HEAD 时 <c>reset</c> 把索引恢复到 HEAD，工作区不动——这是通用做法。
    /// 零提交仓库（刚 init、还没 commit 过）在老版本 git 上 <c>reset</c> 会报
    /// <c>Failed to resolve 'HEAD'</c>，只能退回 <c>rm --cached</c> 把条目从索引摘掉。
    /// 实测 git 2.55 上的 <c>reset -- path</c> 已能处理无 HEAD 的情况，所以这条兜底
    /// 是**为老版本留的**，不是空分支：参数构造单独抽出来就是为了让它可断言。
    /// </remarks>
    public static IReadOnlyList<string> UnstageArguments(bool hasHead)
        => hasHead
            ? new[] { "reset", "-q", "--" }
            : new[] { "rm", "--cached", "-q", "-r", "--" };

    public async Task<GitCommitResult> CommitAsync(
        string repositoryRoot,
        string title,
        string? body,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return GitCommitResult.Failed("提交标题不能为空。");
        }

        // 提交一律走 CLI：LibGit2Sharp 的提交路径**不执行** pre-commit / commit-msg hook，
        // 用它会让仓库自己的规范静默失效（《软件设计方案》§2.1 红约束）
        var arguments = new List<string> { "commit", "-m", title.Trim() };
        if (!string.IsNullOrWhiteSpace(body))
        {
            arguments.Add("-m");
            arguments.Add(body.Trim());
        }

        var result = await RunWriteAsync(repositoryRoot, arguments, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            // hook 拒绝时 git 把理由写在 stderr（或 stdout），原样交给用户——
            // 替他改写成"提交失败"会丢掉唯一能让他知道该怎么改的信息
            return GitCommitResult.Failed(result.Message.Length > 0 ? result.Message : "提交失败。");
        }

        // 回读：hook 可以改写提交信息，所以不能把用户输入当结果
        var log = await RunAsync(new[] { "log", "-1", "--format=%H%n%s" }, repositoryRoot, cancellationToken)
            .ConfigureAwait(false);

        if (!log.Success)
        {
            return new GitCommitResult(true, string.Empty, title.Trim(), "已提交，但读取提交信息失败。");
        }

        var lines = log.StandardOutput.Replace("\r\n", "\n").Split('\n');
        var hash = lines.Length > 0 ? lines[0].Trim() : string.Empty;
        var subject = lines.Length > 1 ? lines[1].Trim() : title.Trim();

        return new GitCommitResult(true, hash, subject, log.StandardOutput.Trim());
    }

    /// <summary>写操作统一入口：按仓库根取锁 → 执行 → 释放。</summary>
    private async Task<GitCommandResult> RunWriteAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        if (!Directory.Exists(repositoryRoot))
        {
            return GitCommandResult.ToolMissing($"目录不存在：{repositoryRoot}");
        }

        var key = NormalizeLockKey(repositoryRoot);
        var gate = _writeLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunAsync(arguments, repositoryRoot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>锁键归一：同一仓库用不同写法（尾分隔符、大小写、斜杠方向）必须落到同一把锁上。</summary>
    internal static string NormalizeLockKey(string repositoryRoot)
        => Path.TrimEndingDirectorySeparator(repositoryRoot)
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .ToUpperInvariant();

    private static FileDiff EmptyDiff(string path, string? error) => new()
    {
        Path = path,
        Error = error,
        Hunks = Array.Empty<DiffHunk>(),
    };
}
