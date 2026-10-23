namespace ProjectManager.Scaffolding.Tests;

/// <summary>定位仓库内固定资产：基线快照、临时工作目录。</summary>
public static class FixtureLocator
{
    private const string Marker = "forge-baseline";

    private static readonly Lazy<string> Root = new(FindRepositoryRoot);

    public static string RepositoryRoot => Root.Value;

    public static string BaselineRoot => Path.Combine(RepositoryRoot, "tests", "fixtures", Marker);

    public static string CaseDirectory(string caseId) => Path.Combine(BaselineRoot, caseId);

    public static string CreateTempDirectory(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), "pm-scaffold-tests", $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>尽力删除临时目录；被占用时不抛错（测试不应因清理失败而变红）。</summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 忽略：Windows 上偶发句柄未释放
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "fixtures", Marker);
            if (Directory.Exists(candidate))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"未能定位 tests/fixtures/{Marker}。请从仓库内运行测试（当前基目录：{AppContext.BaseDirectory}）。");
    }
}
