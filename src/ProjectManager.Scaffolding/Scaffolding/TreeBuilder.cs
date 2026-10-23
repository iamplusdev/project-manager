using System.Text;
using ProjectManager.Scaffolding.Models;

namespace ProjectManager.Scaffolding.Scaffolding;

/// <summary>
/// 树展开（WP1.5）。把「模板结构 + 选中模块」合成一棵最终的目录树，并提供三种派生视图：
/// 扁平路径序列、ASCII 树形文本、空叶子目录清单。
/// </summary>
/// <remarks>
/// 所有顺序都按<b>模板 JSON 的书写顺序</b>（Forge 依赖 dict 插入顺序），
/// 因此 <c>DirNode</c> 用有序列表而非字典——顺序即行为，不能换成无序容器。
/// </remarks>
public static class TreeBuilder
{
    public const string PlaceholderFileName = ".gitkeep";

    /// <summary>合成最终树：先深拷贝模板结构（避免污染模板），再按模块勾选顺序并入路径。</summary>
    public static DirNode Build(Template template, IEnumerable<string>? selectedModules)
    {
        var tree = template.Structure.Clone();
        var selected = new HashSet<string>(selectedModules ?? Array.Empty<string>(), StringComparer.Ordinal);

        foreach (var module in template.Modules)
        {
            if (!selected.Contains(module.Key))
            {
                continue;
            }

            foreach (var relative in module.Paths)
            {
                Insert(tree, relative);
            }
        }

        return tree;
    }

    /// <summary>按模块定义顺序把相对路径插入树中（逐段 <c>FindOrAdd</c>）。</summary>
    public static void Insert(DirNode root, string relative)
    {
        var current = root;
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.FindOrAdd(segment);
        }
    }

    /// <summary>把树扁平化为绝对目录路径序列（父在前、子在后，与 Forge <c>flatten_paths</c> 一致）。</summary>
    public static IReadOnlyList<string> FlattenAbsolute(string basePath, DirNode tree)
    {
        var paths = new List<string>();
        Walk(basePath, tree, paths.Add);
        return paths;
    }

    /// <summary>相对路径序列（不含项目根），用于 preview 与测试断言。</summary>
    public static IReadOnlyList<string> FlattenRelative(DirNode tree)
    {
        var paths = new List<string>();
        Walk(string.Empty, tree, absolute => paths.Add(ToRelative(absolute)));
        return paths;
    }

    private static void Walk(string basePath, DirNode node, Action<string> visit)
    {
        foreach (var child in node.Children)
        {
            var path = string.IsNullOrEmpty(basePath)
                ? child.Name
                : Path.Combine(basePath, child.Name);
            visit(path);
            Walk(path, child, visit);
        }
    }

    /// <summary>把 <see cref="Path.Combine"/> 的结果统一成 <c>/</c> 分隔（G6）。</summary>
    public static string ToRelative(string path) => path.Replace('\\', '/');

    /// <summary>
    /// ASCII 树形文本。逐字对齐 Forge 的 <c>tree_string</c>：
    /// 子节点数决定用 <c>├──</c> 还是 <c>└──</c>，非末位子节点的前缀续接 <c>│   </c>。
    /// </summary>
    public static string TreeString(string projectName, DirNode tree)
    {
        var builder = new StringBuilder();
        builder.Append(projectName).Append('/');

        void Build(DirNode node, string prefix)
        {
            var children = node.Children;
            for (var index = 0; index < children.Count; index++)
            {
                var isLast = index == children.Count - 1;
                builder.Append('\n')
                    .Append(prefix)
                    .Append(isLast ? "└── " : "├── ")
                    .Append(children[index].Name)
                    .Append('/');

                if (children[index].Children.Count > 0)
                {
                    Build(children[index], prefix + (isLast ? "    " : "│   "));
                }
            }
        }

        Build(tree, string.Empty);
        return builder.ToString();
    }

    /// <summary>空叶子目录的相对路径（含嵌套层级的完整相对路径）。</summary>
    public static IReadOnlyList<string> EmptyLeafDirectories(DirNode tree)
    {
        var leaves = new List<string>();

        void WalkLeaves(DirNode node, string prefix)
        {
            foreach (var child in node.Children)
            {
                var relative = prefix.Length == 0 ? child.Name : $"{prefix}/{child.Name}";
                if (child.Children.Count > 0)
                {
                    WalkLeaves(child, relative);
                }
                else
                {
                    leaves.Add(relative);
                }
            }
        }

        WalkLeaves(tree, string.Empty);
        return leaves;
    }

    /// <summary>
    /// 从 <c>.gitignore</c> 规则中提取「目录前缀」子集（WP1.5 + 3.3 风险对策）。
    /// 只认不含通配符的路径型规则；<c>output/</c> 与 <c>/output</c> 归一为 <c>output</c>。
    /// 明确不支持正则与 <c>**</c> 语义——超集行为由 UI 文档说明，不假装是完整 gitignore 实现。
    /// </summary>
    public static IReadOnlyList<string> IgnoredDirectoryPrefixes(IReadOnlyList<string> gitignore)
    {
        var prefixes = new List<string>();
        foreach (var raw in gitignore)
        {
            var pattern = raw.Trim().Trim('/');
            if (pattern.Length == 0 || pattern.StartsWith('#') || pattern.StartsWith('!'))
            {
                continue;
            }

            if (pattern.IndexOfAny(new[] { '*', '?', '[' }) >= 0)
            {
                continue;
            }

            prefixes.Add(pattern);
        }

        return prefixes;
    }

    /// <summary>相对路径是否落在任一被忽略目录前缀之下（含自身）。</summary>
    public static bool MatchesIgnoredDirectory(string relative, IReadOnlyList<string> prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (relative == prefix || relative.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
