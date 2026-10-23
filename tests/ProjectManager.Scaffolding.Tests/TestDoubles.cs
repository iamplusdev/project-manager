using ProjectManager.Scaffolding.IO;

namespace ProjectManager.Scaffolding.Tests;

/// <summary>可注入的时钟：备份时间戳与元数据时间必须可控，否则测试不可重复。</summary>
public sealed class FixedClock : IClock
{
    public FixedClock(DateTimeOffset localNow) => LocalNow = localNow;

    public DateTimeOffset LocalNow { get; set; }

    public DateTimeOffset UtcNow => LocalNow.ToUniversalTime();
}

/// <summary>
/// 故障注入文件系统：用于覆盖"磁盘满 / 权限不足 / 回滚失败 / 生成中途取消"等真实 IO 异常路径。
/// 开发计划 3.3 风险对策点名要求——否则回滚逻辑只能靠人读代码相信它。
/// </summary>
public sealed class FaultInjectingFileSystem : IFileSystem
{
    private readonly IFileSystem _inner;

    public FaultInjectingFileSystem(IFileSystem? inner = null) => _inner = inner ?? PhysicalFileSystem.Instance;

    /// <summary>允许成功写入的次数；超过即抛 <see cref="IOException"/>。null 表示不限。</summary>
    public int? WritesBeforeFailure { get; set; }

    /// <summary>允许成功建目录的次数；超过即抛 <see cref="UnauthorizedAccessException"/>。</summary>
    public int? DirectoriesBeforeFailure { get; set; }

    /// <summary>为 true 时删除操作一律失败——用于验证 PM-SCF-021（回滚失败降级）。</summary>
    public bool FailDeletes { get; set; }

    /// <summary>可写性探测结果；false 用于模拟只读介质 / Program Files。</summary>
    public bool Writable { get; set; } = true;

    /// <summary>每次成功建目录后回调（用于在阶段中途取消令牌）。</summary>
    public Action<string>? OnDirectoryCreated { get; set; }

    public int WriteCount { get; private set; }

    public int DirectoryCount { get; private set; }

    public bool FileExists(string path) => _inner.FileExists(path);

    public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

    public bool IsSymbolicLink(string path) => _inner.IsSymbolicLink(path);

    public void CreateDirectory(string path)
    {
        if (DirectoriesBeforeFailure is { } limit && DirectoryCount >= limit)
        {
            throw new UnauthorizedAccessException($"注入的建目录失败：{path}");
        }

        _inner.CreateDirectory(path);
        DirectoryCount++;
        OnDirectoryCreated?.Invoke(path);
    }

    public void WriteAllText(string path, string content)
    {
        if (WritesBeforeFailure is { } limit && WriteCount >= limit)
        {
            throw new IOException($"注入的写入失败：{path}");
        }

        _inner.WriteAllText(path, content);
        WriteCount++;
    }

    public string ReadAllText(string path) => _inner.ReadAllText(path);

    public void DeleteFile(string path)
    {
        if (FailDeletes)
        {
            throw new IOException($"注入的删除失败：{path}");
        }

        _inner.DeleteFile(path);
    }

    public void MoveFile(string sourcePath, string destinationPath)
    {
        if (FailDeletes)
        {
            throw new IOException($"注入的移动失败：{sourcePath}");
        }

        _inner.MoveFile(sourcePath, destinationPath);
    }

    public void DeleteDirectory(string path)
    {
        if (FailDeletes)
        {
            throw new IOException($"注入的删目录失败：{path}");
        }

        _inner.DeleteDirectory(path);
    }

    public bool IsDirectoryEmpty(string path) => _inner.IsDirectoryEmpty(path);

    public bool IsWritable(string directory) => Writable && _inner.IsWritable(directory);
}
