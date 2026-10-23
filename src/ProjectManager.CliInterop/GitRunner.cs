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
}

/// <summary>
/// Git CLI 封装（双引擎架构的 CLI 侧）。M2 只需要 <c>git init</c>；
/// 后续 Git 工作台复用这里的进程封装约定：超时看门狗、UTF-8 输出、错误归一。
/// </summary>
public sealed class GitRunner : IGitRunner
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

    private static bool LooksLikeUnsupportedBranchOption(GitCommandResult result)
        => result.StandardError.Contains("unknown switch", StringComparison.OrdinalIgnoreCase)
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
