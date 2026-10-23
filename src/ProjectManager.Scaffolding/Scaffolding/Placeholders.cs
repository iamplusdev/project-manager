using ProjectManager.Scaffolding.Models;

namespace ProjectManager.Scaffolding.Scaffolding;

/// <summary>
/// 占位符替换（WP1.7）。支持 4 个占位符：
/// <c>{{name}}</c> / <c>{{description}}</c> / <c>{{date}}</c> / <c>{{template}}</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>未识别的 <c>{{...}}</c> 一律原样保留</b>，不报错、不清空。
/// 原因：模板文件内容可能合法地包含双花括号（Vue/Angular/Handlebars 模板、文档示例），
/// 若把未知占位符当错误，用户就必须为"写文档"而转义，得不偿失。
/// </para>
/// <para>
/// 键（落盘路径）上的占位符规则相同，但替换后若仍残留 <c>{{</c>，
/// 会因文件名非法而被 <see cref="ScaffoldGenerator"/> 拒绝——路径必须可判定，不能"保留待定"。
/// </para>
/// </remarks>
public static class Placeholders
{
    public const string Name = "{{name}}";
    public const string Description = "{{description}}";
    public const string Date = "{{date}}";
    public const string Template = "{{template}}";

    /// <summary>单次生成的占位符取值。</summary>
    public sealed record Values(string ProjectName, string ProjectDescription, string TemplateName, string Date)
    {
        public static Values From(GenerationRequest request, Template template, DateTimeOffset now)
            => new(request.ProjectName, request.Description, template.Name, now.ToString("yyyy-MM-dd"));
    }

    public static string Apply(string text, Values values)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf("{{", StringComparison.Ordinal) < 0)
        {
            return text;
        }

        return text
            .Replace(Name, values.ProjectName, StringComparison.Ordinal)
            .Replace(Description, values.ProjectDescription, StringComparison.Ordinal)
            .Replace(Template, values.TemplateName, StringComparison.Ordinal)
            .Replace(Date, values.Date, StringComparison.Ordinal);
    }
}

/// <summary>把模板内的文件内容读出来的抽象：生产实现读模板目录，测试可注入内存内容。</summary>
public interface ITemplateFileSource
{
    /// <summary>按模板内相对路径取文本；不存在返回 <c>null</c>。</summary>
    string? TryReadText(string templateRelativePath);
}

/// <summary>从模板配置文件所在目录读取 <c>files</c> 引用的文件。</summary>
public sealed class DirectoryTemplateFileSource : ITemplateFileSource
{
    private readonly string _root;

    public DirectoryTemplateFileSource(string root) => _root = root;

    public static DirectoryTemplateFileSource ForTemplate(Template template)
        => new(Path.GetDirectoryName(template.SourcePath) ?? string.Empty);

    public string? TryReadText(string templateRelativePath)
    {
        var full = Path.GetFullPath(Path.Combine(_root, templateRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }
}
