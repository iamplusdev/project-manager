using System.Security.Cryptography;
using System.Text;
using ProjectManager.Scaffolding;
using ProjectManager.Scaffolding.Models;
using ProjectManager.Scaffolding.Templates;

namespace ProjectManager.Application.Templates;

/// <summary>
/// 一条模板配置问题。错误与警告共用这个形状，靠 <see cref="IsWarning"/> 区分——
/// 拆成两个类型只会让 UI 写两套几乎相同的渲染。
/// </summary>
/// <param name="Code">错误码（PM-SCF-010 / PM-SCF-011），配置级问题也归到这两个码上。</param>
/// <param name="Template">模板名；配置级问题（JSON 语法、版本过高）为 null。</param>
/// <param name="Field">字段路径；定位不到字段时为 null。</param>
public sealed record TemplateIssue(
    string Code,
    string? Template,
    string? Field,
    string Message,
    bool IsWarning)
{
    /// <summary>
    /// 一行可复制的定位串：<c>[PM-SCF-010] 模板「X」· modules：缺少 paths。</c>
    /// </summary>
    /// <remarks>
    /// 引擎抛出的文案本身已带「模板「X」的 field」前缀，再拼一次定位串会变成
    /// "模板「X」· field：模板「X」的 field …" 这样的重复句。所以这里先看文案里有没有，
    /// 有就只加错误码——定位信息宁少不重。
    /// </remarks>
    public string Describe()
    {
        var scope = Template is null
            ? "整份配置"
            : Field is null
                ? $"模板「{Template}」"
                : $"模板「{Template}」· {Field}";

        return Message.Contains(scope, StringComparison.Ordinal)
            || (Template is not null && Message.Contains($"模板「{Template}」", StringComparison.Ordinal))
                ? $"[{Code}] {Message}"
                : $"[{Code}] {scope}：{Message}";
    }
}

/// <summary>模板在导入预览里的摘要行。</summary>
public sealed record TemplateEntry(
    string Name,
    string Title,
    int Directories,
    int Modules,
    int FileTargets)
{
    public string Detail => $"{Directories} 个目录 · {Modules} 个模块 · {FileTargets} 个注入文件";

    public string Describe() => $"{Title}（{Detail}）";

    /// <summary>从已加载的模板生成摘要行（设置页展示用，与导入预览共用同一份口径）。</summary>
    public static TemplateEntry FromTemplate(Template template) => new(
        template.Name,
        template.Title,
        CountDirectories(template.Structure),
        template.Modules.Count,
        template.Files.Count);

    private static int CountDirectories(DirNode node)
    {
        var count = 0;
        foreach (var child in node.Children)
        {
            count += 1 + CountDirectories(child);
        }

        return count;
    }
}

/// <summary>对一份模板配置的只读检查结果。不落盘、不改任何文件。</summary>
public sealed record TemplateInspection(
    bool IsValid,
    string SourcePath,
    string Hash,
    IReadOnlyList<TemplateEntry> Entries,
    IReadOnlyList<TemplateIssue> Issues)
{
    public IReadOnlyList<TemplateIssue> Errors =>
        Issues.Where(issue => !issue.IsWarning).ToArray();

    public IReadOnlyList<TemplateIssue> Warnings =>
        Issues.Where(issue => issue.IsWarning).ToArray();

    public string Summary => IsValid
        ? $"共 {Entries.Count} 个模板，检查通过"
        : Errors.Count > 0
            ? Errors[0].Describe()
            : "配置不可用";
}

/// <summary>导入结果。<see cref="BackupPath"/> 非空表示旧配置已留存，可据此回退。</summary>
public sealed record TemplateImportOutcome(
    bool Succeeded,
    TemplateInspection Inspection,
    string? BackupPath,
    string? Failure)
{
    public static TemplateImportOutcome Fail(TemplateInspection inspection, string failure) =>
        new(false, inspection, null, failure);
}

/// <summary>
/// 模板库维护：检查 / 导入 / 停用用户配置。
/// </summary>
/// <remarks>
/// 为什么要有「停用」而不是删除：用户配置文件一旦损坏，<see cref="TemplateLoader.Load"/>
/// 会自动回退到安装目录，但那条回退路径是**隐式**的——用户看不出"现在用的其实是安装目录的模板"。
/// 提供显式的停用动作（改名为 .bak），回退就从"碰巧还能用"变成"用户知情的选择"。
/// </remarks>
public sealed class TemplateLibraryService
{
    private readonly string _userPath;
    private readonly TemplateLoader _loader;
    private readonly Func<DateTime> _clock;

    /// <param name="clock">
    /// 时间来源。默认取系统时钟；测试里固定住才能复现"同一毫秒连续备份"这类撞名场景
    /// （靠真实时钟碰运气验证不了去重逻辑）。
    /// </param>
    public TemplateLibraryService(string userPath, TemplateLoader? loader = null, Func<DateTime>? clock = null)
    {
        _userPath = Path.GetFullPath(userPath);
        _loader = loader ?? TemplateLoader.CreateDefault(userPath);
        _clock = clock ?? (() => DateTime.Now);
    }

    public string UserPath => _userPath;

    public bool HasUserConfig => File.Exists(_userPath);

    /// <summary>检查一份配置文件，不落盘、不动用户目录。</summary>
    public TemplateInspection Inspect(string path)
    {
        var full = Path.GetFullPath(path);

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Invalid(
                full,
                string.Empty,
                new TemplateIssue("PM-SCF-010", null, null, $"无法读取文件：{ex.Message}", IsWarning: false));
        }

        return InspectBytes(bytes, full);
    }

    /// <summary>
    /// 导入一份配置：先检查，通过才写入用户目录；旧文件先备份再覆盖。
    /// 检查不过时**一个字节都不写**——这是回退能力的前提。
    /// </summary>
    public TemplateImportOutcome Import(string path)
    {
        var full = Path.GetFullPath(path);

        if (IsSamePath(full, _userPath))
        {
            // 用户直接选中了当前配置：等价于"重新检查"，不该备份自己再覆盖自己
            var self = Inspect(full);
            return self.IsValid
                ? new TemplateImportOutcome(true, self, null, null)
                : TemplateImportOutcome.Fail(self, self.Summary);
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var failed = Invalid(
                full,
                string.Empty,
                new TemplateIssue("PM-SCF-010", null, null, $"无法读取文件：{ex.Message}", IsWarning: false));
            return TemplateImportOutcome.Fail(failed, failed.Summary);
        }

        var inspection = InspectBytes(bytes, full);
        if (!inspection.IsValid)
        {
            return TemplateImportOutcome.Fail(inspection, inspection.Summary);
        }

        string? backup = null;
        try
        {
            var directory = Path.GetDirectoryName(_userPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(_userPath))
            {
                backup = NextBackupPath();
                File.Copy(_userPath, backup, overwrite: false);
            }

            // 先写临时文件再替换：中途失败时用户目录里不会留下半份 JSON
            var temp = _userPath + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, _userPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TemplateImportOutcome.Fail(inspection, $"写入用户配置失败：{ex.Message}");
        }

        return new TemplateImportOutcome(true, inspection, backup, null);
    }

    /// <summary>停用用户配置（改名为 .bak），让加载器回退到安装目录或内置模板。返回备份路径。</summary>
    public string? DisableUserConfig()
    {
        if (!File.Exists(_userPath))
        {
            return null;
        }

        var backup = NextBackupPath();
        File.Move(_userPath, backup);
        return backup;
    }

    private TemplateInspection InspectBytes(byte[] bytes, string sourcePath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        try
        {
            var loaded = _loader.LoadFromBytes(bytes, TemplateSource.User, sourcePath);
            var entries = loaded.Templates.Values
                .OrderBy(template => template.Name, StringComparer.Ordinal)
                .Select(TemplateEntry.FromTemplate)
                .ToArray();

            var warnings = loaded.Warnings
                .Select(line => new TemplateIssue("PM-SCF-010", null, null, line, IsWarning: true))
                .ToArray();

            return new TemplateInspection(true, sourcePath, hash, entries, warnings);
        }
        catch (ScaffoldException ex)
        {
            return Invalid(
                sourcePath,
                hash,
                new TemplateIssue(ex.ErrorCode, ex.TemplateName, ex.Field, ex.Message, IsWarning: false));
        }
    }

    private static TemplateInspection Invalid(string sourcePath, string hash, TemplateIssue issue) =>
        new(false, sourcePath, hash, Array.Empty<TemplateEntry>(), new[] { issue });

    /// <summary>备份名带毫秒；撞名时（时钟精度低、时间被回拨）用序号让开，绝不覆盖上一个备份。</summary>
    private string NextBackupPath()
    {
        var stamp = _clock().ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        var candidate = $"{_userPath}.{stamp}.bak";
        var index = 1;
        while (File.Exists(candidate))
        {
            candidate = $"{_userPath}.{stamp}-{index}.bak";
            index++;
        }

        return candidate;
    }

    private static bool IsSamePath(string left, string right) =>
        string.Equals(
            left.Replace('\\', '/').TrimEnd('/'),
            right.Replace('\\', '/').TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);
}
