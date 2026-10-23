namespace ProjectManager.Scaffolding.Models;

/// <summary>
/// 脚手架错误码。码与文案分离：UI 按码分支（可否重试 / 需否提权 / 需否换路径），日志按码聚合。
/// 与方案 4.5 一一对应。
/// </summary>
public enum ScaffoldErrorCode
{
    /// <summary>生成路径不存在或不是文件夹。</summary>
    PathNotFound = 1,

    /// <summary>生成路径不可写（权限 / 只读介质 / Program Files）。</summary>
    PathNotWritable = 2,

    /// <summary>计划目录位置被同名文件占用。</summary>
    PathOccupiedByFile = 3,

    /// <summary>目标项目目录已存在（由 UI 决定合并 / 仅预览 / 取消）。</summary>
    TargetExists = 4,

    /// <summary>模板非法：语法 / 字段缺失 / 非法目录段 / schemaVersion 过高。</summary>
    TemplateInvalid = 10,

    /// <summary>模板超过护栏上限。</summary>
    TemplateTooLarge = 11,

    /// <summary>生成中断（IO 异常等），已完成部分已回滚。</summary>
    GenerationFailed = 20,

    /// <summary>回滚失败：需要输出「本次新增清单」供手工清理。</summary>
    RollbackFailed = 21,

    /// <summary>用户取消（仅阶段间隙生效）。</summary>
    Cancelled = 30,

    /// <summary>路径过长（≥260）或含不支持的形态（符号链接 / .git）。</summary>
    PathTooLong = 40,

    /// <summary>目标为符号链接/junction，或试图在 <c>.git</c> 目录内生成——拒绝以免越界写入。</summary>
    SymlinkRejected = 41,

    /// <summary>
    /// 请求本身非法（项目名为空等）。属<b>引擎防御性校验</b>：
    /// 正常路径下 Step 1 已拦下，UI 不应看到此码；出现即说明调用方绕过了表单校验。
    /// </summary>
    InvalidRequest = 50,
}

/// <summary>
/// 错误码的字符串形式与用户可读前缀。数值在契约中不稳定，<b>字符串才是对外契约</b>。
/// </summary>
public static class ScaffoldErrorCodes
{
    public static string ToCode(this ScaffoldErrorCode code) => code switch
    {
        ScaffoldErrorCode.PathNotFound => "PM-SCF-001",
        ScaffoldErrorCode.PathNotWritable => "PM-SCF-002",
        ScaffoldErrorCode.PathOccupiedByFile => "PM-SCF-003",
        ScaffoldErrorCode.TargetExists => "PM-SCF-004",
        ScaffoldErrorCode.TemplateInvalid => "PM-SCF-010",
        ScaffoldErrorCode.TemplateTooLarge => "PM-SCF-011",
        ScaffoldErrorCode.GenerationFailed => "PM-SCF-020",
        ScaffoldErrorCode.RollbackFailed => "PM-SCF-021",
        ScaffoldErrorCode.Cancelled => "PM-SCF-030",
        ScaffoldErrorCode.PathTooLong => "PM-SCF-040",
        ScaffoldErrorCode.SymlinkRejected => "PM-SCF-041",
        ScaffoldErrorCode.InvalidRequest => "PM-SCF-050",
        _ => "PM-SCF-999",
    };
}
