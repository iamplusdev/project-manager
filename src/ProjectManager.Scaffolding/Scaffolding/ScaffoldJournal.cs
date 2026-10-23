using ProjectManager.Scaffolding.IO;

namespace ProjectManager.Scaffolding.Scaffolding;

/// <summary>
/// 回滚日志（WP1.6）。每次成功动作<b>先记后做</b>的反面——这里选择"做成即记"，
/// 因为记录本身不做 IO；关键在于：记录顺序与执行顺序一致，回滚时逆序撤销。
/// </summary>
/// <remarks>
/// G8 的落点：<see cref="Rollback"/> <b>不接受 CancellationToken</b>。
/// 一旦进入回滚，取消请求只能被忽略——否则会留下"半生成"目录，
/// 用户将无法分辨哪些是自己原有的文件。
/// </remarks>
internal sealed class ScaffoldJournal
{
    private readonly List<Entry> _entries = new();
    private readonly IFileSystem _fs;

    public ScaffoldJournal(IFileSystem fs) => _fs = fs;

    public void RecordDirectory(string path) => _entries.Add(new Entry(EntryKind.Directory, path, null));

    /// <summary>记录一次文件写入；<paramref name="backupPath"/> 非空表示该位置原有文件已被备份。</summary>
    public void RecordFile(string path, string? backupPath) => _entries.Add(new Entry(EntryKind.File, path, backupPath));

    /// <summary>被保留的既有文件（例如用户自己的 <c>.gitignore</c>）：只登记，回滚无需动作。</summary>
    public void RecordPreserved(string path) => _entries.Add(new Entry(EntryKind.Preserved, path, null));

    public RollbackReport Rollback()
    {
        var removedDirectories = 0;
        var removedFiles = 0;
        var restored = 0;
        var failures = new List<string>();

        for (var index = _entries.Count - 1; index >= 0; index--)
        {
            var entry = _entries[index];
            try
            {
                switch (entry.Kind)
                {
                    case EntryKind.File:
                        if (_fs.FileExists(entry.Path))
                        {
                            _fs.DeleteFile(entry.Path);
                            removedFiles++;
                        }

                        // 备份存在则还原：回滚后用户看到的内容必须与生成前完全一致
                        if (entry.BackupPath is { } backup && _fs.FileExists(backup))
                        {
                            _fs.MoveFile(backup, entry.Path);
                            restored++;
                        }

                        break;

                    case EntryKind.Directory:
                        if (_fs.DirectoryExists(entry.Path) && _fs.IsDirectoryEmpty(entry.Path))
                        {
                            _fs.DeleteDirectory(entry.Path);
                            removedDirectories++;
                        }

                        break;

                    default:
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(entry.Path);
            }
        }

        return new RollbackReport(removedDirectories, removedFiles, restored, failures);
    }

    private enum EntryKind
    {
        Directory,
        File,
        Preserved,
    }

    private readonly record struct Entry(EntryKind Kind, string Path, string? BackupPath);
}

/// <summary>回滚结果。失败项需要交给 UI 生成"本次新增清单"，避免用户面对陌生残留。</summary>
public sealed record RollbackReport(
    int RemovedDirectories,
    int RemovedFiles,
    int RestoredFiles,
    IReadOnlyList<string> Failures)
{
    public bool Succeeded => Failures.Count == 0;
}
