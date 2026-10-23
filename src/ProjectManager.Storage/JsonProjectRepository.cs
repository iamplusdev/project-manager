using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProjectManager.Storage;

/// <summary>
/// 以 JSON 文件承载的项目清单。
/// </summary>
/// <remarks>
/// <para>
/// 选 JSON 而非 SQLite：M2 阶段项目数量是"几十条"量级，SQLite 需要额外 NuGet 依赖，
/// 会破坏"任何机器 <c>dotnet build</c> 即可离线构建"这一工程约定。
/// 仓储是接口化的，数据量真到需要索引时只换实现，不动上层。
/// </para>
/// <para>
/// 两条硬约束：<b>原子写</b>（先写临时文件再替换，避免断电留下半截 JSON）与
/// <b>损坏隔离</b>（解析失败时把坏文件改名留存，而不是直接覆盖——用户可能还想捞回内容）。
/// </para>
/// </remarks>
public sealed class JsonProjectRepository : IProjectRepository
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonProjectRepository(string path, TimeProvider? time = null)
    {
        _path = path;
        _time = time ?? TimeProvider.System;
    }

    public JsonProjectRepository(AppPaths paths, TimeProvider? time = null)
        : this(paths.ProjectsFile, time)
    {
    }

    public string FilePath => _path;

    public async Task<ProjectLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoredProject> UpsertAsync(StoredProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var list = current.Projects.ToList();
            var key = NormalizePath(project.Path);

            var index = list.FindIndex(item => NormalizePath(item.Path).Equals(key, StringComparison.OrdinalIgnoreCase));
            StoredProject stored;
            if (index >= 0)
            {
                // 同一路径再次注册：保留原 Id 与首次生成时间，避免最近列表出现两条同项目
                var existing = list[index];
                stored = Merge(existing, project);
                list[index] = stored;
            }
            else
            {
                stored = project;
                list.Add(stored);
            }

            await WriteUnlockedAsync(new ProjectFile
            {
                SchemaVersion = CurrentSchemaVersion,
                Projects = list.Select(ProjectRecord.From).ToList(),
            }, cancellationToken).ConfigureAwait(false);

            return stored;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoredProject?> TouchAsync(string id, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var list = current.Projects.ToList();
            var index = list.FindIndex(item => item.Id == id);
            if (index < 0)
            {
                return null;
            }

            var updated = list[index].WithLastOpened(at);
            list[index] = updated;
            await WriteUnlockedAsync(new ProjectFile
            {
                SchemaVersion = CurrentSchemaVersion,
                Projects = list.Select(ProjectRecord.From).ToList(),
            }, cancellationToken).ConfigureAwait(false);

            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var list = current.Projects.ToList();
            if (list.RemoveAll(item => item.Id == id) == 0)
            {
                return false;
            }

            await WriteUnlockedAsync(new ProjectFile
            {
                SchemaVersion = CurrentSchemaVersion,
                Projects = list.Select(ProjectRecord.From).ToList(),
            }, cancellationToken).ConfigureAwait(false);

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<StoredProject>> ListRecentAsync(int count, CancellationToken cancellationToken = default)
    {
        var current = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return current.Projects
            .OrderByDescending(item => item.LastOpenedAt)
            .Take(Math.Max(0, count))
            .ToArray();
    }

    private async Task<ProjectLoadResult> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new ProjectLoadResult { Projects = Array.Empty<StoredProject>() };
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(_path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ProjectLoadResult
            {
                Projects = Array.Empty<StoredProject>(),
                Warning = $"项目清单读取失败：{ex.Message}（本次以空清单继续，写入时会覆盖该文件）",
            };
        }

        ProjectFile? file = null;
        try
        {
            file = JsonSerializer.Deserialize<ProjectFile>(text, JsonOptions);
        }
        catch (JsonException ex)
        {
            var quarantined = Quarantine();
            return new ProjectLoadResult
            {
                Projects = Array.Empty<StoredProject>(),
                Warning = $"项目清单已损坏（{ex.Message}），原文件已保留为 {Path.GetFileName(quarantined)}，本次以空清单继续。",
            };
        }

        if (file?.Projects is null)
        {
            return new ProjectLoadResult
            {
                Projects = Array.Empty<StoredProject>(),
                Warning = string.IsNullOrWhiteSpace(text) ? null : "项目清单内容为空或格式不符，本次以空清单继续。",
            };
        }

        var projects = new List<StoredProject>(file.Projects.Count);
        var dropped = 0;
        foreach (var record in file.Projects)
        {
            if (string.IsNullOrWhiteSpace(record.Path) || string.IsNullOrWhiteSpace(record.Name))
            {
                dropped++;
                continue;
            }

            projects.Add(record.ToStored());
        }

        return new ProjectLoadResult
        {
            Projects = projects,
            Warning = dropped > 0 ? $"项目清单中有 {dropped} 条记录缺少路径或名称，已跳过。" : null,
        };
    }

    private async Task WriteUnlockedAsync(ProjectFile file, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(file, JsonOptions);
        var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);

        // 同卷替换：File.Move 的 overwrite 在 Windows 上是原子替换语义，不会留下半截文件
        File.Move(temp, _path, overwrite: true);
    }

    /// <summary>把损坏文件改名留存，返回新文件名。同秒重复损坏时追加序号，绝不覆盖既有隔离文件。</summary>
    private string Quarantine()
    {
        var stamp = _time.GetLocalNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var target = $"{_path}.corrupt-{stamp}";
        var index = 1;
        while (File.Exists(target))
        {
            target = $"{_path}.corrupt-{stamp}-{index++}";
        }

        try
        {
            File.Move(_path, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return _path;
        }
    }

    private static StoredProject Merge(StoredProject existing, StoredProject incoming) => new()
    {
        Id = existing.Id,
        // 名称与模板以最新一次生成为准：用户可能修了拼写后重新生成
        Name = incoming.Name,
        Path = incoming.Path,
        TemplateName = incoming.TemplateName.Length > 0 ? incoming.TemplateName : existing.TemplateName,
        CreatedAt = existing.CreatedAt,
        LastOpenedAt = incoming.LastOpenedAt > existing.LastOpenedAt ? incoming.LastOpenedAt : existing.LastOpenedAt,
        GitInitialized = incoming.GitInitialized || existing.GitInitialized,
        Generated = existing.Generated && incoming.Generated,
    };

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
    }

    private sealed class ProjectFile
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public List<ProjectRecord> Projects { get; set; } = new();
    }

    /// <summary>落盘记录。字段全部可空，便于对旧文件/手工编辑容错。</summary>
    private sealed class ProjectRecord
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Path { get; set; }

        public string? TemplateName { get; set; }

        public DateTimeOffset? CreatedAt { get; set; }

        public DateTimeOffset? LastOpenedAt { get; set; }

        public bool GitInitialized { get; set; }

        public bool Generated { get; set; } = true;

        public static ProjectRecord From(StoredProject project) => new()
        {
            Id = project.Id,
            Name = project.Name,
            Path = project.Path,
            TemplateName = project.TemplateName,
            CreatedAt = project.CreatedAt,
            LastOpenedAt = project.LastOpenedAt,
            GitInitialized = project.GitInitialized,
            Generated = project.Generated,
        };

        public StoredProject ToStored() => new()
        {
            Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("n") : Id,
            Name = Name!,
            Path = Path!,
            TemplateName = TemplateName ?? string.Empty,
            CreatedAt = CreatedAt ?? DateTimeOffset.Now,
            LastOpenedAt = LastOpenedAt ?? CreatedAt ?? DateTimeOffset.Now,
            GitInitialized = GitInitialized,
            Generated = Generated,
        };
    }
}
