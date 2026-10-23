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

    /// <summary>
    /// 出错的模板名（配置级错误如 JSON 语法错、版本过高时为空）。
    /// 与 <see cref="Field"/> 一起构成"模板名 + 字段 + 错误码"的定位三件套——
    /// 用户拿到 <c>[PM-SCF-010] 模板「单体桌面工具」字段 modules：…</c> 才知道该改哪一行，
    /// 只给一句"模板非法"等于让人从头读整个 JSON。
    /// </summary>
    public string? TemplateName { get; init; }

    /// <summary>出错的字段路径（<c>structure</c>、<c>modules.key.paths</c>、<c>naming.dirCase</c> 等）。</summary>
    public string? Field { get; init; }

    /// <summary>回滚失败时需要用户手工清理的清单（G5 降级路径）。</summary>
    public IReadOnlyList<string> ManualCleanupList { get; init; } = Array.Empty<string>();
}
