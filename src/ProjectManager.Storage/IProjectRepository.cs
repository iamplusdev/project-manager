namespace ProjectManager.Storage;

/// <summary>项目清单读取结果。<see cref="Warning"/> 非空表示"读到了但发生过降级"，必须展示给用户。</summary>
public sealed class ProjectLoadResult
{
    public required IReadOnlyList<StoredProject> Projects { get; init; }

    /// <summary>降级说明（例如清单文件损坏已隔离）。为 <c>null</c> 表示一切正常。</summary>
    public string? Warning { get; init; }
}

/// <summary>
/// 受管项目清单仓储。接口化是为了让"换 SQLite"只改实现（M3 视数据量决定），
/// 并让 Application 层可在无文件系统副作用下测试。
/// </summary>
public interface IProjectRepository
{
    Task<ProjectLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 注册项目。同一路径（大小写不敏感）已存在时**更新**该条并保留其 <see cref="StoredProject.Id"/>，
    /// 返回最终落盘的对象（调用方据此拿到复用的 Id）。
    /// </summary>
    Task<StoredProject> UpsertAsync(StoredProject project, CancellationToken cancellationToken = default);

    /// <summary>标记为最近打开。返回更新后的对象；项目不存在时返回 <c>null</c>。</summary>
    Task<StoredProject?> TouchAsync(string id, DateTimeOffset at, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>最近项目（按 <see cref="StoredProject.LastOpenedAt"/> 倒序）。</summary>
    Task<IReadOnlyList<StoredProject>> ListRecentAsync(int count, CancellationToken cancellationToken = default);
}
