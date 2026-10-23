using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ProjectManager.Scaffolding.IO;

namespace ProjectManager.Scaffolding.Validation;

/// <summary>
/// 表单校验（WP1.3）。规则与 Forge 的 <c>validation.py</c> 逐条等价，
/// 差异仅在于长度按 <b>Unicode 码点</b> 计数（与 Python <c>len()</c> 一致），
/// 而不是 C# <c>string.Length</c> 的 UTF-16 码元数——否则 emoji 会被算作 2 字。
/// </summary>
public static partial class ProjectValidator
{
    public const int NameMaxLength = 50;
    public const int DescriptionMaxLength = 200;

    [GeneratedRegex(@"[\\/:*?""<>|]")]
    private static partial Regex IllegalNameChars();

    [GeneratedRegex(@"^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsReservedName();

    /// <summary>返回 <c>null</c> 表示通过；否则为可直接展示的中文错误文案（沿用 Forge 文案）。</summary>
    public static string? ValidateName(string? name)
    {
        var cleaned = (name ?? string.Empty).Trim();
        if (cleaned.Length == 0)
        {
            return "项目名称不能为空";
        }

        if (CodePointLength(cleaned) > NameMaxLength)
        {
            return $"项目名称过长 (最多{NameMaxLength}字)";
        }

        if (IllegalNameChars().IsMatch(cleaned))
        {
            return "包含非法字符： \\ / : * ? \" < > |";
        }

        if (cleaned is "." or "..")
        {
            return "项目名称不能是 . 或 ..";
        }

        if (WindowsReservedName().IsMatch(cleaned))
        {
            return "项目名称是 Windows 保留名称，请更换";
        }

        if (cleaned.EndsWith('.'))
        {
            return "项目名称不能以点结尾";
        }

        return null;
    }

    public static string? ValidateDescription(string? description)
    {
        var cleaned = (description ?? string.Empty).Trim();
        if (cleaned.Length == 0)
        {
            return "项目简介不能为空";
        }

        if (CodePointLength(cleaned) > DescriptionMaxLength)
        {
            return $"简介太长啦 (最多{DescriptionMaxLength}字)";
        }

        return null;
    }

    /// <summary>
    /// 生成路径校验。可写性探测与「是否存在」都走 <see cref="IFileSystem"/>，
    /// 便于用桩实现覆盖「只读介质」等异常路径。
    /// </summary>
    public static string? ValidateTargetDirectory(string? targetDirectory, IFileSystem fs)
    {
        var cleaned = (targetDirectory ?? string.Empty).Trim();
        if (cleaned.Length == 0)
        {
            return "请选择有效的生成路径";
        }

        if (!fs.DirectoryExists(cleaned))
        {
            return "生成路径不存在或不是文件夹";
        }

        if (!fs.IsWritable(cleaned))
        {
            return "生成路径不可写";
        }

        return null;
    }

    /// <summary>按 Unicode 码点计数（Python <c>len()</c> 语义）。</summary>
    public static int CodePointLength(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var count = 0;
        foreach (var _ in value.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    /// <summary>项目名 → 安全目录名（仅做保守清理，不做命名风格转换，风格见 <c>NameDeriver</c>）。</summary>
    public static string ToSafeDirectoryName(string name)
        => name.Trim().TrimEnd('.').Trim();

    internal static string FormatInvariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
