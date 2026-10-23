using ProjectManager.Scaffolding.IO;

namespace ProjectManager.Application.Components;

/// <summary>一个被识别出的组件（顶层目录或其直接子目录）。</summary>
/// <param name="Name">展示名（路径末级）。</param>
/// <param name="RelativePath">相对项目根的路径，统一用 <c>/</c> 分隔。</param>
/// <param name="Kind">技术栈推断结果；推断不出时为 <see cref="ComponentKinds.Unknown"/>。</param>
/// <param name="MarkerFile">命中的标记文件名；未命中为空串。</param>
public sealed record ProjectComponent(string Name, string RelativePath, string Kind, string MarkerFile)
{
    public bool HasMarker => MarkerFile.Length > 0;
}

public static class ComponentKinds
{
    public const string Unknown = "未识别";
}

/// <summary>
/// 组件识别（WP3.1）：按模板声明的白名单扫描顶层目录，用标记文件推断技术栈。
/// </summary>
/// <remarks>
/// 规则是**刻意保守**的初版（方案第 3 节允许"初版即可用，允许手动修正"）：
/// <list type="number">
/// <item><b>白名单优先</b>：只认模板 <c>components</c> 里声明的顶层目录，其余一律不参与——
/// 避免把用户的临时目录、构建产物当组件；</item>
/// <item><b>永不入列</b>：<c>docs / assets / config / output</c> 与构建产物目录即使出现在白名单里也剔除（方案硬约束）；</item>
/// <item><b>两层收敛</b>：顶层目录自身有标记文件就是单个组件，否则把它的直接子目录各算一个组件；不再向下递归；</item>
/// <item><b>只读</b>：不写任何文件、不落库。组件清单是"当前磁盘事实"，缓存留给上层。</item>
/// </list>
/// </remarks>
public sealed class ComponentScanner
{
    /// <summary>方案硬约束：这些目录永不参与组件识别。</summary>
    private static readonly HashSet<string> NeverScan = new(StringComparer.OrdinalIgnoreCase)
    {
        "docs",
        "assets",
        "config",
        "output",
        ".git",
        ".vs",
        ".idea",
        "bin",
        "obj",
        "node_modules",
    };

    /// <summary>标记文件 → 技术栈。按顺序匹配，先命中先返回。</summary>
    private static readonly (string FileName, string Kind)[] MarkerFiles =
    {
        ("package.json", "Node"),
        ("go.mod", "Go"),
        ("Cargo.toml", "Rust"),
        ("pyproject.toml", "Python"),
        ("requirements.txt", "Python"),
        ("pom.xml", "JVM"),
        ("build.gradle", "JVM"),
        ("build.gradle.kts", "JVM"),
        ("*.csproj", ".NET"),
        ("*.sln", ".NET"),
        ("Dockerfile", "容器"),
        ("docker-compose.yml", "容器"),
        ("docker-compose.yaml", "容器"),
    };

    private readonly IFileSystem _fileSystem;

    public ComponentScanner(IFileSystem? fileSystem = null)
        => _fileSystem = fileSystem ?? PhysicalFileSystem.Instance;

    /// <summary>
    /// 扫描项目根下的组件。白名单为空表示该项目没有组件声明（例如"打开已有项目"接入且无元数据），
    /// 此时返回空集合，由调用方提示"缺少组件声明"而不是乱猜。
    /// </summary>
    public IReadOnlyList<ProjectComponent> Scan(string projectRoot, IReadOnlyList<string>? whitelist)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || whitelist is null || whitelist.Count == 0)
        {
            return Array.Empty<ProjectComponent>();
        }

        if (!_fileSystem.DirectoryExists(projectRoot))
        {
            return Array.Empty<ProjectComponent>();
        }

        var components = new List<ProjectComponent>();

        foreach (var entry in whitelist)
        {
            var name = Normalize(entry);
            if (name.Length == 0 || NeverScan.Contains(name) || name.Contains('/'))
            {
                // 白名单只接受顶层目录名；带分隔符的声明视为配置错误，跳过而不是猜
                continue;
            }

            var topPath = Path.Combine(projectRoot, name);
            if (!_fileSystem.DirectoryExists(topPath))
            {
                continue;
            }

            if (Detect(topPath) is { } self)
            {
                components.Add(new ProjectComponent(name, name, self.Kind, self.Marker));
                continue;
            }

            var children = _fileSystem.EnumerateDirectories(topPath)
                .Select(Path.GetFileName)
                .Where(child => !string.IsNullOrEmpty(child) && !NeverScan.Contains(child!))
                .ToArray();

            if (children.Length == 0)
            {
                // 脚手架刚生成时目录是空的：仍算一个组件，只是技术栈待识别
                components.Add(new ProjectComponent(name, name, ComponentKinds.Unknown, string.Empty));
                continue;
            }

            foreach (var child in children)
            {
                var childPath = Path.Combine(topPath, child!);
                var detected = Detect(childPath);
                components.Add(new ProjectComponent(
                    child!,
                    $"{name}/{child}",
                    detected?.Kind ?? ComponentKinds.Unknown,
                    detected?.Marker ?? string.Empty));
            }
        }

        return components
            .OrderBy(component => component.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>探测单个目录的标记文件。命中返回（技术栈, 标记文件名），否则 <c>null</c>。</summary>
    private (string Kind, string Marker)? Detect(string directory)
    {
        var files = _fileSystem.EnumerateFiles(directory);
        if (files.Count == 0)
        {
            return null;
        }

        var names = files.Select(Path.GetFileName).Where(name => name is not null).Select(name => name!).ToArray();

        foreach (var (fileName, kind) in MarkerFiles)
        {
            if (fileName.StartsWith('*'))
            {
                var suffix = fileName[1..];
                var hit = names.FirstOrDefault(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                if (hit is not null)
                {
                    return (kind, hit);
                }

                continue;
            }

            var exact = names.FirstOrDefault(name => string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return (kind, exact);
            }
        }

        return null;
    }

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim().TrimEnd('/', '\\');
}
