using ProjectManager.CliInterop;

namespace ProjectManager.Application.Git;

/// <summary>
/// 差异预览的一行。为绑定准备的扁平结构：hunk header 也作为一行插进去，
/// 这样 UI 用一条 <c>ListBox</c> 就能画出"分段 + 内容"的完整 diff，不需要嵌套模板。
/// </summary>
public sealed record DiffRow
{
    public required string Text { get; init; }

    /// <summary><c>+</c> / <c>-</c> / 空格 / <c>@@</c>（分段行）。</summary>
    public required string Marker { get; init; }

    public string OldNumber { get; init; } = string.Empty;

    public string NewNumber { get; init; } = string.Empty;

    public bool IsAdded { get; init; }

    public bool IsRemoved { get; init; }

    public bool IsHeader { get; init; }

    /// <summary>尾注（<c>\ No newline at end of file</c>）：不算增删，用弱化色显示。</summary>
    public bool IsNote { get; init; }

    /// <summary>把 hunks 展开成可绑定的行序列。</summary>
    public static IReadOnlyList<DiffRow> FromHunks(IReadOnlyList<DiffHunk> hunks)
    {
        if (hunks.Count == 0)
        {
            return Array.Empty<DiffRow>();
        }

        var rows = new List<DiffRow>();
        foreach (var hunk in hunks)
        {
            rows.Add(new DiffRow
            {
                Text = hunk.Header,

                // header 文本自带 @@ 记号，Marker 再写一遍会显示成 "@@ @@ -1,3 +1,4 @@"
                Marker = string.Empty,
                IsHeader = true,
            });

            foreach (var line in hunk.Lines)
            {
                rows.Add(new DiffRow
                {
                    Text = line.Text,
                    Marker = line.Marker,
                    OldNumber = line.OldLineNumber?.ToString() ?? string.Empty,
                    NewNumber = line.NewLineNumber?.ToString() ?? string.Empty,
                    IsAdded = line.Kind == DiffLineKind.Added,
                    IsRemoved = line.Kind == DiffLineKind.Removed,
                    IsNote = line.Kind == DiffLineKind.NoNewline,
                });
            }
        }

        return rows;
    }

    /// <summary>
    /// 没有 hunk 时的说明文案。<paramref name="diff"/> 为 null 表示"还没选文件"。
    /// </summary>
    public static string ExplainEmpty(FileDiff? diff)
    {
        if (diff is null)
        {
            return "在上方选择一个文件，查看它的具体改动。";
        }

        if (diff.Error is not null)
        {
            return $"读取差异失败：{diff.Error}";
        }

        if (diff.IsNew && diff.Hunks.Count == 0)
        {
            return "未跟踪的新文件：git 不为其生成差异内容，提交后即可在历史中查看。";
        }

        if (diff.Hunks.Count == 0)
        {
            return "这个文件没有文本差异（可能是二进制内容，或只有模式/权限变化）。";
        }

        return string.Empty;
    }
}
