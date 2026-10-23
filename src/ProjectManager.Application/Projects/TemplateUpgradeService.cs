using ProjectManager.Scaffolding.IO;
using ProjectManager.Scaffolding.Models;
using ProjectManager.Scaffolding.Scaffolding;

namespace ProjectManager.Application.Projects;

/// <summary>项目与当前模板的对齐状态。</summary>
public enum UpgradeState
{
    /// <summary>外部项目，或缺少 <c>.pm-scaffold.json</c>，无从比对。</summary>
    NotManaged,

    /// <summary>项目记录了模板名，但当前配置里已没有该模板。</summary>
    TemplateMissing,

    /// <summary>模板指纹一致，无需动作。</summary>
    UpToDate,

    /// <summary>模板已变，且按模块选择推算出的结构有缺失目录。</summary>
    Outdated,
}

/// <summary>升级预览：只包含"要补什么"，永远不包含"要删什么"。</summary>
public sealed class UpgradePlan
{
    public required UpgradeState State { get; init; }

    /// <summary>模板声明了、磁盘上缺失的相对目录（<c>/</c> 分隔，父在前）。</summary>
    public IReadOnlyList<string> MissingDirectories { get; init; } = Array.Empty<string>();

    /// <summary>缺失的空叶子目录所对应的占位文件（<c>.gitkeep</c>）。</summary>
    public IReadOnlyList<string> MissingPlaceholders { get; init; } = Array.Empty<string>();

    /// <summary>可直接展示的结论。</summary>
    public string Message { get; init; } = string.Empty;

    public bool HasWork => MissingDirectories.Count > 0 || MissingPlaceholders.Count > 0;

    public bool CanApply => State == UpgradeState.Outdated && HasWork;
}

/// <summary>补结构的结果。</summary>
public sealed class UpgradeOutcome
{
    public IReadOnlyList<string> CreatedDirectories { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CreatedPlaceholders { get; init; } = Array.Empty<string>();

    /// <summary>是否已把 <c>.pm-scaffold.json</c> 的指纹更新为当前模板。</summary>
    public bool MetadataUpdated { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// 模板升级检测与"只加不删"补结构（WP3.5）。
/// </summary>
/// <remarks>
/// 判定依据是 <c>.pm-scaffold.json</c> 里的 <c>templateHash</c>（模板配置文件的 SHA256）：
/// 与当前模板一致即视为对齐；不一致才按**项目当时勾选的模块**重算应有结构，与磁盘实际存在的目录做差。
/// <para>
/// 三条硬约束：① 只创建缺失目录与占位文件；② 绝不删除、绝不覆盖任何既有文件；③ 补完把指纹写回，
/// 否则下次打开又会提示"模板已更新"。模块选择必须沿用元数据里的记录——用全模块重算会诱导用户
/// 补出一堆当初没要的目录（例如没勾安装包的桌面项目突然冒出 <c>output/installer</c>）。
/// </para>
/// </remarks>
public sealed class TemplateUpgradeService
{
    private readonly IFileSystem _fileSystem;
    private readonly TimeProvider _time;

    public TemplateUpgradeService(IFileSystem? fileSystem = null, TimeProvider? time = null)
    {
        _fileSystem = fileSystem ?? PhysicalFileSystem.Instance;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>只读比对。不做任何写操作，可安全地在选中项目时调用。</summary>
    public UpgradePlan Plan(string projectRoot, string templateName, Template? template)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(templateName))
        {
            return new UpgradePlan
            {
                State = UpgradeState.NotManaged,
                Message = "外部项目没有模板记录，无法与模板比对。",
            };
        }

        var metadata = ScaffoldMetadata.TryRead(_fileSystem, projectRoot);
        if (metadata is null)
        {
            return new UpgradePlan
            {
                State = UpgradeState.NotManaged,
                Message = "缺少 .pm-scaffold.json，无法确认该项目由哪套模板生成。",
            };
        }

        if (template is null)
        {
            return new UpgradePlan
            {
                State = UpgradeState.TemplateMissing,
                Message = $"模板「{templateName}」不在当前配置中（可能已删除或改名），无法比对。",
            };
        }

        if (metadata.HasHash && string.Equals(metadata.TemplateHash, template.ConfigHash, StringComparison.OrdinalIgnoreCase))
        {
            return new UpgradePlan
            {
                State = UpgradeState.UpToDate,
                Message = $"已与模板「{template.Title}」对齐。",
            };
        }

        var (missingDirectories, missingPlaceholders) = DiffStructure(projectRoot, template, metadata.Modules);

        return new UpgradePlan
        {
            State = UpgradeState.Outdated,
            MissingDirectories = missingDirectories,
            MissingPlaceholders = missingPlaceholders,
            Message = missingDirectories.Count == 0
                ? $"模板「{template.Title}」已更新，但目录结构无缺失（可能只改了文档或占位内容）。"
                : $"模板「{template.Title}」已更新，可补 {missingDirectories.Count} 个目录。",
        };
    }

    /// <summary>
    /// 按 <paramref name="plan"/> 补结构。幂等：目录已存在则跳过，占位文件已存在则不覆盖。
    /// </summary>
    public UpgradeOutcome Apply(string projectRoot, Template template, UpgradePlan plan)
    {
        var createdDirectories = new List<string>();
        var createdPlaceholders = new List<string>();

        foreach (var relative in plan.MissingDirectories)
        {
            var path = Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (_fileSystem.DirectoryExists(path))
            {
                continue;
            }

            _fileSystem.CreateDirectory(path);
            createdDirectories.Add(relative);
        }

        foreach (var relative in plan.MissingPlaceholders)
        {
            var path = Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (_fileSystem.FileExists(path))
            {
                continue;
            }

            // 占位文件所在目录可能刚被创建；再兜一次，避免父目录缺失导致写入失败
            var directory = Path.GetDirectoryName(path);
            if (directory is not null && !_fileSystem.DirectoryExists(directory))
            {
                _fileSystem.CreateDirectory(directory);
            }

            _fileSystem.WriteAllText(path, string.Empty);
            createdPlaceholders.Add(relative);
        }

        var metadataUpdated = ScaffoldMetadata.TryUpdateHash(
            _fileSystem,
            projectRoot,
            template.ConfigHash,
            template.SchemaVersion,
            _time.GetLocalNow());

        return new UpgradeOutcome
        {
            CreatedDirectories = createdDirectories,
            CreatedPlaceholders = createdPlaceholders,
            MetadataUpdated = metadataUpdated,
            Message = BuildOutcomeMessage(createdDirectories.Count, createdPlaceholders.Count, metadataUpdated, plan.HasWork),
        };
    }

    private (IReadOnlyList<string> Directories, IReadOnlyList<string> Placeholders) DiffStructure(
        string projectRoot,
        Template template,
        IReadOnlyList<string> modules)
    {
        var tree = TreeBuilder.Build(template, modules);
        var declared = TreeBuilder.FlattenRelative(tree);

        var missingDirectories = new List<string>();
        foreach (var relative in declared)
        {
            if (relative.Length == 0)
            {
                continue;
            }

            var path = Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!_fileSystem.DirectoryExists(path))
            {
                missingDirectories.Add(relative);
            }
        }

        // 占位文件只针对"空叶子目录"：与生成时同一套规则（含 gitignore 忽略判定），否则会与引擎行为分叉
        var ignored = TreeBuilder.IgnoredDirectoryPrefixes(template.Gitignore);
        var missingPlaceholders = new List<string>();
        foreach (var leaf in TreeBuilder.EmptyLeafDirectories(tree))
        {
            if (TreeBuilder.MatchesIgnoredDirectory(leaf, ignored))
            {
                continue;
            }

            var path = Path.Combine(
                projectRoot,
                leaf.Replace('/', Path.DirectorySeparatorChar),
                TreeBuilder.PlaceholderFileName);
            if (!_fileSystem.FileExists(path))
            {
                missingPlaceholders.Add($"{leaf}/{TreeBuilder.PlaceholderFileName}");
            }
        }

        return (missingDirectories, missingPlaceholders);
    }

    private static string BuildOutcomeMessage(int directories, int placeholders, bool metadataUpdated, bool hadWork)
    {
        if (!hadWork)
        {
            return "没有需要补的目录。";
        }

        var parts = new List<string>();
        if (directories > 0)
        {
            parts.Add($"{directories} 个目录");
        }

        if (placeholders > 0)
        {
            parts.Add($"{placeholders} 个占位文件");
        }

        var summary = parts.Count == 0 ? "没有需要补的内容" : $"已补 {string.Join("、", parts)}";
        return metadataUpdated ? $"{summary}，已更新模板指纹。" : $"{summary}；模板指纹未能写入。";
    }
}
