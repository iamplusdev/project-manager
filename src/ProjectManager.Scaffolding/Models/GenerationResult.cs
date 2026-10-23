namespace ProjectManager.Scaffolding.Models;

/// <summary>
/// 生成结果。所有路径均为<b>相对于项目根</b>的路径（统一 <c>/</c>），
/// 避免把用户绝对路径写入日志或 UI 文案（数据最小化）。
/// </summary>
public sealed class GenerationResult
{
    public required string ProjectPath { get; init; }

    public List<string> DirectoriesCreated { get; } = new();

    public List<string> FilesWritten { get; } = new();

    public List<string> Backups { get; } = new();

    /// <summary>被保留（未覆盖）的既有文件，例如用户自己的 <c>.gitignore</c>。</summary>
    public List<string> Preserved { get; } = new();

    public List<string> Placeholders { get; } = new();

    /// <summary>由 <c>files</c> 注入的文件。</summary>
    public List<string> InjectedFiles { get; } = new();

    /// <summary>因目标已存在而跳过的 <c>files</c> 条目（G9）。</summary>
    public List<string> SkippedExisting { get; } = new();

    /// <summary>目标项目目录在生成前是否已存在（UI 据此提示「合并」语义）。</summary>
    public bool TargetExisted { get; init; }

    public bool DryRun { get; init; }

    public int RolledBackDirectories { get; init; }
}

/// <summary>
/// 生成计划（预览用）。与执行阶段共用同一套计算，保证「预览所见 = 生成所得」。
/// </summary>
public sealed class ScaffoldPlan
{
    public required string ProjectPath { get; init; }

    /// <summary>将创建的目录（缺省已存在的会被排除）。</summary>
    public List<string> DirectoriesToCreate { get; } = new();

    public List<string> TextFilesToWrite { get; } = new();

    public List<string> FilesToInject { get; } = new();

    public List<string> PlaceholdersToCreate { get; } = new();

    /// <summary>将被备份的既有同名文件（转为 <c>.bak</c>）。</summary>
    public List<string> FilesToBackup { get; } = new();

    /// <summary>将被保留的既有文件（不覆盖）。</summary>
    public List<string> FilesToPreserve { get; } = new();

    public bool TargetExisted { get; init; }
}
