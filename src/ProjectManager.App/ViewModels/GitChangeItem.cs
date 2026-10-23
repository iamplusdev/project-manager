using ProjectManager.Application.Git;
using ProjectManager.Application.Mvvm;
using ProjectManager.CliInterop;

namespace ProjectManager.App.ViewModels;

/// <summary>
/// 变更清单里的一行（带勾选态）。
/// </summary>
/// <remarks>
/// 勾选**只表示"我打算处理这个文件"**，不直接写索引：
/// 写索引是显式动作（「暂存所选」/「取消暂存」按钮），否则每次误点都会改仓库状态，
/// 而失败还得回滚勾选——多一条能出错的路径，换不来更少的点击。
/// </remarks>
public sealed class GitChangeItem : ObservableObject
{
    private GitChangeEntry _entry;
    private bool _isSelected;

    public GitChangeItem(GitChangeEntry entry)
    {
        _entry = entry;

        // 默认勾选规则：已有暂存内容的不预选（否则一个误点「暂存」会重复处理），
        // 其余预选——打开工作台最常见的动作就是"把这些都提交了"
        _isSelected = !entry.HasStagedChanges && !entry.IsConflicted;
    }

    public GitChangeEntry Entry => _entry;

    public string Path => _entry.Path;

    public string DisplayPath => _entry.DisplayPath;

    public string StatusText => _entry.StatusText;

    public bool HasStagedChanges => _entry.HasStagedChanges;

    public bool HasUnstagedChanges => _entry.HasUnstagedChanges;

    public bool IsConflicted => _entry.IsConflicted;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>刷新同一行的事实（别的操作后状态可能变了），但保留用户的勾选。</summary>
    public void Update(GitChangeEntry entry)
    {
        if (_entry == entry)
        {
            return;
        }

        _entry = entry;
        Raise(nameof(Entry), nameof(Path), nameof(DisplayPath), nameof(StatusText),
            nameof(HasStagedChanges), nameof(HasUnstagedChanges), nameof(IsConflicted));
    }
}

/// <summary>工作台一次动作的结果（供界面提示）。</summary>
public sealed record WorkspaceFeedback(bool Succeeded, string Message)
{
    public static WorkspaceFeedback Ok(string message) => new(true, message);

    public static WorkspaceFeedback Fail(string message) => new(false, message);
}
