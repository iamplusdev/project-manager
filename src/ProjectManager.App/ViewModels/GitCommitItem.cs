using ProjectManager.Application.Git;
using ProjectManager.CliInterop;

namespace ProjectManager.App.ViewModels;

/// <summary>
/// 提交历史列表里的一行。
/// </summary>
/// <remarks>
/// 展示串（相对时间、refs 标签）在构造时就定死：列表刷新时统一取一个 <c>now</c>，
/// 同一屏里的"3 小时前"才不会因为各行取值时刻不同而出现"3 小时前"排在"2 小时前"上面。
/// </remarks>
public sealed class GitCommitItem
{
    public GitCommitItem(GitCommitEntry entry, DateTimeOffset now)
    {
        Entry = entry;
        WhenText = GitHistoryService.DescribeWhen(now, entry.AuthorDate);
        RefsText = GitHistoryService.DescribeRefs(entry.Refs);
        MetaText = $"{entry.ShortHash} · {entry.Author} · {WhenText}";
    }

    public GitCommitEntry Entry { get; }

    public string Hash => Entry.Hash;

    public string ShortHash => Entry.ShortHash;

    public string Subject => Entry.Subject;

    public string Author => Entry.Author;

    public string WhenText { get; }

    public string RefsText { get; }

    public bool HasRefs => RefsText.Length > 0;

    /// <summary>「合并」「根提交」之一；普通提交为空串。</summary>
    public string KindNote => Entry.KindNote;

    public bool HasKindNote => KindNote.Length > 0;

    public string MetaText { get; }
}

/// <summary>提交详情里的一条文件改动。</summary>
public sealed class GitCommitFileItem
{
    public GitCommitFileItem(GitCommitFile file)
    {
        File = file;
    }

    public GitCommitFile File { get; }

    public string Path => File.Path;

    public string ChangeText => File.ChangeText;

    public bool IsBinary => File.IsBinary;

    /// <summary>重命名行（numstat 用 <c>旧 => 新</c> 表示）单独标出来，否则用户会以为路径里真有箭头。</summary>
    public bool IsRename => File.IsRename;

    public bool IsAddition => !File.IsBinary && (File.Added ?? 0) > 0 && (File.Deleted ?? 0) == 0;

    public bool IsDeletion => !File.IsBinary && (File.Deleted ?? 0) > 0 && (File.Added ?? 0) == 0;
}
