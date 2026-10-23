using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ProjectManager.Storage;

/// <summary>JSON 设置存储。设置丢失代价低，因此损坏时直接用默认值，只提示不隔离。</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSettingsStore(string path) => _path = path;

    public JsonSettingsStore(AppPaths paths) : this(paths.SettingsFile)
    {
    }

    public string FilePath => _path;

    public async Task<(AppSettings Settings, string? Warning)> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return (new AppSettings(), null);
        }

        try
        {
            var text = await File.ReadAllTextAsync(_path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var settings = JsonSerializer.Deserialize<AppSettings>(text, JsonOptions);
            return settings is null
                ? (new AppSettings(), "设置内容为空，已使用默认设置。")
                : (settings, null);
        }
        catch (JsonException ex)
        {
            return (new AppSettings(), $"设置文件格式损坏（{ex.Message}），已使用默认设置。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (new AppSettings(), $"设置读取失败：{ex.Message}，已使用默认设置。");
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = _path + ".tmp";
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            await File.WriteAllTextAsync(temp, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
