namespace ProjectManager.Scaffolding.Models;

/// <summary>一次生成请求。<see cref="TargetDirectory"/> 是父目录，项目目录在其下按项目名创建。</summary>
public sealed class GenerationRequest
{
    public required string ProjectName { get; init; }

    public required string Description { get; init; }

    /// <summary>父目录（已存在的目录）。</summary>
    public required string TargetDirectory { get; init; }

    /// <summary>勾选的模块 key；空集合表示不使用任何模块。</summary>
    public IReadOnlyCollection<string> SelectedModules { get; init; } = Array.Empty<string>();

    /// <summary>仅预览（不写任何字节），用于 Step 3 的「仅预览」档位。</summary>
    public bool DryRun { get; init; }

    /// <summary>项目目录最终落点：<c>{TargetDirectory}/{ProjectName}</c>。</summary>
    public string ResolveProjectPath()
        => Path.Combine(TargetDirectory, ProjectName);
}
