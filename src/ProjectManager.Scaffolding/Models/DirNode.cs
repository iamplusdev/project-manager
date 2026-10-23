namespace ProjectManager.Scaffolding.Models;

/// <summary>
/// 目录树节点。用有序列表保存子节点，保证「结构顺序」与模板 JSON 的书写顺序一致
/// （Forge 依赖 dict 插入顺序，README/structure.md 的树形输出也因此稳定）。
/// </summary>
public sealed class DirNode
{
    private readonly List<DirNode> _children = new();

    public DirNode(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public IReadOnlyList<DirNode> Children => _children;

    public bool IsLeaf => _children.Count == 0;

    /// <summary>按名查找子节点，不存在则按顺序追加（等价于 Forge 的 <c>setdefault</c>）。</summary>
    public DirNode FindOrAdd(string name)
    {
        foreach (var child in _children)
        {
            if (string.Equals(child.Name, name, StringComparison.Ordinal))
            {
                return child;
            }
        }

        var created = new DirNode(name);
        _children.Add(created);
        return created;
    }

    /// <summary>深拷贝整棵子树（模板结构必须可复用，不能被模块合并污染）。</summary>
    public DirNode Clone()
    {
        var copy = new DirNode(Name);
        foreach (var child in _children)
        {
            copy._children.Add(child.Clone());
        }

        return copy;
    }
}
