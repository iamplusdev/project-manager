namespace ProjectManager.Scaffolding.IO;

/// <summary>
/// 文件系统抽象。存在两个目的：
/// 1. 让「回滚 / 预检 / 跳过已有文件」等异常路径可在无真实磁盘的情况下被测试（M1 风险对策）；
/// 2. 把编码与路径形态统一收口，避免各处 <c>File.WriteAllText</c> 默认值不一致。
/// </summary>
public interface IFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>枚举直接子目录（不递归）。目录不可读时返回空集合，而不是抛异常——扫描类功能不应中断 UI。</summary>
    IReadOnlyList<string> EnumerateDirectories(string path);

    /// <summary>枚举直接子文件（不递归），同样在不可读时返回空集合。</summary>
    IReadOnlyList<string> EnumerateFiles(string path);

    bool IsSymbolicLink(string path);

    void CreateDirectory(string path);

    /// <summary>以 UTF-8（无 BOM）写入文本。</summary>
    void WriteAllText(string path, string content);

    string ReadAllText(string path);

    void DeleteFile(string path);

    /// <summary>移动（改名）文件。回滚时把 <c>.bak</c> 还原回原文件名依赖它。</summary>
    void MoveFile(string sourcePath, string destinationPath);

    void DeleteDirectory(string path);

    bool IsDirectoryEmpty(string path);

    /// <summary>探测目录是否可写（创建并删除一个探针文件），不留下痕迹。</summary>
    bool IsWritable(string directory);
}

/// <summary>真实磁盘实现。</summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static PhysicalFileSystem Instance { get; } = new();

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> EnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return Array.Empty<string>();
        }
    }

    public IReadOnlyList<string> EnumerateFiles(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return Array.Empty<string>();
        }
    }

    public bool IsSymbolicLink(string path)
    {
        var info = new FileInfo(path);
        if (info.Exists)
        {
            return info.LinkTarget is not null;
        }

        var dirInfo = new DirectoryInfo(path);
        return dirInfo.Exists && dirInfo.LinkTarget is not null;
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void WriteAllText(string path, string content) => File.WriteAllText(path, content, Utf8NoBom);

    public string ReadAllText(string path) => File.ReadAllText(path, Utf8NoBom);

    public void DeleteFile(string path) => File.Delete(path);

    public void MoveFile(string sourcePath, string destinationPath) => File.Move(sourcePath, destinationPath, overwrite: true);

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: false);

    public bool IsDirectoryEmpty(string path) => !Directory.EnumerateFileSystemEntries(path).Any();

    public bool IsWritable(string directory)
    {
        var probe = Path.Combine(directory, $".pm-write-probe-{Guid.NewGuid():N}");
        try
        {
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>时间抽象：备份时间戳与元数据时间可注入，保证测试确定性。</summary>
public interface IClock
{
    DateTimeOffset LocalNow { get; }

    DateTimeOffset UtcNow { get; }
}

/// <summary>系统时钟。</summary>
public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    public DateTimeOffset LocalNow => DateTimeOffset.Now;

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
