using ProjectManager.Application.Mvvm;
using ProjectManager.Storage;

namespace ProjectManager.Application.Projects;

/// <summary>
/// 主壳的项目列表模型：负责"最近项目"的读取、打开时间刷新与降级提示。
/// 具体的"在资源管理器中打开"由 App 层注入委托，以便此处保持零平台依赖、可单测。
/// </summary>
public sealed class ProjectListModel : ObservableObject
{
    private readonly IProjectRepository _repository;
    private readonly TimeProvider _time;
    private IReadOnlyList<StoredProject> _projects = Array.Empty<StoredProject>();
    private string _searchText = string.Empty;
    private string? _warning;
    private bool _isLoading;

    public ProjectListModel(IProjectRepository repository, TimeProvider? time = null)
    {
        _repository = repository;
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyList<StoredProject> Projects
    {
        get => _projects;
        private set
        {
            if (SetProperty(ref _projects, value))
            {
                Raise(nameof(IsEmpty), nameof(Count), nameof(HasProjects), nameof(VisibleProjects), nameof(HasVisibleProjects));
            }
        }
    }

    /// <summary>
    /// 顶栏搜索词。只影响 <see cref="VisibleProjects"/>，**不改变清单本身**，
    /// 也不落库——关闭应用即遗忘，避免"上次搜索把列表筛空了"的困惑。
    /// </summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty))
            {
                return;
            }

            Raise(nameof(VisibleProjects), nameof(HasVisibleProjects), nameof(IsFiltering));
        }
    }

    public bool IsFiltering => !string.IsNullOrWhiteSpace(_searchText);

    /// <summary>按搜索词过滤后的视图（名称或路径命中，大小写不敏感）。</summary>
    public IReadOnlyList<StoredProject> VisibleProjects => Filter(_projects);

    public bool HasVisibleProjects => VisibleProjects.Count > 0;

    public bool IsEmpty => _projects.Count == 0;

    public bool HasProjects => _projects.Count > 0;

    public int Count => _projects.Count;

    /// <summary>清单降级说明（例如文件损坏已隔离）。非空时必须让用户看到。</summary>
    public string? Warning
    {
        get => _warning;
        private set
        {
            if (SetProperty(ref _warning, value))
            {
                OnPropertyChanged(nameof(HasWarning));
            }
        }
    }

    public bool HasWarning => !string.IsNullOrEmpty(_warning);

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        try
        {
            var result = await _repository.LoadAsync(cancellationToken).ConfigureAwait(true);
            Warning = result.Warning;
            Projects = result.Projects
                .OrderByDescending(project => project.LastOpenedAt)
                .ToArray();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>把项目提到最近列表顶部（打开项目时调用）。返回是否成功。</summary>
    public async Task<bool> TouchAsync(string id, CancellationToken cancellationToken = default)
    {
        var updated = await _repository.TouchAsync(id, _time.GetLocalNow(), cancellationToken).ConfigureAwait(true);
        if (updated is null)
        {
            return false;
        }

        Projects = _projects
            .Select(project => project.Id == updated.Id ? updated : project)
            .OrderByDescending(project => project.LastOpenedAt)
            .ToArray();

        return true;
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        var removed = await _repository.RemoveAsync(id, cancellationToken).ConfigureAwait(true);
        if (removed)
        {
            Projects = _projects.Where(project => project.Id != id).ToArray();
        }

        return removed;
    }

    private IReadOnlyList<StoredProject> Filter(IReadOnlyList<StoredProject> source)
    {
        if (!IsFiltering)
        {
            return source;
        }

        var term = _searchText.Trim();
        return source
            .Where(project =>
                project.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || project.Path.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}
