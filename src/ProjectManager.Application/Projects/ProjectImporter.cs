using System.Text.Json;
using ProjectManager.Scaffolding.IO;
using ProjectManager.Storage;

namespace ProjectManager.Application.Projects;

/// <summary>把已有目录接入项目清单的结果。</summary>
public enum ImportStatus
{
    /// <summary>新加入清单。</summary>
    Added,

    /// <summary>该路径已在清单中，已把它提到最近（不是错误）。</summary>
    AlreadyManaged,

    /// <summary>路径为空、不存在或不是目录。</summary>
    MissingDirectory,

    /// <summary>非法路径或清单写入失败。</summary>
    Failed,
}

public sealed class ImportResult
{
    public required ImportStatus Status { get; init; }

    /// <summary>成功时为准入后的记录（<see cref="ImportStatus.Added"/> / <see cref="ImportStatus.AlreadyManaged"/>）。</summary>
    public StoredProject? Project { get; init; }

    /// <summary>可直接展示给用户的说明。</summary>
    public string Message { get; init; } = string.Empty;

    public bool Succeeded => Status is ImportStatus.Added or ImportStatus.AlreadyManaged;
}

/// <summary>
/// 「打开已有项目」：把一个已存在的目录接入项目清单（M2 收尾补齐的能力）。
/// </summary>
/// <remarks>
/// 与生成路径的区别只在<b>信息从哪来</b>：生成时名称与模板由用户输入，
/// 这里从目录本身推断——有 <c>.pm-scaffold.json</c> 就采信其中的项目名与模板
/// （PM 生成过的目录可以"原样接回"），没有就以目录名为准并标记为非生成项目。
/// 不复制、不移动、不写目标目录内任何文件，只写清单。
/// </remarks>
public sealed class ProjectImporter
{
    private readonly IProjectRepository _repository;
    private readonly IFileSystem _fileSystem;
    private readonly TimeProvider _time;

    public ProjectImporter(IProjectRepository repository, IFileSystem? fileSystem = null, TimeProvider? time = null)
    {
        _repository = repository;
        _fileSystem = fileSystem ?? PhysicalFileSystem.Instance;
        _time = time ?? TimeProvider.System;
    }

    public async Task<ImportResult> ImportAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return Fail(ImportStatus.MissingDirectory, "未选择目录。");
        }

        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Fail(ImportStatus.Failed, $"路径无效：{ex.Message}");
        }

        if (!_fileSystem.DirectoryExists(fullPath))
        {
            return Fail(ImportStatus.MissingDirectory, $"目录不存在：{fullPath}");
        }

        var now = _time.GetLocalNow();

        try
        {
            var loaded = await _repository.LoadAsync(cancellationToken).ConfigureAwait(true);

            // 同一路径（大小写不敏感）已在清单里：不重复登记，只把它提到最近
            var existing = loaded.Projects.FirstOrDefault(
                project => string.Equals(
                    Path.TrimEndingDirectorySeparator(project.Path),
                    fullPath,
                    StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                var touched = await _repository.TouchAsync(existing.Id, now, cancellationToken).ConfigureAwait(true);
                return new ImportResult
                {
                    Status = ImportStatus.AlreadyManaged,
                    Project = touched ?? existing,
                    Message = $"「{existing.Name}」已在项目清单中，已提到最近。",
                };
            }

            var metadata = ReadMetadata(fullPath);
            var name = metadata?.ProjectName is { Length: > 0 } metadataName
                ? metadataName
                : DeriveNameFromPath(fullPath);

            var stored = await _repository.UpsertAsync(
                new StoredProject
                {
                    Id = Guid.NewGuid().ToString("n"),
                    Name = name,
                    Path = fullPath,
                    TemplateName = metadata?.Template ?? string.Empty,
                    CreatedAt = now,
                    LastOpenedAt = now,
                    GitInitialized = _fileSystem.DirectoryExists(Path.Combine(fullPath, ".git")),
                    Generated = metadata is not null,
                },
                cancellationToken).ConfigureAwait(true);

            var origin = metadata is null ? "已按目录名识别" : $"已从 .pm-scaffold.json 识别（模板：{metadata.Template}）";
            return new ImportResult
            {
                Status = ImportStatus.Added,
                Project = stored,
                Message = $"「{name}」已加入项目清单，{origin}。",
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(ImportStatus.Failed, $"写入项目清单失败：{ex.Message}");
        }
    }

    /// <summary>目录名即项目名；盘符根目录退化为整条路径。</summary>
    private static string DeriveNameFromPath(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        return string.IsNullOrEmpty(name) ? fullPath : name;
    }

    /// <summary>
    /// 读取 <c>.pm-scaffold.json</c>；不存在、损坏或字段缺失一律视为"没有元数据"。
    /// 这里是<b>消费者</b>，不对文件做任何校验或修复——追责在生成侧。
    /// </summary>
    private ScaffoldIdentity? ReadMetadata(string directory)
    {
        var path = Path.Combine(directory, MetadataFileName);

        try
        {
            if (!_fileSystem.FileExists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(_fileSystem.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var projectName = ReadString(root, "projectName");
            var template = ReadString(root, "template") ?? string.Empty;

            return projectName is null ? null : new ScaffoldIdentity(projectName, template);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static ImportResult Fail(ImportStatus status, string message) => new()
    {
        Status = status,
        Message = message,
    };

    private sealed record ScaffoldIdentity(string ProjectName, string Template);

    private const string MetadataFileName = ".pm-scaffold.json";
}
