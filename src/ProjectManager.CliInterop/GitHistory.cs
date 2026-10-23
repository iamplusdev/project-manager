using System.Globalization;

namespace ProjectManager.CliInterop;

/// <summary>ref 指向的类型。决定 UI 用哪种颜色的标签显示。</summary>
public enum GitRefKind
{
    /// <summary>本地分支（<c>refs/heads/…</c>）。</summary>
    Branch,

    /// <summary>远程跟踪分支（<c>refs/remotes/…</c>）。</summary>
    Remote,

    /// <summary>标签（<c>refs/tags/…</c>）。</summary>
    Tag,

    /// <summary>其它 ref（<c>refs/stash</c> 之类）。</summary>
    Other,
}

/// <summary>一个指向提交的 ref，名字已去掉 <c>refs/heads/</c> 这类限定前缀。</summary>
public sealed record GitRefLabel(GitRefKind Kind, string Name, bool IsCurrent)
{
    /// <summary>标签文案：当前分支加一个星号，其余直接是名字。</summary>
    public string Display => IsCurrent ? $"*{Name}" : Name;
}

/// <summary>
/// 提交历史里的一条。
/// </summary>
/// <remarks>
/// 字段全部来自一次 <c>git log</c> 的格式化输出，**不包含提交正文**——正文可能有多行，
/// 混在按行切分的记录里会让解析变脆。正文在用户选中某条时按需再取（<see cref="GitCommitDetail"/>）。
/// </remarks>
public sealed record GitCommitEntry
{
    public required string Hash { get; init; }

    public required string ShortHash { get; init; }

    public string Author { get; init; } = string.Empty;

    public string AuthorEmail { get; init; } = string.Empty;

    /// <summary>作者时间。<c>null</c> 表示 git 给了无法解析的日期（不该发生，但不静默当成"现在"）。</summary>
    public DateTimeOffset? AuthorDate { get; init; }

    public string Subject { get; init; } = string.Empty;

    /// <summary>父提交个数。0 = 根提交，≥2 = 合并提交。</summary>
    public int ParentCount { get; init; }

    public IReadOnlyList<GitRefLabel> Refs { get; init; } = Array.Empty<GitRefLabel>();

    public bool IsMerge => ParentCount > 1;

    public bool IsRoot => ParentCount == 0;

    /// <summary>提交右侧的类型注记，只标"值得提醒"的两种，普通提交返回空串不加噪声。</summary>
    public string KindNote => IsMerge ? "合并" : IsRoot ? "根提交" : string.Empty;
}

/// <summary>提交详情里的一条文件改动。<see cref="Added"/>/<see cref="Deleted"/> 为 <c>null</c> 表示二进制文件（git 给 <c>-</c>）。</summary>
public sealed record GitCommitFile
{
    public required string Path { get; init; }

    public int? Added { get; init; }

    public int? Deleted { get; init; }

    public bool IsBinary => Added is null || Deleted is null;

    /// <summary>numstat 对重命名给的是 <c>旧 => 新</c>（或 <c>dir/{旧 => 新}/f</c>），原样保留。</summary>
    public bool IsRename => Path.Contains(" => ", StringComparison.Ordinal);

    public string ChangeText => IsBinary ? "二进制" : $"+{Added} −{Deleted}";
}

/// <summary>单条提交的详情：完整信息 + 改动文件清单。</summary>
public sealed record GitCommitDetail
{
    public required GitCommitEntry Commit { get; init; }

    /// <summary>完整提交信息（含标题、空行、正文）。</summary>
    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<GitCommitFile> Files { get; init; } = Array.Empty<GitCommitFile>();

    /// <summary>读取失败的原因；成功为 <c>null</c>。</summary>
    public string? Error { get; init; }

    /// <summary>合计增删行数。二进制文件不计入。</summary>
    public int TotalAdded => Files.Sum(file => file.Added ?? 0);

    public int TotalDeleted => Files.Sum(file => file.Deleted ?? 0);

    /// <summary>一行摘要，空文件清单时说明为什么空。</summary>
    public string FilesSummary
    {
        get
        {
            if (Error is not null)
            {
                return Error;
            }

            if (Files.Count == 0)
            {
                return Commit.IsMerge ? "合并提交，按第一父提交统计后没有文件差异。" : "这次提交没有文件改动。";
            }

            var binary = Files.Count(file => file.IsBinary);
            var text = $"{Files.Count} 个文件 · +{TotalAdded} −{TotalDeleted}";
            return binary > 0 ? $"{text}（含 {binary} 个二进制）" : text;
        }
    }
}

/// <summary>
/// <c>git log</c> 与 <c>git show --numstat</c> 的输出解析。全部为纯函数，不碰磁盘、不启进程。
/// </summary>
/// <remarks>
/// 记录格式用 <c>%x00</c> 作为字段分隔、换行作为记录分隔：
/// NUL 是 git 保证不会出现在作者名/邮件/标题里的唯一字符；<c>%s</c> 取的是首行，
/// 天然不含换行，所以"一条提交一行"成立。用 <c>|</c> 或 Tab 之类的可见字符做分隔，
/// 遇到作者名里带分隔符就会错位。
/// </remarks>
public static class GitHistoryParser
{
    /// <summary>
    /// 一次 <c>git log</c> 的格式串。字段顺序与 <see cref="ParseLog"/> 的下标严格对应，改一处必须改另一处。
    /// </summary>
    /// <remarks>
    /// <c>--decorate=full</c> 让 <c>%D</c> 给出 <c>refs/heads/main</c> 这样的限定名。
    /// 若用短名，本地分支 <c>feature/x</c> 与远程 <c>origin/main</c> 无法区分（短名里都带斜杠），
    /// 只能靠猜；限定名是 git 自己给的答案，不用猜。
    /// </remarks>
    public const string LogFormat = "%H%x00%h%x00%an%x00%ae%x00%aI%x00%P%x00%D%x00%s";

    /// <summary>解析 <see cref="LogFormat"/> 的输出。</summary>
    public static IReadOnlyList<GitCommitEntry> ParseLog(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return Array.Empty<GitCommitEntry>();
        }

        var entries = new List<GitCommitEntry>();

        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            // 末尾换行会留一个空串
            if (raw.Length == 0)
            {
                continue;
            }

            var fields = raw.Split('\0');

            // 字段数不对说明这行不是我们生成的格式（git 的警告混进 stdout 等）：
            // 宁可丢掉这一条，也不要往下标越界取到别的字段而显示错误信息
            if (fields.Length < 8)
            {
                continue;
            }

            entries.Add(new GitCommitEntry
            {
                Hash = fields[0],
                ShortHash = fields[1],
                Author = fields[2],
                AuthorEmail = fields[3],
                AuthorDate = ParseDate(fields[4]),
                ParentCount = fields[5].Length == 0
                    ? 0
                    : fields[5].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
                Refs = ParseRefs(fields[6]),
                Subject = fields[7],
            });
        }

        return entries;
    }

    /// <summary>把 <c>%D</c>（限定名形式）拆成 ref 列表。</summary>
    /// <remarks>
    /// 分隔符是 <c>", "</c>：git 的 ref 名不允许出现空格（<c>git check-ref-format</c> 的硬规则），
    /// 所以按逗号+空格切不会把一个名字切成两半。
    /// </remarks>
    public static IReadOnlyList<GitRefLabel> ParseRefs(string? decorations)
    {
        if (string.IsNullOrWhiteSpace(decorations))
        {
            return Array.Empty<GitRefLabel>();
        }

        var labels = new List<GitRefLabel>();

        foreach (var piece in decorations.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            var text = piece.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var isCurrent = false;

            if (text.Equals("HEAD", StringComparison.Ordinal))
            {
                labels.Add(new GitRefLabel(GitRefKind.Other, "HEAD", false));
                continue;
            }

            // "HEAD -> refs/heads/main"：HEAD 指向的分支就是当前分支
            if (text.StartsWith("HEAD -> ", StringComparison.Ordinal))
            {
                isCurrent = true;
                text = text["HEAD -> ".Length..];
            }

            // "tag: refs/tags/v1.0"：标签前面有 "tag: " 前缀
            if (text.StartsWith("tag: ", StringComparison.Ordinal))
            {
                text = text["tag: ".Length..];
            }

            var (kind, name) = Classify(text);
            labels.Add(new GitRefLabel(kind, name, isCurrent));
        }

        return labels;
    }

    private static (GitRefKind Kind, string Name) Classify(string qualified)
    {
        if (qualified.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            return (GitRefKind.Branch, qualified["refs/heads/".Length..]);
        }

        if (qualified.StartsWith("refs/remotes/", StringComparison.Ordinal))
        {
            return (GitRefKind.Remote, qualified["refs/remotes/".Length..]);
        }

        if (qualified.StartsWith("refs/tags/", StringComparison.Ordinal))
        {
            return (GitRefKind.Tag, qualified["refs/tags/".Length..]);
        }

        // 解析后为空（例如顶层直接是 refs/）：保留原文，不显示空标签
        return (GitRefKind.Other, qualified.Length == 0 ? "HEAD" : qualified);
    }

    /// <summary>
    /// 解析 <c>git show --numstat --format=%B%x00</c> 的输出。
    /// </summary>
    /// <param name="output">命令原始输出。</param>
    /// <param name="commit">所属提交（<see cref="GitCommitDetail.Commit"/> 直接复用，不重复解析）。</param>
    /// <remarks>
    /// 用 <c>%B%x00</c> 把提交信息与 numstat 拼在一次调用里取回：分两次请求会多一次进程启动，
    /// 更麻烦的是两次之间工作区可能已经变了（比如正在 rebase），拿到的正文与文件清单对不上。
    /// </remarks>
    public static GitCommitDetail ParseDetail(string? output, GitCommitEntry commit)
    {
        if (string.IsNullOrEmpty(output))
        {
            return new GitCommitDetail { Commit = commit };
        }

        var normalized = output.Replace("\r\n", "\n");
        var split = normalized.IndexOf('\0', StringComparison.Ordinal);

        // 没有分隔符说明格式串没生效（老 git 或参数被改写）：整段当信息，文件清单为空，
        // 而不是把 diff 正文误当成 numstat 行
        if (split < 0)
        {
            return new GitCommitDetail { Commit = commit, Message = normalized };
        }

        var message = normalized[..split];
        var rest = normalized[(split + 1)..];

        return new GitCommitDetail
        {
            Commit = commit,
            // git 在 %B 后面会补换行；提交信息的"结尾换行"不是内容，去掉以免 UI 多出空行
            Message = message.TrimEnd('\n'),
            Files = ParseNumstat(rest),
        };
    }

    /// <summary>解析 numstat 行：<c>增加\t删除\t路径</c>，二进制为 <c>-\t-\t路径</c>。</summary>
    public static IReadOnlyList<GitCommitFile> ParseNumstat(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<GitCommitFile>();
        }

        var files = new List<GitCommitFile>();

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length == 0)
            {
                continue;
            }

            // 路径里可能有 Tab（虽然罕见）：按 3 段切，把第三个字段之后的全当路径
            var parts = raw.Split('\t', 3);
            if (parts.Length < 3)
            {
                continue;
            }

            var path = parts[2];
            if (path.Length == 0)
            {
                continue;
            }

            files.Add(new GitCommitFile
            {
                Path = path,
                Added = ParseCount(parts[0]),
                Deleted = ParseCount(parts[1]),
            });
        }

        return files;
    }

    /// <summary>numstat 的 <c>-</c> 表示二进制（不可计行），返回 <c>null</c> 而不是 0——0 是"改了但没加删行"。</summary>
    private static int? ParseCount(string text)
        => text == "-"
            ? null
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;

    private static DateTimeOffset? ParseDate(string text)
        => DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var value)
            ? value
            : null;
}
