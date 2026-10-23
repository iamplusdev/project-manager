using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProjectManager.Scaffolding.Models;

namespace ProjectManager.Scaffolding.Templates;

/// <summary>模板加载结果。</summary>
public sealed class TemplateLoadResult
{
    public required IReadOnlyDictionary<string, Template> Templates { get; init; }

    public required TemplateSource Source { get; init; }

    public required string SourcePath { get; init; }

    /// <summary>非致命问题（未知字段、被忽略的条目）。不静默吞掉，交由设置页/日志展示。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 模板加载器。三源优先级：<c>%APPDATA%</c> &gt; 安装目录 <c>config/</c> &gt; 内置资源。
/// 任一来源校验失败时**回退下一优先级**，并把失败原因作为警告带出（不静默）。
/// </summary>
public sealed class TemplateLoader
{
    public const int SupportedSchemaVersion = 1;
    public const int MaxDirectoriesPerTemplate = 2000;
    public const int MaxFilesPerTemplate = 5000;

    private const string BuiltinResourceName = "ProjectManager.Scaffolding.Resources.templates.default.json";
    private const string MetaSchemaVersionKey = "$schemaVersion";

    private static readonly char[] IllegalSegmentChars = { '\\', ':', '*', '?', '"', '<', '>', '|' };

    private static readonly string[] KnownTemplateFields =
    {
        "structure", "modules", "descriptions", "readme_quickstart", "gitignore",
        "displayName", "description", "icon", "release", "minAppVersion",
        "files", "naming", "components",
    };

    private static readonly string[] KnownNamingStyles = { "pascal", "kebab", "snake" };

    private readonly string _userPath;
    private readonly string _installPath;

    public TemplateLoader(string userPath, string installPath)
    {
        _userPath = userPath;
        _installPath = installPath;
    }

    /// <summary>按真实产品形态解析三源路径（用户可写 / 安装目录只读 / 内置兜底）。</summary>
    public static TemplateLoader CreateDefault(string? userPath = null, string? installPath = null)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var resolvedUser = userPath ?? Path.Combine(appData, "ProjectManager", "templates.json");
        var resolvedInstall = installPath ?? Path.Combine(AppContext.BaseDirectory, "config", "templates.json");
        return new TemplateLoader(resolvedUser, resolvedInstall);
    }

    public string UserPath => _userPath;

    public string InstallPath => _installPath;

    /// <summary>
    /// 首次启动时把安装目录的模板复制到用户目录（复制而非引用：安装目录随更新被整体替换且通常不可写）。
    /// </summary>
    public bool TrySeedUserCopy()
    {
        if (File.Exists(_userPath) || !File.Exists(_installPath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(_userPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Copy(_installPath, _userPath, overwrite: false);
        return true;
    }

    /// <summary>按优先级加载；全部失败时抛出 <see cref="ScaffoldException"/>（PM-SCF-010）。</summary>
    public TemplateLoadResult Load()
    {
        var warnings = new List<string>();
        var failures = new List<string>();

        foreach (var (path, source) in new[] { (_userPath, TemplateSource.User), (_installPath, TemplateSource.Install) })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var result = LoadFromFile(path, source);
                return new TemplateLoadResult
                {
                    Templates = result.Templates,
                    Source = result.Source,
                    SourcePath = result.SourcePath,
                    Warnings = warnings.Concat(result.Warnings).ToArray(),
                };
            }
            catch (ScaffoldException ex)
            {
                failures.Add($"{source}：{path} → [{ex.ErrorCode}] {ex.Message}");
                warnings.Add($"[{ex.ErrorCode}] 模板来源已跳过（{source}）：{ex.Message}");
            }
        }

        try
        {
            var builtin = LoadFromEmbedded(BuiltinResourceName);
            return new TemplateLoadResult
            {
                Templates = builtin.Templates,
                Source = TemplateSource.Builtin,
                SourcePath = BuiltinResourceName,
                Warnings = warnings.Concat(builtin.Warnings).ToArray(),
            };
        }
        catch (ScaffoldException ex)
        {
            failures.Add($"builtin：{BuiltinResourceName} → [{ex.ErrorCode}] {ex.Message}");
        }

        throw new ScaffoldException(
            ScaffoldErrorCode.TemplateInvalid,
            "没有可用的模板配置：所有来源均加载失败。")
        {
            Details = string.Join(" | ", failures.Concat(warnings)),
        };
    }

    public TemplateLoadResult LoadFromFile(string path, TemplateSource source)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ScaffoldException(ScaffoldErrorCode.TemplateInvalid, $"模板配置文件读取失败：{path}", ex)
            {
                Details = "templates.json",
            };
        }

        return LoadFromBytes(bytes, source, path);
    }

    public TemplateLoadResult LoadFromEmbedded(string resourceName)
    {
        using var stream = typeof(TemplateLoader).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new ScaffoldException(ScaffoldErrorCode.TemplateInvalid, $"内置模板资源缺失：{resourceName}")
            {
                Details = resourceName,
            };

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return LoadFromBytes(buffer.ToArray(), TemplateSource.Builtin, resourceName);
    }

    public TemplateLoadResult LoadFromBytes(byte[] bytes, TemplateSource source, string sourcePath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        // 去掉 BOM 再解析：用户可能拿记事本另存过，不应因 BOM 判为非法
        var text = new UTF8Encoding(false).GetString(TrimBom(bytes));

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            throw new ScaffoldException(ScaffoldErrorCode.TemplateInvalid, $"模板配置 JSON 语法错误：{sourcePath}", ex)
            {
                Details = $"位置 行 {ex.LineNumber + 1} 列 {ex.BytePositionInLine + 1}",
            };
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Any())
            {
                throw new ScaffoldException(ScaffoldErrorCode.TemplateInvalid, $"模板配置应是非空对象：{sourcePath}");
            }

            var warnings = new List<string>();
            var schemaVersion = ReadSchemaVersion(root, warnings);
            if (schemaVersion > SupportedSchemaVersion)
            {
                throw new ScaffoldException(
                    ScaffoldErrorCode.TemplateInvalid,
                    $"模板配置版本过高（$schemaVersion={schemaVersion}），当前应用仅支持 {SupportedSchemaVersion}，请升级应用。")
                {
                    Details = MetaSchemaVersionKey,
                };
            }

            var templates = new Dictionary<string, Template>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name == MetaSchemaVersionKey)
                {
                    continue;
                }

                var template = BuildTemplate(property.Name, property.Value, source, sourcePath, hash, schemaVersion, warnings);
                templates[template.Name] = template;
            }

            if (templates.Count == 0)
            {
                throw new ScaffoldException(ScaffoldErrorCode.TemplateInvalid, $"模板配置中没有任何模板：{sourcePath}");
            }

            return new TemplateLoadResult
            {
                Templates = templates,
                Source = source,
                SourcePath = sourcePath,
                Warnings = warnings,
            };
        }
    }

    private static byte[] TrimBom(byte[] bytes)
        => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes[3..]
            : bytes;

    private static int ReadSchemaVersion(JsonElement root, List<string> warnings)
    {
        if (!root.TryGetProperty(MetaSchemaVersionKey, out var value))
        {
            return 1;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var version) || version < 1)
        {
            warnings.Add($"忽略非法的 {MetaSchemaVersionKey}（应为正整数），按 1 处理。");
            return 1;
        }

        return version;
    }

    private static Template BuildTemplate(
        string name,
        JsonElement body,
        TemplateSource source,
        string sourcePath,
        string hash,
        int schemaVersion,
        List<string> warnings)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(name, "模板配置应是非空对象。");
        }

        foreach (var property in body.EnumerateObject())
        {
            if (!KnownTemplateFields.Contains(property.Name, StringComparer.Ordinal))
            {
                warnings.Add($"模板「{name}」包含未知字段：{property.Name}（已忽略，请检查是否拼写错误或需要升级应用）。");
            }
        }

        var structure = ParseStructure(name, body);
        var descriptions = ParseDescriptions(name, body);
        var quickstart = ParseQuickstart(name, body);
        var modules = ParseModules(name, body);
        var gitignore = ParseGitignore(name, body);
        var files = ParseFiles(name, body);
        var naming = ParseNaming(name, body);
        var components = ParseComponents(name, body);

        var template = new Template(name, structure, modules, descriptions, quickstart, gitignore)
        {
            DisplayName = ReadOptionalString(body, "displayName"),
            Description = ReadOptionalString(body, "description"),
            Icon = ReadOptionalString(body, "icon"),
            MinAppVersion = ReadOptionalString(body, "minAppVersion"),
            ReleaseTagPrefix = ReadReleaseTagPrefix(body),
            Files = files,
            Naming = naming,
            Components = components,
            Source = source,
            SourcePath = sourcePath,
            ConfigHash = hash,
            SchemaVersion = schemaVersion,
        };

        EnforceLimits(template);
        return template;
    }

    private static ScaffoldException Invalid(string templateName, string message)
        => new(ScaffoldErrorCode.TemplateInvalid, $"模板「{templateName}」{message}")
        {
            Details = templateName,
        };

    private static DirNode ParseStructure(string name, JsonElement body)
    {
        if (!body.TryGetProperty("structure", out var structure) || structure.ValueKind != JsonValueKind.Object
            || !structure.EnumerateObject().Any())
        {
            throw Invalid(name, "的 structure 应是非空对象。");
        }

        var root = new DirNode(string.Empty);
        foreach (var property in structure.EnumerateObject())
        {
            var top = ValidateSegment(name, "structure", property.Name);
            var node = root.FindOrAdd(top);
            foreach (var relative in ReadPathList(name, "structure", property.Value))
            {
                Insert(node, relative);
            }
        }

        return root;
    }

    private static void Insert(DirNode node, string relative)
    {
        var current = node;
        foreach (var segment in relative.Split('/'))
        {
            current = current.FindOrAdd(segment);
        }
    }

    private static IReadOnlyList<string> ReadPathList(string templateName, string field, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return new[] { NormalizeRelative(templateName, field, value.GetString() ?? string.Empty) };
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(templateName, $"的 {field} 目录列表应是数组。");
        }

        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw Invalid(templateName, $"的 {field} 目录列表只能包含字符串。");
            }

            result.Add(NormalizeRelative(templateName, field, item.GetString() ?? string.Empty));
        }

        return result;
    }

    private static string NormalizeRelative(string templateName, string field, string raw)
    {
        var segments = raw.Split('/');
        var normalized = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            normalized.Add(ValidateSegment(templateName, field, segment));
        }

        return string.Join('/', normalized);
    }

    private static string ValidateSegment(string templateName, string field, string segment)
    {
        var cleaned = segment.Trim();
        if (cleaned.Length == 0 || cleaned is "." or "..")
        {
            throw Invalid(templateName, $"的 {field} 包含非法目录名：\"{segment}\"（不允许空、. 或 ..）。");
        }

        if (cleaned.IndexOfAny(IllegalSegmentChars) >= 0)
        {
            throw Invalid(templateName, $"的 {field} 包含非法目录名：\"{segment}\"（含 \\ : * ? \" < > | 之一）。");
        }

        if (cleaned.StartsWith('/') || cleaned.EndsWith('.') || cleaned.EndsWith(' '))
        {
            throw Invalid(templateName, $"的 {field} 包含非法目录名：\"{segment}\"（不允许以 / 开头或以点/空格结尾）。");
        }

        if (cleaned.Length >= 2 && cleaned[1] == ':')
        {
            throw Invalid(templateName, $"的 {field} 不允许绝对路径：\"{segment}\"。");
        }

        return cleaned;
    }

    private static IReadOnlyDictionary<string, string> ParseDescriptions(string name, JsonElement body)
    {
        if (!body.TryGetProperty("descriptions", out var descriptions) || descriptions.ValueKind != JsonValueKind.Object
            || !descriptions.EnumerateObject().Any())
        {
            throw Invalid(name, "缺少非空的 descriptions。");
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in descriptions.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw Invalid(name, $"的 descriptions「{property.Name}」应为字符串。");
            }

            map[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        return map;
    }

    private static string ParseQuickstart(string name, JsonElement body)
    {
        if (!body.TryGetProperty("readme_quickstart", out var quickstart) || quickstart.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(quickstart.GetString()))
        {
            throw Invalid(name, "缺少 readme_quickstart。");
        }

        return quickstart.GetString()!;
    }

    private static IReadOnlyList<ModuleOption> ParseModules(string name, JsonElement body)
    {
        if (!body.TryGetProperty("modules", out var modules))
        {
            return Array.Empty<ModuleOption>();
        }

        if (modules.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(name, "的 modules 应是数组。");
        }

        var result = new List<ModuleOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in modules.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw Invalid(name, "的模块配置应是对象。");
            }

            var key = ReadOptionalString(entry, "key").Trim();
            var label = ReadOptionalString(entry, "label").Trim();
            if (key.Length == 0 || label.Length == 0)
            {
                throw Invalid(name, "的模块缺少 key 或 label。");
            }

            if (!seen.Add(key))
            {
                throw Invalid(name, $"存在重复的模块 key：{key}");
            }

            var paths = entry.TryGetProperty("paths", out var pathsElement)
                ? ReadPathList(name, $"modules.{key}.paths", pathsElement)
                : Array.Empty<string>();

            if (paths.Count == 0)
            {
                throw Invalid(name, $"的模块 {key} 缺少 paths。");
            }

            result.Add(new ModuleOption(key, label, paths));
        }

        return result;
    }

    private static IReadOnlyList<string> ParseGitignore(string name, JsonElement body)
    {
        if (!body.TryGetProperty("gitignore", out var gitignore))
        {
            return Array.Empty<string>();
        }

        if (gitignore.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(name, "的 gitignore 应是数组。");
        }

        return gitignore.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(line => line.Trim().Length > 0)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, string> ParseFiles(string name, JsonElement body)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!body.TryGetProperty("files", out var files))
        {
            return map;
        }

        if (files.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(name, "的 files 应是对象（落盘路径 → 模板内文件）。");
        }

        foreach (var property in files.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                throw Invalid(name, $"的 files「{property.Name}」应是非空字符串（模板内文件路径）。");
            }

            ValidateTargetKey(name, property.Name);
            map[property.Name] = property.Value.GetString()!.Trim();
        }

        return map;
    }

    /// <summary>校验 files 的键：允许 <c>{{name}}</c> 等占位符，但其余段必须合法。</summary>
    private static void ValidateTargetKey(string templateName, string key)
    {
        foreach (var segment in key.Split('/'))
        {
            var withoutTokens = System.Text.RegularExpressions.Regex.Replace(segment, "\\{\\{\\w+\\}\\}", "x");
            ValidateSegment(templateName, "files", withoutTokens);
        }
    }

    private static NamingConvention ParseNaming(string name, JsonElement body)
    {
        if (!body.TryGetProperty("naming", out var naming))
        {
            return NamingConvention.Default;
        }

        if (naming.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(name, "的 naming 应是对象。");
        }

        return new NamingConvention(
            ReadNamingStyle(name, naming, "namespaceCase"),
            ReadNamingStyle(name, naming, "packageCase"),
            ReadNamingStyle(name, naming, "dirCase"));
    }

    private static string ReadNamingStyle(string templateName, JsonElement naming, string field)
    {
        var value = ReadOptionalString(naming, field).Trim().ToLowerInvariant();
        if (value.Length == 0)
        {
            return NamingConvention.Default.NamespaceCase.Length > 0 && field == "namespaceCase"
                ? NamingConvention.Default.NamespaceCase
                : field switch
                {
                    "packageCase" => NamingConvention.Default.PackageCase,
                    "dirCase" => NamingConvention.Default.DirCase,
                    _ => NamingConvention.Default.NamespaceCase,
                };
        }

        if (!KnownNamingStyles.Contains(value, StringComparer.Ordinal))
        {
            throw Invalid(templateName, $"的 naming.{field} 只能是 pascal / kebab / snake，实际为「{value}」。");
        }

        return value;
    }

    private static IReadOnlyList<string> ParseComponents(string name, JsonElement body)
    {
        if (!body.TryGetProperty("components", out var components))
        {
            return Array.Empty<string>();
        }

        if (components.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(name, "的 components 应是字符串数组。");
        }

        var result = new List<string>();
        foreach (var item in components.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw Invalid(name, "的 components 只能包含字符串。");
            }

            result.Add(ValidateSegment(name, "components", item.GetString() ?? string.Empty));
        }

        return result;
    }

    private static string ReadOptionalString(JsonElement element, string field)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(field, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return value.GetString() ?? string.Empty;
    }

    private static string ReadReleaseTagPrefix(JsonElement body)
    {
        if (!body.TryGetProperty("release", out var release) || release.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return ReadOptionalString(release, "tagPrefix");
    }

    private static void EnforceLimits(Template template)
    {
        var (dirs, files) = CountNodes(template.Structure);
        if (dirs > MaxDirectoriesPerTemplate)
        {
            throw new ScaffoldException(
                ScaffoldErrorCode.TemplateTooLarge,
                $"模板「{template.Name}」目录数超限：{dirs} > {MaxDirectoriesPerTemplate}。")
            {
                Details = $"structure（{dirs} 个目录）",
            };
        }

        if (files > MaxFilesPerTemplate)
        {
            throw new ScaffoldException(
                ScaffoldErrorCode.TemplateTooLarge,
                $"模板「{template.Name}」文件声明数超限：{files} > {MaxFilesPerTemplate}。")
            {
                Details = $"files（{files} 项）",
            };
        }

        if (template.Files.Count > MaxFilesPerTemplate)
        {
            throw new ScaffoldException(
                ScaffoldErrorCode.TemplateTooLarge,
                $"模板「{template.Name}」files 条目超限：{template.Files.Count} > {MaxFilesPerTemplate}。")
            {
                Details = "files",
            };
        }
    }

    private static (int Directories, int Files) CountNodes(DirNode node)
    {
        var dirs = 0;
        var files = 0;
        foreach (var child in node.Children)
        {
            dirs++;
            var (d, f) = CountNodes(child);
            dirs += d;
            files += f;
        }

        return (dirs, files);
    }

    internal static bool IsKnownNamingStyle(string value) => KnownNamingStyles.Contains(value, StringComparer.Ordinal);

    internal static string FormatInvariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
