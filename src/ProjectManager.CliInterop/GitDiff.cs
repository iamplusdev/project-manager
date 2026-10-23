using System.Globalization;

namespace ProjectManager.CliInterop;

/// <summary>diff 里的一行。行号用于 UI 显示，未变更的上下文行两侧都有行号。</summary>
public enum DiffLineKind
{
    Context,
    Added,
    Removed,

    /// <summary><c>\ No newline at end of file</c> 尾注。它不是内容行，单独一类避免被计成 +/-。</summary>
    NoNewline,
}

public sealed record DiffLine(DiffLineKind Kind, string Text, int? OldLineNumber, int? NewLineNumber)
{
    /// <summary>单字符标记，UI 左栏显示（空格 / <c>+</c> / <c>-</c>）。</summary>
    public string Marker => Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "-",
        _ => " ",
    };
}

public sealed record DiffHunk(
    string Header,
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    IReadOnlyList<DiffLine> Lines)
{
    /// <summary>把 header 之外的正文还原成可直接展示的行（不含行号）。</summary>
    public string ToText() => string.Join('\n', Lines.Select(line => line.Marker + line.Text));
}

/// <summary>一个文件的差异。</summary>
public sealed record FileDiff
{
    /// <summary>新路径（删除的文件取旧路径）。</summary>
    public required string Path { get; init; }

    /// <summary>重命名时的旧路径。</summary>
    public string? OriginalPath { get; init; }

    public bool IsNew { get; init; }

    public bool IsDeleted { get; init; }

    public bool IsBinary { get; init; }

    public bool IsRenamed => OriginalPath is not null;

    /// <summary>取差异时出错的原因（非空表示"没拿到内容"，而不是"文件没改动"）。</summary>
    public string? Error { get; init; }

    public IReadOnlyList<DiffHunk> Hunks { get; init; } = Array.Empty<DiffHunk>();

    public int AddedLines => Hunks.Sum(hunk => hunk.Lines.Count(line => line.Kind == DiffLineKind.Added));

    public int RemovedLines => Hunks.Sum(hunk => hunk.Lines.Count(line => line.Kind == DiffLineKind.Removed));

    /// <summary>一句话摘要：<c>+12 −3</c>；二进制文件没有行数，直说。</summary>
    public string Summary => IsBinary ? "二进制文件" : $"+{AddedLines} −{RemovedLines}";

    /// <summary>变更性质标签。</summary>
    public string KindText
    {
        get
        {
            if (IsBinary)
            {
                return IsNew ? "新增（二进制）" : IsDeleted ? "删除（二进制）" : "二进制改动";
            }

            if (IsNew)
            {
                return "新增文件";
            }

            if (IsDeleted)
            {
                return "删除文件";
            }

            return IsRenamed ? "重命名" : "修改";
        }
    }
}

/// <summary>
/// <c>git diff</c> 的统一格式解析器。
/// </summary>
/// <remarks>
/// 只解析「多文件 diff」这种最常见的输出（<c>git diff</c> / <c>git diff --cached</c>）。
/// 明确**不支持**的：合并冲突的 <c>diff --cc</c> 段（多父提交的 <c>@@@</c> 格式）——
/// 冲突文件在 UI 上直接提示"需在外部解决"，比给一个三方 diff 的半成品更有用。
/// </remarks>
public static class UnifiedDiffParser
{
    /// <summary>解析多文件 diff；无 hunk 的段（纯模式变更、二进制）也会保留为条目。</summary>
    public static IReadOnlyList<FileDiff> Parse(string? output)
    {
        var files = new List<FileDiff>();
        if (string.IsNullOrEmpty(output))
        {
            return files;
        }

        // 统一按 \n 切分后逐行推进。git 输出的行尾不受平台影响，但 SourceText 里可能带 \r
        var lines = output.Replace("\r\n", "\n").Split('\n');

        var index = 0;
        while (index < lines.Length)
        {
            if (!lines[index].StartsWith("diff --git ", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            var builder = new FileDiffBuilder(lines[index]);
            index++;

            // 头部元信息段：直到第一个 @@ 或下一个 diff --git
            while (index < lines.Length
                   && !lines[index].StartsWith("@@", StringComparison.Ordinal)
                   && !lines[index].StartsWith("diff --git ", StringComparison.Ordinal))
            {
                builder.ReadHeaderLine(lines[index]);
                index++;
            }

            // hunk 段
            while (index < lines.Length && lines[index].StartsWith("@@", StringComparison.Ordinal))
            {
                index = ReadHunk(lines, index, builder);
            }

            files.Add(builder.Build());
        }

        return files;
    }

    /// <summary>读一个 hunk（header + 正文），返回下一个待处理行号。</summary>
    private static int ReadHunk(string[] lines, int index, FileDiffBuilder builder)
    {
        var header = lines[index];
        var range = ParseHunkHeader(header);
        index++;

        var body = new List<DiffLine>();
        var oldLine = range.OldStart;
        var newLine = range.NewStart;

        while (index < lines.Length)
        {
            var line = lines[index];

            // 下一条 hunk / 下一个文件 / 其它段落开头：hunk 结束
            if (line.StartsWith("@@", StringComparison.Ordinal) || line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                break;
            }

            if (line.StartsWith('\\'))
            {
                // \ No newline at end of file —— 不占行号，也不是内容行
                body.Add(new DiffLine(DiffLineKind.NoNewline, line, null, null));
                index++;
                continue;
            }

            // 行数已凑够就停：否则 diff 结尾的空白会被当成"空的上下文行"多算一行。
            // 判定要放在空行分支**之前**——空串既可能是真的空上下文行，也可能是文件末尾的换行残留
            if (range.IsSatisfied(oldLine - range.OldStart, newLine - range.NewStart))
            {
                break;
            }

            if (line.Length == 0)
            {
                // 空的上下文行在 diff 里只剩换行，行首的空格标记被吃掉了
                body.Add(new DiffLine(DiffLineKind.Context, string.Empty, oldLine++, newLine++));
                index++;
                continue;
            }

            switch (line[0])
            {
                case '+':
                    body.Add(new DiffLine(DiffLineKind.Added, line[1..], null, newLine++));
                    break;

                case '-':
                    body.Add(new DiffLine(DiffLineKind.Removed, line[1..], oldLine++, null));
                    break;

                case ' ':
                    body.Add(new DiffLine(DiffLineKind.Context, line[1..], oldLine++, newLine++));
                    break;

                default:
                    // 既不是 +/-/空格/\ 也不是新段：说明 hunk 内容到此为止
                    builder.AddHunk(new DiffHunk(header, range.OldStart, range.OldCount, range.NewStart, range.NewCount, body));
                    return index;
            }

            index++;
        }

        builder.AddHunk(new DiffHunk(header, range.OldStart, range.OldCount, range.NewStart, range.NewCount, body));
        return index;
    }

    /// <summary>解析 <c>@@ -1,3 +1,4 @@</c>；省略的计数按 1 处理（git 的省略规则）。</summary>
    internal static HunkRange ParseHunkHeader(string header)
    {
        // 形如 @@ -oldStart[,oldCount] +newStart[,newCount] @@
        var cursor = header.IndexOf('-', StringComparison.Ordinal);
        var plus = header.IndexOf('+', StringComparison.Ordinal);
        if (cursor < 0 || plus < 0 || plus < cursor)
        {
            return new HunkRange(1, 0, 1, 0);
        }

        ReadRange(header, cursor + 1, plus, out var oldStart, out var oldCount);
        var end = header.IndexOf("@@", plus, StringComparison.Ordinal);
        ReadRange(header, plus + 1, end < 0 ? header.Length : end, out var newStart, out var newCount);

        return new HunkRange(oldStart, oldCount, newStart, newCount);
    }

    private static void ReadRange(string text, int start, int end, out int startValue, out int count)
    {
        var slice = text[start..Math.Min(end, text.Length)].Trim();
        var comma = slice.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            startValue = ParseInt(slice, 1);
            count = 1;
            return;
        }

        startValue = ParseInt(slice[..comma], 1);
        count = ParseInt(slice[(comma + 1)..], 0);
    }

    private static int ParseInt(string text, int fallback)
        => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    internal readonly record struct HunkRange(int OldStart, int OldCount, int NewStart, int NewCount)
    {
        /// <summary>两侧都已消费满。</summary>
        public bool IsSatisfied(int oldConsumed, int newConsumed) => oldConsumed >= OldCount && newConsumed >= NewCount;
    }

    /// <summary>把逐行读到的元信息攒成一个 <see cref="FileDiff"/>。</summary>
    private sealed class FileDiffBuilder
    {
        private readonly string _header;
        private readonly List<DiffHunk> _hunks = new();
        private string? _oldPath;
        private string? _newPath;
        private string? _renameFrom;
        private string? _renameTo;
        private bool _isNew;
        private bool _isDeleted;
        private bool _isBinary;

        public FileDiffBuilder(string header) => _header = header;

        public void ReadHeaderLine(string line)
        {
            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                _oldPath = NormalizePath(line[4..]);
                return;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                _newPath = NormalizePath(line[4..]);
                return;
            }

            if (line.StartsWith("new file mode", StringComparison.Ordinal))
            {
                _isNew = true;
                return;
            }

            if (line.StartsWith("deleted file mode", StringComparison.Ordinal))
            {
                _isDeleted = true;
                return;
            }

            if (line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                _renameFrom = line["rename from ".Length..];
                return;
            }

            if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                _renameTo = line["rename to ".Length..];
                return;
            }

            if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line.Equals("GIT binary patch", StringComparison.Ordinal))
            {
                _isBinary = true;
            }
        }

        public void AddHunk(DiffHunk hunk) => _hunks.Add(hunk);

        public FileDiff Build()
        {
            var path = _newPath
                ?? _renameTo
                ?? _renameFrom
                ?? PathFromDiffGit(_header);

            // 删除的文件 +++ 侧是 /dev/null（归一为空串），只能用 --- 侧
            if (_isDeleted)
            {
                path = _oldPath ?? _renameFrom ?? path;
            }

            return new FileDiff
            {
                Path = path,
                // 只有 git 明说 rename from/to 才算重命名；
                // 普通的 --- / +++ 两侧不同不代表重命名（git 会同时输出 rename 段作为证据）
                OriginalPath = _renameFrom is not null && !string.Equals(_renameFrom, path, StringComparison.Ordinal)
                    ? _renameFrom
                    : null,
                IsNew = _isNew,
                IsDeleted = _isDeleted,
                IsBinary = _isBinary,
                Hunks = _hunks,
            };
        }

        /// <summary><c>--- a/x</c> / <c>+++ b/x</c> / <c>+++ /dev/null</c> 归一为仓库相对路径。</summary>
        private static string NormalizePath(string raw)
        {
            var path = raw.Trim();
            if (path.Length == 0 || path == "/dev/null")
            {
                return string.Empty;
            }

            // 去掉 a/ b/ 前缀；文件名本身以 a/ 开头的罕见情况靠 git 的 --no-prefix 才能区分，
            // 这里保持默认前缀约定
            if (path.Length > 2 && (path[0] == 'a' || path[0] == 'b') && path[1] == '/')
            {
                path = path[2..];
            }

            return path;
        }

        /// <summary>兜底：从 <c>diff --git a/x b/x</c> 里取后半段。</summary>
        private static string PathFromDiffGit(string header)
        {
            var text = header["diff --git ".Length..].Trim();
            if (text.Length == 0)
            {
                return string.Empty;
            }

            // 路径含空格时两个路径以 " b/" 分隔；找最后一个 " b/" 作切点
            var marker = text.LastIndexOf(" b/", StringComparison.Ordinal);
            return marker > 0 ? text[(marker + 3)..] : text;
        }
    }
}
