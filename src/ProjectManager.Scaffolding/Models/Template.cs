namespace ProjectManager.Scaffolding.Models;

/// <summary>
/// 一套脚手架模板。字段与 Forge 的 <c>templates.json</c> 完全兼容，扩展字段全部可选。
/// </summary>
public sealed class Template
{
    public Template(
        string name,
        DirNode structure,
        IReadOnlyList<ModuleOption> modules,
        IReadOnlyDictionary<string, string> descriptions,
        string readmeQuickstart,
        IReadOnlyList<string> gitignore)
    {
        Name = name;
        Structure = structure;
        Modules = modules;
        Descriptions = descriptions;
        ReadmeQuickstart = readmeQuickstart;
        Gitignore = gitignore;
    }

    /// <summary>模板键（JSON 顶层键），也是对外展示名缺省值。</summary>
    public string Name { get; }

    public string DisplayName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Icon { get; init; } = string.Empty;

    /// <summary>结构树（顶层目录 → 子路径）。</summary>
    public DirNode Structure { get; }

    public IReadOnlyList<ModuleOption> Modules { get; }

    /// <summary>相对路径 → 中文说明，用于 structure.md。</summary>
    public IReadOnlyDictionary<string, string> Descriptions { get; }

    public string ReadmeQuickstart { get; }

    /// <summary>写入 .gitignore 的规则行（同时用于「忽略目录跳过占位」判定）。</summary>
    public IReadOnlyList<string> Gitignore { get; }

    /// <summary>内容注入：落盘相对路径（键可含占位符） → 模板内资源相对路径。</summary>
    public IReadOnlyDictionary<string, string> Files { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public NamingConvention Naming { get; init; } = NamingConvention.Default;

    /// <summary>组件候选白名单；空表示该模板的项目不参与组件识别。</summary>
    public IReadOnlyList<string> Components { get; init; } = Array.Empty<string>();

    public string ReleaseTagPrefix { get; init; } = string.Empty;

    public string MinAppVersion { get; init; } = string.Empty;

    /// <summary>加载来源（user / install / builtin），用于设置页只读展示与排障。</summary>
    public TemplateSource Source { get; init; } = TemplateSource.Builtin;

    /// <summary>模板来源文件路径（内置资源为空）。</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>模板配置文件的 SHA256（不含 <c>sha256:</c> 前缀），用于升级检测。</summary>
    public string ConfigHash { get; init; } = string.Empty;

    public int SchemaVersion { get; init; } = 1;

    /// <summary>步骤 1 卡片展示名：优先 displayName，否则回退键名。</summary>
    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;

    public IReadOnlyList<ModuleOption> DefaultModules => Modules;
}

/// <summary>模板来源优先级：用户目录 &gt; 安装目录 &gt; 内置资源。</summary>
public enum TemplateSource
{
    Builtin = 0,
    Install = 1,
    User = 2,
}
