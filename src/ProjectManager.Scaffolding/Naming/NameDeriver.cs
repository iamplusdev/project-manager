using System.Text;
using ProjectManager.Scaffolding.Models;

namespace ProjectManager.Scaffolding.Naming;

/// <summary>
/// 命名派生（WP1.4）。把项目名派生成「根命名空间 / 包名 / 目录名」三种风格。
/// </summary>
/// <remarks>
/// 规则在此 <b>冻结</b>，M2 只展示不重算（开发计划 3.3 风险对策）。
/// 词边界规则固定为三条，不做启发式：
/// <list type="number">
/// <item>非字母数字字符一律视为分隔符（<c>my_project</c>、<c>my-project</c> 等价）；</item>
/// <item>小写（或数字）后紧跟大写处断开（<c>myProject</c> → <c>my</c>+<c>Project</c>）；</item>
/// <item>字母与数字交界处断开（<c>utils2</c> → <c>utils</c>+<c>2</c>）。</item>
/// </list>
/// 连续大写视为一个词（<c>HTTPServer</c> → <c>HTTPServer</c>），不做专有名词识别。
/// 中文等无大小写的文字整体成词。
/// </remarks>
public static class NameDeriver
{
    /// <summary>派生结果。<see cref="DirectoryText"/> 仅用于展示，<b>不用于改写项目目录名</b>。</summary>
    public sealed record DerivedNames(
        string NamespaceText,
        string PackageText,
        string DirectoryText,
        IReadOnlyList<string> Words);

    public static DerivedNames Derive(string projectName, NamingConvention? convention = null)
    {
        var naming = convention ?? NamingConvention.Default;
        var words = SplitWords(projectName);

        return new DerivedNames(
            Join(words, naming.NamespaceCase),
            Join(words, naming.PackageCase),
            Join(words, naming.DirCase),
            words);
    }

    /// <summary>按三条固定规则切词；返回小写化后的词序列（无大小写概念的文字保持原样）。</summary>
    public static IReadOnlyList<string> SplitWords(string value)
    {
        var words = new List<string>();
        if (string.IsNullOrEmpty(value))
        {
            return words;
        }

        var current = new StringBuilder();
        char? previous = null;

        void Flush()
        {
            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        foreach (var rune in value.EnumerateRunes())
        {
            if (!Rune.IsLetterOrDigit(rune))
            {
                Flush();
                previous = null;
                continue;
            }

            var ch = rune.ToString();
            var isUpper = Rune.IsUpper(rune);
            var isDigit = Rune.IsDigit(rune);

            if (previous is { } prev)
            {
                var prevUpper = char.IsUpper(prev);
                var prevDigit = char.IsDigit(prev);

                // 规则 2：小写/数字 → 大写 断开
                if (isUpper && !prevUpper)
                {
                    Flush();
                }
                // 规则 3：字母 ↔ 数字 交界断开
                else if (isDigit != prevDigit)
                {
                    Flush();
                }
            }

            current.Append(ch);
            previous = ch.Length == 1 ? ch[0] : '\0';
        }

        Flush();
        return words;
    }

    private static string Join(IReadOnlyList<string> words, string style)
    {
        if (words.Count == 0)
        {
            return string.Empty;
        }

        return style switch
        {
            "kebab" => string.Join('-', words.Select(word => word.ToLowerInvariant())),
            "snake" => string.Join('_', words.Select(word => word.ToLowerInvariant())),
            _ => string.Concat(words.Select(Capitalize)),
        };
    }

    private static string Capitalize(string word)
    {
        if (word.Length == 0)
        {
            return word;
        }

        var lower = word.ToLowerInvariant();
        return char.ToUpperInvariant(lower[0]) + lower[1..];
    }
}
