using System.IO;
using ProjectManager.CliInterop;
using ProjectManager.Storage;

namespace ProjectManager.Application.Repositories;

/// <summary>项目在 Git 维度上的归属状态。</summary>
public enum RepositoryState
{
    /// <summary>项目目录自身就是一个仓库根。</summary>
    Repository,

    /// <summary>项目目录不是仓库。</summary>
    NotRepository,

    /// <summary>项目目录位于某个上层仓库内（monorepo 子目录等），不算独立仓库。</summary>
    InParentRepository,

    /// <summary>查不出来（git 缺失、目录不可读等）。</summary>
    Unavailable,
}

/// <summary>
/// 仓库清单的一行：一个项目 + 它当下的仓库事实。
/// 与 <see cref="CliInterop.GitStatusSnapshot"/> 的区别是这一层已经把状态**归一成可展示的文字**，
/// 视图不再需要判断「未初始化是因为没有 .git 还是因为在上层仓库里」。
/// </summary>
public sealed class RepositoryEntry
{
    public required string ProjectId { get; init; }

    public required string Name { get; init; }

    /// <summary>项目根目录绝对路径。</summary>
    public required string Path { get; init; }

    public required RepositoryState State { get; init; }

    public string Branch { get; init; } = string.Empty;

    public int ChangedFiles { get; init; }

    /// <summary>徽标文字：干净 / 有改动 / 未初始化 / 上层仓库 / 不可用。</summary>
    public string StatusText { get; init; } = string.Empty;

    /// <summary>第二行说明：分支与改动数，或未初始化的原因。</summary>
    public string DetailText { get; init; } = string.Empty;

    public bool IsRepository => State == RepositoryState.Repository;

    public bool HasChanges => IsRepository && ChangedFiles > 0;

    /// <summary>路径末级，列表里比全路径好读。</summary>
    public string FolderName
    {
        get
        {
            var trimmed = System.IO.Path.TrimEndingDirectorySeparator(Path);
            var name = System.IO.Path.GetFileName(trimmed);
            return name.Length == 0 ? Path : name;
        }
    }

    /// <summary>把一次仓库查询结果翻译成一行清单。纯函数，便于单测各分支文案。</summary>
    public static RepositoryEntry FromStatus(StoredProject project, GitStatusSnapshot status)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(status);

        var (state, statusText, detailText) = Classify(status);

        return new RepositoryEntry
        {
            ProjectId = project.Id,
            Name = project.Name,
            Path = project.Path,
            State = state,
            Branch = status.Branch,
            ChangedFiles = status.ChangedFiles,
            StatusText = statusText,
            DetailText = detailText,
        };
    }

    /// <summary>查询失败也占一行——清单里"少了某个项目"比显示"不可用"更让人困惑。</summary>
    public static RepositoryEntry Unavailable(StoredProject project, string reason) => new()
    {
        ProjectId = project.Id,
        Name = project.Name,
        Path = project.Path,
        State = RepositoryState.Unavailable,
        StatusText = "不可用",
        DetailText = reason,
    };

    private static (RepositoryState State, string StatusText, string DetailText) Classify(GitStatusSnapshot status)
    {
        if (status.ParentRepository is { } parent)
        {
            var parentName = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(parent));
            return (
                RepositoryState.InParentRepository,
                "上层仓库",
                parentName.Length == 0 ? "位于上层仓库内，未独立初始化" : $"位于上层仓库 {parentName} 内");
        }

        if (status.Error is { } error)
        {
            return (RepositoryState.Unavailable, "不可用", error);
        }

        if (!status.IsRepository)
        {
            return (RepositoryState.NotRepository, "未初始化", "尚未 git init");
        }

        return status.ChangedFiles == 0
            ? (RepositoryState.Repository, "干净", $"{status.Branch} · 工作区干净")
            : (RepositoryState.Repository, "有改动", $"{status.Branch} · {status.ChangedFiles} 处未提交改动");
    }
}

/// <summary>
/// 仓库清单的一行统计。**"未初始化"与"不可用"必须分开**：
/// 前者是用户能处理的状态（点一下 init 就好），后者是查询本身出了问题（目录没了 / git 没装），
/// 混在一起会让人以为只要初始化就能解决。
/// </summary>
public sealed record RepositoryStats(int Total, int Repositories, int Dirty, int Pending, int Unavailable)
{
    public static RepositoryStats From(IReadOnlyList<RepositoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return new RepositoryStats(
            Total: entries.Count,
            Repositories: entries.Count(entry => entry.State == RepositoryState.Repository),
            Dirty: entries.Count(entry => entry.HasChanges),
            Pending: entries.Count(entry => entry.State is RepositoryState.NotRepository or RepositoryState.InParentRepository),
            Unavailable: entries.Count(entry => entry.State == RepositoryState.Unavailable));
    }

    /// <summary>一行文案。无项目时为空串（空态另有提示，不必再说一遍"0 个"）。</summary>
    public string Text
    {
        get
        {
            if (Total == 0)
            {
                return string.Empty;
            }

            var parts = new List<string> { $"{Repositories} 个仓库" };
            if (Dirty > 0)
            {
                parts.Add($"{Dirty} 个有改动");
            }

            if (Pending > 0)
            {
                parts.Add($"{Pending} 个未初始化");
            }

            if (Unavailable > 0)
            {
                parts.Add($"{Unavailable} 个不可用");
            }

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// 跨项目仓库清单（G3 第 2 项）。逐个查 <see cref="IGitRunner.GetStatusAsync"/>，
/// 但**限流**：单项目一次查询要起 4 个 git 进程，N 个项目并发不打伞会把机器拖住。
/// </summary>
/// <remarks>
/// 结果顺序与传入的项目顺序一致（清单顺序由项目清单决定，不按状态重排——
/// 否则每次刷新行序都可能变，用户会找不到刚才那行）。
/// </remarks>
public sealed class RepositoryOverview
{
    /// <summary>默认并发度：4 个项目同时查，最多 16 个 git 进程。</summary>
    public const int DefaultConcurrency = 4;

    private readonly IGitRunner _git;
    private readonly int _maxConcurrency;

    public RepositoryOverview(IGitRunner git, int maxConcurrency = DefaultConcurrency)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

        _git = git;
        _maxConcurrency = maxConcurrency;
    }

    /// <summary>查一遍清单。单个项目失败只影响它自己那一行。</summary>
    public async Task<IReadOnlyList<RepositoryEntry>> QueryAsync(
        IReadOnlyList<StoredProject> projects,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projects);

        if (projects.Count == 0)
        {
            return Array.Empty<RepositoryEntry>();
        }

        using var gate = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
        var slots = new RepositoryEntry[projects.Count];
        var tasks = new Task[projects.Count];

        for (var index = 0; index < projects.Count; index++)
        {
            var slot = index;
            var project = projects[slot];
            tasks[slot] = QueryOneAsync(project, gate, entry => slots[slot] = entry, cancellationToken);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return slots;
    }

    private async Task QueryOneAsync(
        StoredProject project,
        SemaphoreSlim gate,
        Action<RepositoryEntry> publish,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = await _git.GetStatusAsync(project.Path, cancellationToken).ConfigureAwait(false);
            publish(RepositoryEntry.FromStatus(project, status));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            // 一个项目查不出来不应该让整张清单失败
            publish(RepositoryEntry.Unavailable(project, $"查询失败：{ex.Message}"));
        }
        finally
        {
            gate.Release();
        }
    }
}
