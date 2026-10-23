namespace ProjectManager.Storage;

public enum ThemePreference
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>应用设置。全部有合理默认值，缺失字段按默认处理。</summary>
public sealed class AppSettings
{
    /// <summary>上次使用的生成位置（下次打开向导时预填）。</summary>
    public string DefaultTargetDirectory { get; set; } = string.Empty;

    /// <summary>上次使用的模板键。</summary>
    public string DefaultTemplateName { get; set; } = string.Empty;

    /// <summary>生成成功后自动打开项目目录。</summary>
    public bool OpenFolderAfterGeneration { get; set; } = true;

    public ThemePreference Theme { get; set; } = ThemePreference.System;

    /// <summary>侧栏是否折叠（只留图标导航）。</summary>
    public bool SidebarCollapsed { get; set; }

    /// <summary>最近一次看到"模板来源已变"提示时的配置哈希，用于免重复提示。</summary>
    public string LastSeenTemplateHash { get; set; } = string.Empty;
}

/// <summary>设置存储。损坏时回退默认值并带出警告，不回滚用户其他文件。</summary>
public interface ISettingsStore
{
    Task<(AppSettings Settings, string? Warning)> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
