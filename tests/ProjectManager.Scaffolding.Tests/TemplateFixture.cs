using ProjectManager.Scaffolding.Models;
using ProjectManager.Scaffolding.Templates;

namespace ProjectManager.Scaffolding.Tests;

/// <summary>在临时目录里搭一套模板配置（可带 <c>files</c> 引用的模板文件），供扩展能力测试使用。</summary>
public sealed class TemplateFixture : IDisposable
{
    private TemplateFixture(string directory, Template template)
    {
        Directory = directory;
        Template = template;
    }

    public string Directory { get; }

    public Template Template { get; }

    public static TemplateFixture Create(string json, IReadOnlyDictionary<string, string>? files = null)
    {
        var directory = FixtureLocator.CreateTempDirectory("tpl");
        var configPath = Path.Combine(directory, "templates.json");
        File.WriteAllText(configPath, json);

        if (files is not null)
        {
            foreach (var (relative, content) in files)
            {
                var path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }
        }

        var loader = new TemplateLoader(
            Path.Combine(directory, "__user__.json"),
            Path.Combine(directory, "__install__.json"));

        var result = loader.LoadFromFile(configPath, TemplateSource.User);
        return new TemplateFixture(directory, result.Templates.Values.First());
    }

    /// <summary>最小可用模板 JSON。<paramref name="extraFields"/> 用于追加 <c>files</c> 等可选字段。</summary>
    public static string MinimalJson(string templateName = "测试模板", string extraFields = "")
        => "{"
           + "\"" + templateName + "\": {"
           + "\"structure\": { \"src\": [], \"docs\": [] },"
           + "\"modules\": [],"
           + "\"descriptions\": { \"src\": \"源码\", \"docs\": \"文档\" },"
           + "\"readme_quickstart\": \"1. 写代码。\","
           + "\"gitignore\": [\"bin/\"]"
           + extraFields
           + "}"
           + "}";

    public void Dispose() => FixtureLocator.TryDelete(Directory);
}

/// <summary>一次生成的最小上下文（父目录 + 请求），减少每个用例的样板代码。</summary>
public sealed class ScaffoldContext : IDisposable
{
    public ScaffoldContext(string projectName = "MyProject")
    {
        Target = FixtureLocator.CreateTempDirectory("gen");
        ProjectName = projectName;
        ProjectPath = Path.Combine(Target, projectName);
    }

    public string Target { get; }

    public string ProjectName { get; }

    public string ProjectPath { get; }

    public GenerationRequest Request(
        IEnumerable<string>? modules = null,
        string description = "测试项目",
        bool dryRun = false)
        => new()
        {
            ProjectName = ProjectName,
            Description = description,
            TargetDirectory = Target,
            SelectedModules = modules?.ToArray() ?? Array.Empty<string>(),
            DryRun = dryRun,
        };

    public void Dispose() => FixtureLocator.TryDelete(Target);
}
