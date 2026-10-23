namespace ProjectManager.Application.Tests;

/// <summary>临时工作区 + 仓库内固定文件定位。用完即删，不污染用户目录。</summary>
public sealed class TempWorkspace : IDisposable
{
    public TempWorkspace(string label)
    {
        Root = Path.Combine(Path.GetTempPath(), "pm-m2-tests", $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Path2(params string[] segments) => Path.Combine(new[] { Root }.Concat(segments).ToArray());

    /// <summary>新建一个可写的"目标父目录"，用于生成演练。</summary>
    public string NewTarget(string name = "target")
    {
        var path = Path2(name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows 上句柄偶尔未释放：清理失败不应让测试变红
        }
    }
}

public static class RepoFiles
{
    private static readonly Lazy<string> Root = new(FindRoot);

    public static string RepositoryRoot => Root.Value;

    /// <summary>内置模板配置的源文件路径（引擎会把它作为 EmbeddedResource 打包）。</summary>
    public static string BuiltinTemplatesFile =>
        Path.Combine(RepositoryRoot, "src", "ProjectManager.Scaffolding", "Resources", "templates.default.json");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "ProjectManager.Scaffolding", "Resources", "templates.default.json");
            if (File.Exists(candidate))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"未能定位仓库根（当前基目录：{AppContext.BaseDirectory}）。请从仓库内运行测试。");
    }
}
