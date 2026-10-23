using ProjectManager.Scaffolding.Models;

namespace ProjectManager.Application.Wizard;

/// <summary>向导三步 + 结果页。单 ViewModel 贯穿，后退不丢数据。</summary>
public enum WizardStep
{
    Basic = 0,
    Template = 1,
    Preview = 2,
    Result = 3,
}

/// <summary>Step 3 的落盘策略（评审 P0-3 的三档）。</summary>
public enum GenerationStrategy
{
    /// <summary>幂等合并：已存在同名文档先备份为 <c>.bak</c>，用户既有 <c>.gitignore</c> 保留。</summary>
    Merge = 0,

    /// <summary>仅预览：不写任何字节，只渲染将发生的变更。</summary>
    PreviewOnly = 1,
}

/// <summary>Step 1 的模板卡片。</summary>
public sealed class TemplateCard
{
    public TemplateCard(Template template, bool isRecommended)
    {
        Template = template;
        IsRecommended = isRecommended;
    }

    public Template Template { get; }

    public string Key => Template.Name;

    public string Title => Template.Title;

    public string Description => string.IsNullOrWhiteSpace(Template.Description)
        ? $"{Template.Modules.Count} 个可选模块"
        : Template.Description;

    public bool IsRecommended { get; }

    public bool HasModules => Template.Modules.Count > 0;
}

/// <summary>Step 2 的模块勾选项。</summary>
public sealed class ModuleSelection : Mvvm.ObservableObject
{
    private bool _isSelected;

    public ModuleSelection(ModuleOption option, bool isSelected)
    {
        Option = option;
        _isSelected = isSelected;
    }

    public ModuleOption Option { get; }

    public string Key => Option.Key;

    public string Label => Option.Label;

    /// <summary>模块影响的顶层目录，用于勾选时说明"会多出哪些目录"。</summary>
    public string PathsSummary => string.Join("、", Option.Paths.Select(path => path.Split('/')[0]).Distinct());

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>预览树节点。<see cref="StatusLabel"/> 提供文字标签，不依赖颜色传达状态。</summary>
public sealed class PreviewNode : Mvvm.ObservableObject
{
    private bool _isExpanded;

    public PreviewNode(string name, string relativePath, PreviewNodeKind kind, PreviewStatus status)
    {
        Name = name;
        RelativePath = relativePath;
        Kind = kind;
        Status = status;
    }

    public string Name { get; }

    public string RelativePath { get; }

    public PreviewNodeKind Kind { get; }

    public PreviewStatus Status { get; }

    public List<PreviewNode> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsDirectory => Kind == PreviewNodeKind.Directory;

    /// <summary>文字标签（无障碍：状态不只靠颜色）。</summary>
    public string StatusLabel => Status switch
    {
        PreviewStatus.Create => "将创建",
        PreviewStatus.Backup => "将备份",
        PreviewStatus.Preserve => "保留",
        PreviewStatus.Ignored => "已忽略",
        PreviewStatus.Skip => "已存在",
        _ => string.Empty,
    };

    /// <summary>视觉符号（与文字标签并用，不作为唯一区分手段）。</summary>
    public string StatusGlyph => Status switch
    {
        PreviewStatus.Backup => "↺",
        PreviewStatus.Preserve => "·",
        PreviewStatus.Ignored => "—",
        PreviewStatus.Skip => "=",
        _ => "+",
    };

    public int TotalDirectoryCount => Descendants().Count(node => node.IsDirectory) + (IsDirectory ? 1 : 0);

    public int TotalFileCount => Descendants().Count(node => !node.IsDirectory) + (IsDirectory ? 0 : 1);

    private IEnumerable<PreviewNode> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var grandChild in child.Descendants())
            {
                yield return grandChild;
            }
        }
    }
}

public enum PreviewNodeKind
{
    Directory = 0,
    File = 1,
}

public enum PreviewStatus
{
    Create = 0,
    Backup = 1,
    Preserve = 2,
    Ignored = 3,
    Skip = 4,
}

/// <summary>Step 3 的三类清单计数。</summary>
public sealed record PreviewSummary(
    int DirectoriesToCreate,
    int FilesToWrite,
    int FilesToBackup,
    int FilesToPreserve,
    int Placeholders,
    bool TargetExisted)
{
    public static PreviewSummary Empty { get; } = new(0, 0, 0, 0, 0, false);

    public string CreateText => $"将创建 {DirectoriesToCreate} 个目录 · {FilesToWrite} 个文件（含 {Placeholders} 个占位）";

    public string BackupText => FilesToBackup > 0 ? $"将备份 {FilesToBackup} 个同名文件" : "无同名文件需要备份";

    public string PreserveText => FilesToPreserve > 0 ? $"将保留 {FilesToPreserve} 个既有文件" : "无既有文件需要保留";
}
