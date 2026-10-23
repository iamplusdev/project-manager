using ProjectManager.Scaffolding.Models;

namespace ProjectManager.Scaffolding.Scaffolding;

/// <summary>
/// 文本产物（WP1.8）：<c>README.md</c> / <c>structure.md</c> / <c>.gitignore</c>。
/// </summary>
/// <remarks>
/// <para>
/// 内容逐字对齐 Forge，<b>唯一有意的差异是换行统一为 LF</b>：
/// Forge 用 Python <c>Path.write_text</c>，在 Windows 上会把 <c>\n</c> 转成本地 <c>\r\n</c>；
/// 本项目统一 LF —— 原因有三：生成物多为 Git 仓库（行尾应由 <c>.gitattributes</c> 决定，而非生成器）、
/// 跨平台输出需确定性、G6 要求编码与格式统一。
/// </para>
/// <para>
/// 该差异在等价比对时按"归一化后一致"判定，并单独统计为 <c>EolOnly</c>，不以"哈希恰好相等"冒充等价。
/// </para>
/// </remarks>
public static class Documents
{
    public const string ReadmeFileName = "README.md";
    public const string StructureFileName = "structure.md";
    public const string GitignoreFileName = ".gitignore";

    public static string Readme(Template template, string projectName, string description)
        => $"# {projectName}\n\n"
           + $"> {description}\n\n"
           + "## 📂 目录结构\n\n"
           + "详情请参阅：[structure.md](./structure.md)\n\n"
           + "## 🚀 快速开始\n\n"
           + $"{template.ReadmeQuickstart}\n";

    /// <summary>
    /// 结构说明文档。刻意用「行数组 + join」而不是逐段 <c>Append</c>：
    /// 前者与 Forge 的实现形态一一对应，多一个空行的偏差能被直接看出（等价性靠结构，不靠运气）。
    /// </summary>
    public static string Structure(Template template, string projectName, DirNode tree)
    {
        var relatives = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var relative in TreeBuilder.FlattenRelative(tree))
        {
            relatives.Add(relative);
        }

        var lines = new List<string>
        {
            $"# {projectName} 项目结构说明",
            string.Empty,
            "```text",
            TreeBuilder.TreeString(projectName, tree),
            "```",
            string.Empty,
            "## 📖 文件夹作用说明",
            string.Empty,
        };

        foreach (var relative in relatives)
        {
            if (template.Descriptions.TryGetValue(relative, out var description) && !string.IsNullOrEmpty(description))
            {
                lines.Add($"- `{relative}` : {description}");
            }
        }

        return string.Join('\n', lines) + "\n";
    }

    public static string Gitignore(Template template)
        => template.Gitignore.Count == 0 ? string.Empty : string.Join('\n', template.Gitignore) + "\n";
}
