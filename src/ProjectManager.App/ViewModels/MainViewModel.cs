using System.ComponentModel;
using ProjectManager.Application.Mvvm;
using ProjectManager.Application.Projects;
using ProjectManager.App.Services;
using ProjectManager.Storage;

namespace ProjectManager.App.ViewModels;

/// <summary>
/// 主壳模型：项目清单 + 搜索过滤 + 三态主区（详情 / 最近卡片 / 空态）+ 降级提示。
/// 搜索与过滤逻辑放在 <see cref="ProjectListModel"/>（Application 层），此处只做视图状态编排，
/// 保证"能算的东西"都在可单测的一侧。
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly ShellContext _context;
    private StoredProject? _selected;
    private bool _isSidebarCollapsed;

    public MainViewModel(ShellContext context)
    {
        _context = context;
        _context.Projects.PropertyChanged += OnProjectsChanged;
    }

    public ProjectListModel Projects => _context.Projects;

    public bool CanScaffold => _context.CanScaffold;

    public string? TemplateWarning => _context.TemplateWarning;

    public bool HasTemplateWarning => !string.IsNullOrEmpty(_context.TemplateWarning);

    public string TemplateSourceText => _context.TemplateSourceText;

    // ── 侧栏 ────────────────────────────────────────────────────────────

    /// <summary>侧栏折叠态。折叠后只留图标导航，宽度由窗口按令牌切换。</summary>
    public bool IsSidebarCollapsed
    {
        get => _isSidebarCollapsed;
        set
        {
            if (!SetProperty(ref _isSidebarCollapsed, value))
            {
                return;
            }

            Raise(nameof(SidebarToggleHint));
        }
    }

    public string SidebarToggleHint => _isSidebarCollapsed ? "展开侧栏" : "折叠侧栏";

    /// <summary>顶栏"项目切换"按钮的文字：未选中时提示动作，选中时显示项目名。</summary>
    public string SwitcherLabel => _selected?.Name ?? "选择项目";

    // ── 选中与主区三态 ───────────────────────────────────────────────────

    public StoredProject? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value))
            {
                return;
            }

            Raise(
                nameof(HasSelection),
                nameof(SelectedName),
                nameof(SelectedPath),
                nameof(SelectedMeta),
                nameof(SwitcherLabel),
                nameof(ShowDetail),
                nameof(ShowCards),
                nameof(ShowEmptyState));
        }
    }

    public bool HasSelection => _selected is not null;

    public string SelectedName => _selected?.Name ?? string.Empty;

    public string SelectedPath => _selected?.Path ?? string.Empty;

    public string SelectedMeta
    {
        get
        {
            if (_selected is null)
            {
                return string.Empty;
            }

            var template = string.IsNullOrEmpty(_selected.TemplateName) ? "已有项目" : _selected.TemplateName;
            var git = _selected.GitInitialized ? " · 已初始化 Git" : string.Empty;
            return $"{template}{git} · 创建于 {_selected.CreatedAt:yyyy-MM-dd HH:mm}";
        }
    }

    /// <summary>主区当前显示详情。</summary>
    public bool ShowDetail => _selected is not null;

    /// <summary>主区显示"最近项目"卡片网格（有项目但未选中）。</summary>
    public bool ShowCards => _selected is null && Projects.HasVisibleProjects;

    /// <summary>主区显示空态：清单为空，或搜索无命中。</summary>
    public bool ShowEmptyState => !Projects.HasVisibleProjects;

    public bool HasSearchText => Projects.IsFiltering;

    public string EmptyTitle => Projects.IsFiltering ? "没有匹配的项目" : "还没有项目";

    public string EmptyHint
    {
        get
        {
            if (Projects.IsFiltering)
            {
                return "换个关键词试试，或清空搜索框查看全部项目。";
            }

            return CanScaffold
                ? "用脚手架生成一个规范的项目骨架，生成后自动加入项目清单；也可以打开已有目录。"
                : "模板配置加载失败，暂时无法新建项目。请检查设置中的模板来源。";
        }
    }

    // ── 生命周期 ────────────────────────────────────────────────────────

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await Projects.LoadAsync(cancellationToken).ConfigureAwait(true);
        OnPropertyChanged(nameof(CanScaffold));
        OnPropertyChanged(nameof(HasTemplateWarning));
    }

    /// <summary>
    /// 把已有目录接入清单。成功后重载列表并选中该条，
    /// 让"加进来"这件事在界面上立即可见（与生成后的处理一致）。
    /// </summary>
    public async Task<ImportResult> ImportAsync(string directory, CancellationToken cancellationToken = default)
    {
        var result = await _context.Importer.ImportAsync(directory, cancellationToken).ConfigureAwait(true);
        if (!result.Succeeded)
        {
            return result;
        }

        await ReloadAsync(cancellationToken).ConfigureAwait(true);

        if (result.Project is { } project)
        {
            Selected = Projects.Projects.FirstOrDefault(item => item.Id == project.Id) ?? project;
        }

        return result;
    }

    private void OnProjectsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ProjectListModel.Projects) or nameof(ProjectListModel.VisibleProjects)))
        {
            return;
        }

        // 选中项被搜索筛掉时必须清空选中，否则会出现"列表里看不见，主区却在显示它"
        if (_selected is not null && !Projects.VisibleProjects.Any(item => item.Id == _selected.Id))
        {
            Selected = null;
            return;
        }

        Raise(nameof(ShowCards), nameof(ShowEmptyState), nameof(EmptyTitle), nameof(EmptyHint), nameof(HasSearchText));
    }
}
