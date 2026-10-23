namespace ProjectManager.Storage;

/// <summary>
/// 应用数据路径。集中定义，避免各处 <c>Path.Combine</c> 拼出不同位置。
/// </summary>
/// <remarks>
/// 放在 <c>%APPDATA%\ProjectManager</c>（可写、跨版本存活），
/// 而不是安装目录——安装目录在 <c>Program Files</c> 下通常不可写，
/// 且会被 Velopack 更新整体替换。
/// </remarks>
public sealed class AppPaths
{
    public const string AppFolderName = "ProjectManager";

    public AppPaths(string? userDataDirectory = null, string? installDirectory = null)
    {
        UserDataDirectory = userDataDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);
        InstallDirectory = installDirectory ?? AppContext.BaseDirectory;
    }

    /// <summary>可写的用户数据目录（<c>%APPDATA%\ProjectManager</c>）。</summary>
    public string UserDataDirectory { get; }

    /// <summary>安装目录（只读，随包分发）。</summary>
    public string InstallDirectory { get; }

    /// <summary>模板配置：用户可写副本（最高优先级来源）。</summary>
    public string TemplatesFile => Path.Combine(UserDataDirectory, "templates.json");

    /// <summary>安装目录随包分发的模板配置（只读来源）。</summary>
    public string InstallTemplatesFile => Path.Combine(InstallDirectory, "config", "templates.json");

    /// <summary>受管项目清单。</summary>
    public string ProjectsFile => Path.Combine(UserDataDirectory, "projects.json");

    /// <summary>应用设置。</summary>
    public string SettingsFile => Path.Combine(UserDataDirectory, "settings.json");

    /// <summary>生成审计日志。</summary>
    public string AuditLogFile => Path.Combine(UserDataDirectory, "generation.log");

    /// <summary>确保用户数据目录存在；失败时抛出（由调用方决定是否降级为只读模式）。</summary>
    public void EnsureUserDataDirectory() => Directory.CreateDirectory(UserDataDirectory);
}
