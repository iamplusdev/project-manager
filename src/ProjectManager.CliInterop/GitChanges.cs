namespace ProjectManager.CliInterop;

/// <summary>单个文件的变更性质。语义沿用 git 自己的两字符状态码，不另造词汇。</summary>
public enum GitChangeKind
{
    /// <summary>无变更（状态码 <c>.</c>）。</summary>
    Unmodified,

    /// <summary>新增（<c>A</c>）。</summary>
    Added,

    /// <summary>修改（<c>M</c>）。</summary>
    Modified,

    /// <summary>删除（<c>D</c>）。</summary>
    Deleted,

    /// <summary>重命名（<c>R</c>）。</summary>
    Renamed,

    /// <summary>复制（<c>C</c>）。</summary>
    Copied,

    /// <summary>类型变更：文件变链接、普通文件变子模块等（<c>T</c>）。</summary>
    TypeChanged,

    /// <summary>未合并（冲突，<c>U</c>）。</summary>
    Unmerged,

    /// <summary>未跟踪（<c>?</c>）。</summary>
    Untracked,

    /// <summary>被忽略（<c>!</c>）。</summary>
    Ignored,
}

/// <summary>
/// 工作区里的一个变更条目。<see cref="Staged"/> 是索引区状态（git 状态码的第一个字符），
/// <see cref="Unstaged"/> 是工作区状态（第二个字符）——同一个文件可以两者都有
/// （先 <c>add</c> 后又改了一笔），UI 必须能如实显示这种"半暂存"状态。
/// </summary>
public sealed record GitChangeEntry
{
    /// <summary>相对仓库根的路径，<c>/</c> 分隔（git 一贯风格，不转成平台分隔符，便于和 diff 头部对齐）。</summary>
    public required string Path { get; init; }

    /// <summary>重命名/复制时的源路径；其余为 <c>null</c>。</summary>
    public string? OriginalPath { get; init; }

    /// <summary>索引区状态。</summary>
    public GitChangeKind Staged { get; init; } = GitChangeKind.Unmodified;

    /// <summary>工作区状态。</summary>
    public GitChangeKind Unstaged { get; init; } = GitChangeKind.Unmodified;

    /// <summary>是否处于未合并（冲突）态。</summary>
    public bool IsConflicted { get; init; }

    /// <summary>是否为目录（未跟踪目录在 porcelain 里以 <c>/</c> 结尾，git 不为目录展开单个文件）。</summary>
    public bool IsDirectory { get; init; }

    /// <summary>
    /// 索引区有内容可提交。未跟踪文件**不算**已暂存——它连索引都没有，
    /// 把它算进来会让"可提交 N 项"在没执行过 add 时就是错的。
    /// </summary>
    public bool HasStagedChanges => Staged is not (GitChangeKind.Unmodified or GitChangeKind.Untracked);

    /// <summary>工作区还有未暂存的变化（含冲突：冲突必须先解决才能暂存）。</summary>
    public bool HasUnstagedChanges => Unstaged != GitChangeKind.Unmodified || IsConflicted;

    /// <summary>展示用路径：重命名显示 <c>旧 → 新</c>，否则就是路径本身。</summary>
    public string DisplayPath => OriginalPath is null ? Path : $"{OriginalPath} → {Path}";

    /// <summary>一行状态文案。冲突优先，其次区分已暂存/未暂存，最后兜底。</summary>
    public string StatusText
    {
        get
        {
            if (IsConflicted)
            {
                return "冲突 · 未解决";
            }

            if (Staged == GitChangeKind.Untracked || Unstaged == GitChangeKind.Untracked)
            {
                return IsDirectory ? "未跟踪目录" : "未跟踪";
            }

            var staged = HasStagedChanges ? Describe(Staged) : null;
            var unstaged = HasUnstagedChanges ? Describe(Unstaged) : null;

            // 两侧都有：说明文件被 add 过之后又改了一笔，这不是"已暂存"也不是"未暂存"
            if (staged is not null && unstaged is not null)
            {
                return $"已暂存{staged} · 又{unstaged}";
            }

            if (staged is not null)
            {
                return $"已暂存 · {staged}";
            }

            return unstaged ?? "无变化";
        }
    }

    private static string Describe(GitChangeKind kind) => kind switch
    {
        GitChangeKind.Added => "新增",
        GitChangeKind.Modified => "修改",
        GitChangeKind.Deleted => "删除",
        GitChangeKind.Renamed => "重命名",
        GitChangeKind.Copied => "复制",
        GitChangeKind.TypeChanged => "类型变更",
        GitChangeKind.Unmerged => "冲突",
        GitChangeKind.Untracked => "未跟踪",
        GitChangeKind.Ignored => "已忽略",
        _ => "无变化",
    };
}

/// <summary>
/// <c>git status --porcelain=v2 -z</c> 的解析器。
/// </summary>
/// <remarks>
/// 选 v2 而不是 v1：v1 的 <c>R  old -&gt; new</c> 要把路径从箭头里"猜"出来，路径本身含 <c> -&gt; </c> 时会解析错；
/// v2 把源路径放在独立字段里，没有歧义。
/// 配 <c>-z</c>：记录以 NUL 分隔且**不做路径转义**（含空格、中文、引号都不加引号），
/// 这是唯一能在 Windows 中文路径上稳定工作的组合。
/// 解析逻辑刻意做成纯函数：状态码规则很多，能用字符串表覆盖的就不该起 git 进程去验。
/// </remarks>
public static class GitStatusParser
{
    /// <summary>
    /// 解析 porcelain v2 的 <c>-z</c> 输出。
    /// </summary>
    /// <param name="output">原始输出，记录间以 <c>\0</c> 分隔。</param>
    public static IReadOnlyList<GitChangeEntry> ParsePorcelainV2(string? output)
    {
        var entries = new List<GitChangeEntry>();
        if (string.IsNullOrEmpty(output))
        {
            return entries;
        }

        // 按 NUL 切分。类型 2（重命名/复制）记录的源路径是**独立的一段**，
        // 所以要能"回看上一段是不是一条 type-2 记录"，用下标循环而不是 foreach。
        var tokens = output.Split('\0');
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Length == 0)
            {
                continue;
            }

            // 头部行（# branch.oid ...）只在 --branch 时出现，这里不请求，但出现了也不该当成条目
            if (token[0] == '#')
            {
                continue;
            }

            switch (token[0])
            {
                case '?':
                    entries.Add(Untracked(token));
                    break;

                case '!':
                    // 不请求 --ignored，正常不会有；真出现了也如实记下而不是丢弃
                    entries.Add(Simple(token, GitChangeKind.Ignored, GitChangeKind.Ignored));
                    break;

                case '1':
                    entries.Add(Ordinary(token, pathIndex: 8));
                    break;

                case '2':
                {
                    // 2 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <X><score> <path> \0 <origPath>
                    // 比 type-1 多一个 <X><score> 字段，路径在第 10 段
                    var entry = Ordinary(token, pathIndex: 9);
                    var original = i + 1 < tokens.Length ? tokens[i + 1] : string.Empty;
                    entries.Add(entry with { OriginalPath = original.Length > 0 ? original : null });
                    i++; // 消费掉源路径这一段，否则它会被当成下一条记录
                    break;
                }

                case 'u':
                    entries.Add(Unmerged(token));
                    break;

                default:
                    // 未知记录类型：跳过而不是抛异常——git 版本升级增字段时不该整页崩掉
                    break;
            }
        }

        return entries;
    }

    private static GitChangeEntry Untracked(string token)
    {
        var path = token.Length > 2 ? token[2..] : string.Empty;
        return new GitChangeEntry
        {
            Path = path,
            Staged = GitChangeKind.Untracked,
            Unstaged = GitChangeKind.Untracked,
            IsDirectory = path.EndsWith('/'),
        };
    }

    private static GitChangeEntry Simple(string token, GitChangeKind staged, GitChangeKind unstaged)
    {
        var path = token.Length > 2 ? token[2..] : string.Empty;
        return new GitChangeEntry
        {
            Path = path,
            Staged = staged,
            Unstaged = unstaged,
            IsDirectory = path.EndsWith('/'),
        };
    }

    /// <summary>
    /// <c>1 XY sub mH mI mW hH hI path</c> —— 路径是第 9 段（下标 8）；
    /// 重命名/复制记录（type 2）多一个 <c>Xscore</c> 字段，路径下标为 9。
    /// 用带上限的 <c>Split</c>：路径本身含空格时，剩余部分整体落在最后一段。
    /// </summary>
    private static GitChangeEntry Ordinary(string token, int pathIndex)
    {
        var fields = token.Split(' ', pathIndex + 1);
        var xy = fields.Length > 1 ? fields[1] : "..";
        var path = fields.Length > pathIndex ? fields[pathIndex] : string.Empty;

        return new GitChangeEntry
        {
            Path = path,
            Staged = ToKind(CharAt(xy, 0)),
            Unstaged = ToKind(CharAt(xy, 1)),
            IsDirectory = path.EndsWith('/'),
        };
    }

    /// <summary><c>u XY sub m1 m2 m3 mW h1 h2 h3 path</c> —— 未合并。</summary>
    private static GitChangeEntry Unmerged(string token)
    {
        var fields = token.Split(' ', 11);
        var xy = fields.Length > 1 ? fields[1] : "UU";
        var path = fields.Length > 10 ? fields[10] : string.Empty;

        return new GitChangeEntry
        {
            Path = path,
            // 冲突文件的两个状态字符按 git 语义都是 U，但具体是 DU/UD/AA 等仍需保留原始码，
            // 这里统一按 Unmerged 展示，因为对用户而言"要解决冲突"才是要紧事
            Staged = GitChangeKind.Unmerged,
            Unstaged = GitChangeKind.Unmerged,
            IsConflicted = true,
            IsDirectory = path.EndsWith('/'),
        };
    }

    private static char CharAt(string text, int index) => index < text.Length ? text[index] : '.';

    private static GitChangeKind ToKind(char code) => code switch
    {
        'A' => GitChangeKind.Added,
        'M' => GitChangeKind.Modified,
        'D' => GitChangeKind.Deleted,
        'R' => GitChangeKind.Renamed,
        'C' => GitChangeKind.Copied,
        'T' => GitChangeKind.TypeChanged,
        'U' => GitChangeKind.Unmerged,
        '?' => GitChangeKind.Untracked,
        '!' => GitChangeKind.Ignored,
        _ => GitChangeKind.Unmodified,
    };
}
