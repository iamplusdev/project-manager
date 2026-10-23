using ProjectManager.Scaffolding.Models;
using ProjectManager.Storage;

namespace ProjectManager.Application.Wizard;

public enum GenerationOutcomeKind
{
    /// <summary>生成成功且已注册到项目清单。</summary>
    Success = 0,

    /// <summary>仅预览：未写任何字节。</summary>
    PreviewOnly = 1,

    /// <summary>生成失败，已完成部分已回滚。</summary>
    Failed = 2,

    /// <summary>生成失败且回滚不完整，需要用户按清单手工清理。</summary>
    RollbackFailed = 3,

    /// <summary>用户取消（阶段间隙生效，已落盘部分已回滚）。</summary>
    Cancelled = 4,
}

/// <summary>生成过程中对外广播的阶段，用于 UI 进度提示。</summary>
public enum GenerationStage
{
    Preparing = 0,
    Generating = 1,
    InitializingGit = 2,
    Registering = 3,
    Completed = 4,
}

/// <summary>
/// 一次生成的最终结果。区分「文件已生成但注册失败」——
/// 这种状态下不能对用户说"失败"，也不能说"完成"，否则用户会重复生成。
/// </summary>
public sealed class GenerationOutcome
{
    public required GenerationOutcomeKind Kind { get; init; }

    public GenerationResult? Result { get; init; }

    /// <summary>注册成功后的项目记录；注册失败时为 <c>null</c>。</summary>
    public StoredProject? Project { get; init; }

    public string? ErrorCode { get; init; }

    public string? Message { get; init; }

    public string? Details { get; init; }

    /// <summary>需要用户手工清理的清单（回滚失败降级路径）。</summary>
    public IReadOnlyList<string> ManualCleanupList { get; init; } = Array.Empty<string>();

    /// <summary>文件已生成，但写入项目清单失败——结果页应提供「重试注册」。</summary>
    public bool RegistrationFailed { get; init; }

    public string? RegistrationError { get; init; }

    /// <summary>勾选了 <c>git init</c> 但未成功（git 缺失/失败）。不影响生成结果。</summary>
    public bool GitFailed { get; init; }

    public string? GitMessage { get; init; }

    public bool GitSucceeded { get; init; }

    public string? ProjectPath => Result?.ProjectPath;

    /// <summary>可直接展示的一句话结论。</summary>
    public string Headline => Kind switch
    {
        GenerationOutcomeKind.Success when RegistrationFailed => "文件已生成，但未能加入项目清单",
        GenerationOutcomeKind.Success when GitFailed => "生成成功（Git 仓库未初始化）",
        GenerationOutcomeKind.Success => "生成成功",
        GenerationOutcomeKind.PreviewOnly => "预览完成（未写入任何文件）",
        GenerationOutcomeKind.Cancelled => "已取消生成",
        GenerationOutcomeKind.RollbackFailed => "生成失败，且未能完全回滚",
        _ => "生成失败",
    };
}
