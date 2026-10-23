using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectManager.Scaffolding.Models;

namespace ProjectManager.Scaffolding.Scaffolding;

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
