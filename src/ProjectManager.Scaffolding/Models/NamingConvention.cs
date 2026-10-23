namespace ProjectManager.Scaffolding.Models;

/// <summary>命名派生风格（方案 4.2 <c>naming</c> 字段）。</summary>
public sealed class NamingConvention
{
    public static readonly NamingConvention Default = new("pascal", "kebab", "pascal");

    public NamingConvention(string namespaceCase, string packageCase, string dirCase)
    {
        NamespaceCase = namespaceCase;
        PackageCase = packageCase;
        DirCase = dirCase;
    }

    /// <summary>根命名空间风格：<c>pascal</c> | <c>kebab</c> | <c>snake</c>。</summary>
    public string NamespaceCase { get; }

    /// <summary>包名风格。</summary>
    public string PackageCase { get; }

    /// <summary>目录名风格。</summary>
    public string DirCase { get; }
}
