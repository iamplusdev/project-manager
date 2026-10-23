using ProjectManager.CliInterop;
using ProjectManager.Storage;

namespace ProjectManager.Application.Tests;

/// <summary>假的 Git：默认成功，可切换为失败而不真正启动进程（测试不依赖本机装没装 git）。</summary>
public sealed class FakeGitRunner : IGitRunner
{
    public bool Success { get; set; } = true;

    public int InitCalls { get; private set; }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(Success);

    public Task<GitCommandResult> InitAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        InitCalls++;
        return Task.FromResult(Success
            ? new GitCommandResult(true, 0, "Initialized empty Git repository", string.Empty)
            : new GitCommandResult(false, 1, string.Empty, "模拟：git 不可用"));
    }

    /// <summary>详情页用的仓库状态；默认"不是仓库"，需要时由用例设定。</summary>
    public GitStatusSnapshot Status { get; set; } = GitStatusSnapshot.NotRepository();

    /// <summary>按路径指定的状态（大小写不敏感）。仓库清单用例要一次给多个项目不同状态。</summary>
    public Dictionary<string, GitStatusSnapshot> StatusByPath { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>人为延迟，用于观测并发度。</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>指定路径抛出的异常，用于验证"单个项目失败不影响整张清单"。</summary>
    public Func<string, Exception?>? ThrowFor { get; set; }

    public List<string> QueriedPaths { get; } = new();

    /// <summary>观测到的最大并发查询数（验证限流是否真的生效）。</summary>
    public int MaxObservedConcurrency { get; private set; }

    private readonly object _gate = new();
    private int _active;

    public async Task<GitStatusSnapshot> GetStatusAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _active++;
            if (_active > MaxObservedConcurrency)
            {
                MaxObservedConcurrency = _active;
            }

            QueriedPaths.Add(workingDirectory);
        }

        try
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            }

            if (ThrowFor?.Invoke(workingDirectory) is { } failure)
            {
                throw failure;
            }

            return StatusByPath.TryGetValue(workingDirectory, out var status) ? status : Status;
        }
        finally
        {
            lock (_gate)
            {
                _active--;
            }
        }
    }
}

/// <summary>写入必失败的清单仓储，用于验证"文件已生成 + 可重试注册"的降级语义。</summary>
public sealed class ThrowingRepository : IProjectRepository
{
    public Task<ProjectLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new ProjectLoadResult { Projects = Array.Empty<StoredProject>() });

    public Task<StoredProject> UpsertAsync(StoredProject project, CancellationToken cancellationToken = default)
        => throw new IOException("模拟：项目清单写入失败");

    public Task<StoredProject?> TouchAsync(string id, DateTimeOffset at, CancellationToken cancellationToken = default)
        => Task.FromResult<StoredProject?>(null);

    public Task<bool> RemoveAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<IReadOnlyList<StoredProject>> ListRecentAsync(int count, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<StoredProject>>(Array.Empty<StoredProject>());
}
