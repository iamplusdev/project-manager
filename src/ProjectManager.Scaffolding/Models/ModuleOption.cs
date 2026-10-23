namespace ProjectManager.Scaffolding.Models;

/// <summary>模板中的一个可选模块（如「Web 前端」），勾选后把 paths 并入结构树。</summary>
public sealed class ModuleOption
{
    public ModuleOption(string key, string label, IReadOnlyList<string> paths)
    {
        Key = key;
        Label = label;
        Paths = paths;
    }

    public string Key { get; }

    public string Label { get; }

    /// <summary>相对路径集合，统一 <c>/</c> 分隔。</summary>
    public IReadOnlyList<string> Paths { get; }
}
