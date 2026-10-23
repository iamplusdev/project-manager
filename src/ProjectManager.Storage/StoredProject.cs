namespace ProjectManager.Storage;

/// <summary>
/// 受管项目。只保存"项目是什么"，**不保存 Git 实时状态**
/// （分支、脏文件、提交历史一律实时向仓库查询，见软件设计方案第 4 节）。
/// </summary>
public sealed class StoredProject
{
    /// <summary>稳定标识；同一路径的项目始终复用同一 Id。</summary>
    public required string Id { get; init; }

    /// <summary>项目名（生成时用户输入，作为展示名）。</summary>
    public required string Name { get; init; }

    /// <summary>项目根目录绝对路径（唯一键，比较时大小写不敏感）。</summary>
    public required string Path { get; init; }

    /// <summary>生成所用模板键；手工"打开已有项目"时为空。</summary>
    public string TemplateName { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>最近一次在 PM 中打开的时间；最近列表按此倒序。</summary>
    public DateTimeOffset LastOpenedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>生成时是否勾选并成功执行了 <c>git init</c>。</summary>
    public bool GitInitialized { get; init; }

    /// <summary>是否由脚手架生成（false 表示"打开已有项目"接入）。</summary>
    public bool Generated { get; init; } = true;

    public StoredProject WithLastOpened(DateTimeOffset at) => new()
    {
        Id = Id,
        Name = Name,
        Path = Path,
        TemplateName = TemplateName,
        CreatedAt = CreatedAt,
        LastOpenedAt = at,
        GitInitialized = GitInitialized,
        Generated = Generated,
    };
}
