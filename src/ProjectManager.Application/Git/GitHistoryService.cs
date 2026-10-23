using System.Globalization;
using ProjectManager.CliInterop;

namespace ProjectManager.Application.Git;

/// <summary>历史页的展示态：分页结果 + 该给用户看的一句解释。</summary>
public sealed record GitHistoryView
{
    public required GitHistoryPage Page { get; init; }

    /// <summary>用于把提交时间换算成"3 小时前"的参照时刻。加载时取一次，同一屏内保持一致。</summary>
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;

    public bool IsRepository => Page.IsRepository;

    public bool HasCommits => Page.Commits.Count > 0;

    public bool HasMore => Page.HasMore;

    public string Summary
    {
        get
        {
            if (Page.Error is not null)
            {
                return $"提交历史不可用：{Page.Error}";
            }

            if (Page.HasNoCommits)
            {
                return "还没有任何提交";
            }

            // 没全载完时不能说"共 N 个"：那是猜的。全载完才敢用"共"
            return Page.HasMore
                ? $"已加载 {Page.LoadedCount} 个提交（还有更多）"
                : $"共 {Page.LoadedCount} 个提交";
        }
    }

    /// <summary>空清单时解释为什么空，并给出下一步。</summary>
    public string EmptyHint
    {
        get
        {
            if (Page.Error is not null)
            {
                return Page.Error;
            }

            if (!Page.IsRepository)
            {
                return "这个目录还不是独立仓库，没有提交历史可看。";
            }

            if (Page.HasNoCommits)
            {
                return "仓库已初始化，但还没有提交过。到「变更」页暂存并提交第一次改动，它就会出现在这里。";
            }

            return "没有可显示的提交。";
        }
    }

    public static GitHistoryView From(GitHistoryPage page, DateTimeOffset now) => new() { Page = page, Now = now };
}

/// <summary>
/// 提交历史的编排层。
/// </summary>
/// <remarks>
/// 比变更页薄得多，因为历史是只读的：没有"校验 → 执行 → 回读"三段式，
/// 剩下的价值都在**展示规则**上——相对时间怎么说、空态怎么解释、分页怎么续。
/// 这些规则都是纯函数，放在这里而不是 XAML 里，才能被断言钉住。
/// </remarks>
public sealed class GitHistoryService
{
    /// <summary>一页多少条。50 条大约是一屏半，够翻又不会让首屏等太久。</summary>
    public const int DefaultPageSize = 50;

    private readonly IGitHistoryRunner _runner;

    public GitHistoryService(IGitHistoryRunner runner) => _runner = runner;

    /// <summary>读一页历史。<paramref name="skip"/> 是已加载条数，首页传 0。</summary>
    public async Task<GitHistoryView> LoadAsync(
        string repositoryRoot,
        int skip = 0,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var page = await _runner.GetHistoryAsync(repositoryRoot, pageSize, skip, cancellationToken).ConfigureAwait(false);
        return GitHistoryView.From(page, DateTimeOffset.Now);
    }

    /// <summary>读单条提交的完整信息与文件统计。</summary>
    public Task<GitCommitDetail> LoadDetailAsync(
        string repositoryRoot,
        GitCommitEntry commit,
        CancellationToken cancellationToken = default)
        => _runner.GetCommitDetailAsync(repositoryRoot, commit, cancellationToken);

    /// <summary>
    /// 把提交时刻说成人话。
    /// </summary>
    /// <remarks>
    /// 用绝对时刻之差（<see cref="DateTimeOffset"/> 相减不受时区偏移影响），所以作者在东八区、
    /// 用户在西半球时"3 小时前"仍然是 3 小时前。
    /// 超过 30 天改用日期：再往上"37 天前"必须心算，日期反而更好读。
    /// </remarks>
    public static string DescribeWhen(DateTimeOffset now, DateTimeOffset? when)
    {
        if (when is not { } moment)
        {
            return "时间未知";
        }

        var delta = now - moment;

        // 负值说明机器时钟偏差或提交来自"未来"：显示"刚刚"比显示"-2 小时前"体面
        if (delta <= TimeSpan.Zero)
        {
            return "刚刚";
        }

        if (delta.TotalMinutes < 1)
        {
            return "刚刚";
        }

        if (delta.TotalHours < 1)
        {
            return $"{(int)delta.TotalMinutes} 分钟前";
        }

        if (delta.TotalDays < 1)
        {
            return $"{(int)delta.TotalHours} 小时前";
        }

        if (delta.TotalDays < 2)
        {
            return "昨天";
        }

        if (delta.TotalDays < 30)
        {
            return $"{(int)delta.TotalDays} 天前";
        }

        // 本地时区的日期：用户看的是自己的日历，不是作者所在时区的日历
        return moment.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 提交列表里 refs 标签的合并文案，例如 <c>main · v1.0</c>。
    /// 标签多于 3 个时只留 3 个加省略号——一行放不下，且第 4 个标签通常不是用户关心的。
    /// </summary>
    public static string DescribeRefs(IReadOnlyList<GitRefLabel> refs)
    {
        if (refs.Count == 0)
        {
            return string.Empty;
        }

        var shown = refs.Take(3).Select(label => label.Display).ToList();
        return refs.Count > 3 ? string.Join(" · ", shown) + " …" : string.Join(" · ", shown);
    }
}
