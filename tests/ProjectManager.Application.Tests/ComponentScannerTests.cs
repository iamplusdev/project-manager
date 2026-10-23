using ProjectManager.Application.Components;

namespace ProjectManager.Application.Tests;

/// <summary>组件识别：白名单、硬排除目录、两层收敛、标记文件推断、缺失目录与空目录。</summary>
public static class ComponentScannerTests
{
    private static readonly ComponentScanner Scanner = new();

    public static void Run()
    {
        Check.Section("Components · 组件识别");
        ExcludesNeverScanDirectories();
        DetectsTopLevelMarker();
        ExpandsChildrenWhenTopHasNoMarker();
        EmptyTopDirectoryIsStillComponent();
        MissingWhitelistDirectoryIsSkipped();
        EmptyWhitelistMeansNoComponents();
    }

    private static void ExcludesNeverScanDirectories()
    {
        using var workspace = new TempWorkspace("components-never");
        var root = workspace.Root;
        Seed(root, "apps/web/package.json", "{}");
        Seed(root, "docs/index.md", "# docs");
        Seed(root, "assets/logo.png", "binary");
        Seed(root, "output/build.txt", "x");

        var components = Scanner.Scan(root, new[] { "apps", "docs", "assets", "output" });

        Check.Equal(1, components.Count, "docs/assets/output 即使出现在白名单里也必须被剔除");
        Check.Equal("apps/web", components[0].RelativePath, "只应识别出 apps/web");
    }

    private static void DetectsTopLevelMarker()
    {
        using var workspace = new TempWorkspace("components-top");
        var root = workspace.Root;
        Seed(root, "src/Main.csproj", "<Project />");
        Seed(root, "src/Program.cs", "class P {}");
        Seed(root, "src/Sub/Deep.csproj", "<Project />");

        var components = Scanner.Scan(root, new[] { "src" });

        Check.Equal(1, components.Count, "顶层目录自身带标记文件时不应再展开子目录");
        Check.Equal("src", components[0].RelativePath, "组件应是顶层目录本身");
        Check.Equal(".NET", components[0].Kind, "*.csproj 应推断为 .NET");
        Check.That(components[0].HasMarker, "应记录命中的标记文件");
    }

    private static void ExpandsChildrenWhenTopHasNoMarker()
    {
        using var workspace = new TempWorkspace("components-children");
        var root = workspace.Root;
        Seed(root, "apps/web/package.json", "{}");
        Seed(root, "apps/server/go.mod", "module x");
        Seed(root, "apps/rust-tool/Cargo.toml", "[package]");
        Seed(root, "apps/plain/readme.txt", "x");

        var components = Scanner.Scan(root, new[] { "apps" });

        Check.Equal(4, components.Count, "apps 下四个子目录各算一个组件");
        Check.Equal("apps/plain", components[0].RelativePath, "结果应按路径排序");
        Check.Equal("apps/rust-tool", components[1].RelativePath, "结果应按路径排序");
        Check.Equal("apps/server", components[2].RelativePath, "结果应按路径排序");
        Check.Equal("apps/web", components[3].RelativePath, "结果应按路径排序");

        Check.Equal("Node", components[3].Kind, "package.json 应推断为 Node");
        Check.Equal("Go", components[2].Kind, "go.mod 应推断为 Go");
        Check.Equal("Rust", components[1].Kind, "Cargo.toml 应推断为 Rust");
        Check.Equal(ComponentKinds.Unknown, components[0].Kind, "无标记文件的目录应标为未识别");
        Check.That(!components[0].HasMarker, "未识别组件不应有标记文件");
    }

    private static void EmptyTopDirectoryIsStillComponent()
    {
        using var workspace = new TempWorkspace("components-empty");
        var root = workspace.Root;
        Directory.CreateDirectory(Path.Combine(root, "infra"));

        var components = Scanner.Scan(root, new[] { "infra" });

        Check.Equal(1, components.Count, "脚手架刚生成的空目录也应算一个组件");
        Check.Equal("infra", components[0].RelativePath, "空目录组件应为顶层目录本身");
        Check.Equal(ComponentKinds.Unknown, components[0].Kind, "空目录技术栈应为未识别");
    }

    private static void MissingWhitelistDirectoryIsSkipped()
    {
        using var workspace = new TempWorkspace("components-missing");
        var root = workspace.Root;
        Seed(root, "src/Program.cs", "x");

        var components = Scanner.Scan(root, new[] { "src", "packages", "contracts" });

        Check.Equal(1, components.Count, "磁盘上不存在的白名单目录应跳过而不是报错");
        Check.Equal("src", components[0].RelativePath, "存在的 src 应被识别（无标记文件时算未识别）");
    }

    private static void EmptyWhitelistMeansNoComponents()
    {
        using var workspace = new TempWorkspace("components-nolist");
        var root = workspace.Root;
        Seed(root, "src/Program.cs", "x");

        Check.Equal(0, Scanner.Scan(root, Array.Empty<string>()).Count, "无组件声明时不应乱猜");
        Check.Equal(0, Scanner.Scan(root, null).Count, "null 白名单等价于无声明");
        Check.Equal(0, Scanner.Scan(workspace.Path2("nope"), new[] { "src" }).Count, "项目目录不存在时返回空");
    }

    private static void Seed(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
