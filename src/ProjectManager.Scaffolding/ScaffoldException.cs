using ProjectManager.Scaffolding.Models;

namespace ProjectManager.Scaffolding;

/// <summary>
/// 脚手架引擎的结构化异常：携带错误码，UI 按码分支、日志按码聚合。
/// </summary>
public sealed class ScaffoldException : Exception
{
    public ScaffoldException(ScaffoldErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public ScaffoldException(ScaffoldErrorCode code, string message, Exception? innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public ScaffoldErrorCode Code { get; }

    /// <summary>错误码字符串（PM-SCF-0xx），对外契约。</summary>
    public string ErrorCode => Code.ToCode();

    /// <summary>错误定位信息：模板名 / 字段 / 首个冲突路径等。</summary>
    public string Details { get; init; } = string.Empty;

    /// <summary>回滚失败时需要用户手工清理的清单（G5 降级路径）。</summary>
    public IReadOnlyList<string> ManualCleanupList { get; init; } = Array.Empty<string>();
}
