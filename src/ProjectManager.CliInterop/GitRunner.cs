using System.Diagnostics;
using System.Text;

namespace ProjectManager.CliInterop;

/// <summary>一次命令执行结果。<see cref="Message"/> 已归一为可直接展示的文案。</summary>
public sealed record GitCommandResult(bool Success, int ExitCode, string StandardOutput, string StandardError)
{
    public string Message
    {
        get
        {
            if (!Success && StandardError.Trim().Length > 0)
            {
                return StandardError.Trim();
            }

            return StandardOutput.Trim();
        }
    }

    public static GitCommandResult ToolMissing(string reason) => new(false, -1, string.Empty, reason);
}

public interface IGitRunner
{
    /// <summary>探测 <c>git</c> 是否可用（结果缓存，避免每次生成都起进程）。</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>在指定目录执行 <c>git init</c>。目录已存在仓库时为幂等重初始化，不算失败。</summary>
    Task<GitCommandResult> InitAsync(string workingDirectory, CancellationToken cancellationToken = default);

    /// <summary>读取仓库实时状态（分支 + 改动文件数）。**不落库**，每次现查。</summary>
    Task<GitStatusSnapshot> GetStatusAsync(string workingDirectory, CancellationToken cancellationToken = default);
}

/// <summary>
/// 仓库状态快照。刻意不带 ahead/behind：那需要联网或至少本地 ref 比较，
/// 详情页只要"是不是仓库、在哪个分支、有几处改动"就够，避免打开项目就触发网络。
/// </summary>
public sealed record GitStatusSnapshot(bool IsRepository, string Branch, int ChangedFiles, string? Error)
{
    /// <summary>
    /// 项目目录位于**上层仓库**内时的那个上层仓库根（例如打开 monorepo 里的一个子目录）。
    /// 非空表示"这里不是独立仓库"，不能拿上层的分支与改动数冒充本项目的状态。
    /// </summary>
    public string? ParentRepository { get; init; }

    public static GitStatusSnapshot NotRepository() => new(false, string.Empty, 0, null);

    public static GitStatusSnapshot Unavailable(string error) => new(false, string.Empty, 0, error);

    /// <summary>位于上层仓库内的快照：仍然是"未初始化"，但要说明原因，否则用户会以为自家仓库没了。</summary>
    public static GitStatusSnapshot InParentRepository(string repositoryRoot) => new(false, string.Empty, 0, null)
    {
        ParentRepository = repositoryRoot,
    };

    /// <summary>详情页一行摘要。</summary>
    public string Summary
    {
        get
        {
            if (ParentRepository is not null)
            {
                var name = Path.GetFileName(
                    Path.TrimEndingDirectorySeparator(ParentRepository));
                return string.IsNullOrEmpty(name)
                    ? "未独立初始化 Git（位于上层仓库内）"
                    : $"未独立初始化 Git · 位于上层仓库 {name} 内";
            }

            if (!IsRepository)
            {
                return Error is null ? "未初始化 Git" : $"Git 状态不可用：{Error}";
            }

            return ChangedFiles == 0 ? $"{Branch} · 工作区干净" : $"{Branch} · {ChangedFiles} 处未提交改动";
        }
    }

    /// <summary>两个路径是否指向同一目录（忽略大小写、尾分隔符与分隔符风格）。</summary>
    public static bool IsSamePath(string left, string right)
    {
        static string Normalize(string value)
            => Path.TrimEndingDirectorySeparator(value.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));

        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从命令输出解析状态。抽成静态方法是为了让解析规则（空行、CRLF、未提交仓库）可单测，
    /// 不必为每条规则真的去起一个 git 进程。
    /// </summary>
    public static GitStatusSnapshot FromOutputs(string branchOutput, string statusOutput)
    {
        var branch = branchOutput.Trim();
        if (branch.Length == 0)
        {
            branch = "未知分支";
        }

        var changed = statusOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.Trim().Length > 0);

        return new GitStatusSnapshot(true, branch, changed, null);
    }
}

/// <summary>
/// Git CLI 封装（双引擎架构的 CLI 侧）。M2 只需要 <c>git init</c>；
/// 后续 Git 工作台复用这里的进程封装约定：超时看门狗、UTF-8 输出、错误归一。
/// </summary>
/// <remarks>
/// 分部类拆在 <c>GitRunner.Workspace.cs</c>：工作区读写（status/diff/add/commit）与
/// 仓库探测的失败语义不同，分文件更利于单独审视"写操作是否安全"。
/// </remarks>
public sealed partial class GitRunner : IGitRunner, IGitWorkspaceRunner, IGitHistoryRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly string _executable;
    private readonly TimeSpan _timeout;
    private bool? _available;

    public GitRunner(string executable = "git", TimeSpan? timeout = null)
    {
        _executable = executable;
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (_available is { } cached)
        {
            return cached;
        }

        var result = await RunAsync(new[] { "--version" }, workingDirectory: null, cancellationToken).ConfigureAwait(false);
        _available = result.Success;
        return result.Success;
    }

    public async Task<GitCommandResult> InitAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        if (!Directory.Exists(workingDirectory))
        {
            return GitCommandResult.ToolMissing($"目录不存在：{workingDirectory}");
        }

        // -b main 与本仓库约定一致；老版本 git 不支持 -b 时回退到默认分支名
        var result = await RunAsync(new[] { "init", "-b", "main" }, workingDirectory, cancellationToken).ConfigureAwait(false);
        if (result.Success || !LooksLikeUnsupportedBranchOption(result))
        {
            return result;
        }

        return await RunAsync(new[] { "init" }, workingDirectory, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitStatusSnapshot> GetStatusAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        if (!Directory.Exists(workingDirectory))
        {
            return GitStatusSnapshot.Unavailable($"目录不存在：{workingDirectory}");
        }

        // ① 先确认是不是仓库：未提交过任何内容的新仓库也要能识别
        var probe = await RunAsync(new[] { "rev-parse", "--is-inside-work-tree" }, workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (!probe.Success || !probe.StandardOutput.Contains("true", StringComparison.OrdinalIgnoreCase))
        {
            return GitStatusSnapshot.NotRepository();
        }

        // ② 必须是**本目录自身**的仓库根：位于上层仓库内（如打开 monorepo 的子目录）时，
        //    拿上层的分支和改动数冒充本项目状态是错的
        var topLevel = await RunAsync(new[] { "rev-parse", "--show-toplevel" }, workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (!topLevel.Success)
        {
            return GitStatusSnapshot.NotRepository();
        }

        var root = topLevel.StandardOutput.Trim();
        if (root.Length > 0 && !GitStatusSnapshot.IsSamePath(root, workingDirectory))
        {
            return GitStatusSnapshot.InParentRepository(root);
        }

        // ③ 分支名：symbolic-ref 在"零提交仓库"里也有效；失败说明处于分离头指针
        var branch = await RunAsync(new[] { "symbolic-ref", "--short", "HEAD" }, workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        var branchText = branch.Success
            ? branch.StandardOutput
            : (await RunAsync(new[] { "rev-parse", "--abbrev-ref", "HEAD" }, workingDirectory, cancellationToken)
                .ConfigureAwait(false)).StandardOutput;

        // ④ 改动数：--porcelain 每行一个条目，含未跟踪文件
        var status = await RunAsync(new[] { "status", "--porcelain" }, workingDirectory, cancellationToken)
            .ConfigureAwait(false);

        return GitStatusSnapshot.FromOutputs(branchText, status.Success ? status.StandardOutput : string.Empty);
    }

    private static bool LooksLikeUnsupportedBranchOption(GitCommandResult result)        => result.StandardError.Contains("unknown switch", StringComparison.OrdinalIgnoreCase)
           || result.StandardError.Contains("unknown option", StringComparison.OrdinalIgnoreCase)
           || result.StandardError.Contains("invalid option", StringComparison.OrdinalIgnoreCase);

    private async Task<GitCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (workingDirectory is not null)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        using var timeoutSource = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return GitCommandResult.ToolMissing("无法启动 git 进程。");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);

            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return GitCommandResult.ToolMissing($"git 执行超时（{_timeout.TotalSeconds:0} 秒），已终止。");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new GitCommandResult(process.ExitCode == 0, process.ExitCode, stdout, stderr);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // git 未安装：这是可预期状态，不是异常路径——生成照常进行，仅提示未初始化仓库
            _available = false;
            return GitCommandResult.ToolMissing($"未找到 git 可执行文件（{ex.Message}）。");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已自行退出
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 权限不足，放弃终止；超时结果仍会返回
        }
    }
}
