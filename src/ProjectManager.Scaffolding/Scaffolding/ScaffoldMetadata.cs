using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ProjectManager.Scaffolding.IO;
using ProjectManager.Scaffolding.Models;

namespace ProjectManager.Scaffolding.Scaffolding;

/// <summary>读回生成元数据。字段缺失按空值处理——旧版本生成的项目也要能读。</summary>
public sealed record ScaffoldMetadataInfo(
    string ProjectName,
    string Template,
    string TemplateHash,
    int SchemaVersion,
    IReadOnlyList<string> Modules,
    string GeneratedAt)
{
    /// <summary>是否已有可比对的模板指纹。</summary>
    public bool HasHash => TemplateHash.Length > 0;
}

/// <summary>
/// 生成元数据（WP1.9 / G10）。写在项目根的 <c>.pm-scaffold.json</c>，用于：
/// <list type="bullet">
/// <item>识别"这个项目是 PM 生成的"，从而支持模板升级后的"只加不删"补结构；</item>
/// <item>重现问题时有据可查（模板名 + hash + 模块 + 引擎版本）。</item>
/// </list>
/// </summary>
/// <remarks>
/// 刻意<b>不</b>加入项目自身的 <c>.gitignore</c>：它是项目的溯源事实，应与代码一同入库。
/// 文件不含任何绝对路径，避免把用户目录结构泄露进仓库。
/// </remarks>
public static class ScaffoldMetadata
{
    public const string FileName = ".pm-scaffold.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Build(GenerationRequest request, Template template, DateTimeOffset now, string engineVersion)
    {
        var payload = new Payload
        {
            Engine = "ProjectManager.Scaffolding",
            EngineVersion = engineVersion,
            Template = template.Name,
            TemplateHash = template.ConfigHash,
            SchemaVersion = template.SchemaVersion,
            ProjectName = request.ProjectName,
            Modules = request.SelectedModules.ToArray(),
            GeneratedAt = now.ToString("O"),
        };

        return JsonSerializer.Serialize(payload, SerializerOptions) + "\n";
    }

    /// <summary>
    /// 读回项目根的 <c>.pm-scaffold.json</c>。不存在、损坏或不是对象一律返回 <c>null</c>，
    /// 由调用方降级——元数据读不出来不该阻断"打开项目"。
    /// </summary>
    public static ScaffoldMetadataInfo? TryRead(IFileSystem fileSystem, string projectRoot)
    {
        var path = Path.Combine(projectRoot, FileName);
        if (!fileSystem.FileExists(path))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(fileSystem.ReadAllText(path));
            if (node is not JsonObject root)
            {
                return null;
            }

            return new ScaffoldMetadataInfo(
                ReadString(root, "projectName") ?? string.Empty,
                ReadString(root, "template") ?? string.Empty,
                ReadString(root, "templateHash") ?? string.Empty,
                ReadInt(root, "schemaVersion"),
                ReadModules(root),
                ReadString(root, "generatedAt") ?? string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 只更新模板指纹（与可选的 schemaVersion / 升级时间），**保留文件里的其它字段**。
    /// 用于"补结构"之后把项目标记为已对齐，避免下次又提示升级。
    /// </summary>
    public static bool TryUpdateHash(
        IFileSystem fileSystem,
        string projectRoot,
        string templateHash,
        int schemaVersion,
        DateTimeOffset at)
    {
        var path = Path.Combine(projectRoot, FileName);
        if (!fileSystem.FileExists(path))
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(fileSystem.ReadAllText(path)) is not JsonObject root)
            {
                return false;
            }

            root["templateHash"] = templateHash;
            root["schemaVersion"] = schemaVersion;
            root["upgradedAt"] = at.ToString("O");

            fileSystem.WriteAllText(path, root.ToJsonString(SerializerOptions) + "\n");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonObject root, string propertyName)
    {
        var node = root[propertyName];
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    private static int ReadInt(JsonObject root, string propertyName)
    {
        var node = root[propertyName];
        return node is JsonValue value && value.TryGetValue<int>(out var number) ? number : 1;
    }

    private static IReadOnlyList<string> ReadModules(JsonObject root)
    {
        if (root["modules"] is not JsonArray array)
        {
            return Array.Empty<string>();
        }

        var modules = new List<string>();
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
            {
                modules.Add(text);
            }
        }

        return modules;
    }

    private sealed class Payload
    {
        [JsonPropertyName("engine")]
        public string Engine { get; init; } = string.Empty;

        [JsonPropertyName("engineVersion")]
        public string EngineVersion { get; init; } = string.Empty;

        [JsonPropertyName("template")]
        public string Template { get; init; } = string.Empty;

        [JsonPropertyName("templateHash")]
        public string TemplateHash { get; init; } = string.Empty;

        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("projectName")]
        public string ProjectName { get; init; } = string.Empty;

        [JsonPropertyName("modules")]
        public string[] Modules { get; init; } = Array.Empty<string>();

        [JsonPropertyName("generatedAt")]
        public string GeneratedAt { get; init; } = string.Empty;
    }
}
