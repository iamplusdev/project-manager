using System.ComponentModel;
using System.IO;
using ProjectManager.Application.Components;
using ProjectManager.Application.Mvvm;
using ProjectManager.Application.Projects;
using ProjectManager.App.Services;
using ProjectManager.Storage;

namespace ProjectManager.App.ViewModels;

/// <summary>侧栏主导航。M3 先开「项目」与「组件」两页。</summary>
public enum ShellSection
{
    Projects,
    Components,
}

/// <summary>
/// 主壳模型：项目清单 + 搜索过滤 + 主区三态 + 组件页 + 详情页的 Git 实时状态。
/// 搜索/过滤在 <see cref="ProjectListModel"/>、组件识别在 <see cref="ComponentScanner"/>、
/// Git 查询在 <see cref="ProjectManager.CliInterop.IGitRunner"/>——此处只做编排与视图状态。
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly ShellContext _context;
    private StoredProject? _selected;
    private bool _isSidebarCollapsed;
    private ShellSection _section = ShellSection.Projects;
    private IReadOnlyList<ProjectComponent> _components = Array.Empty<ProjectComponent>();
    private string _gitSummary = string.Empty;
    private bool _isLoadingFacts;
    private int _factsVersion;

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

    public ShellSection Section
    {
        get => _section;
        set
        {
            if (!SetProperty(ref _section, value))
            {
                return;
            }

            Raise(nameof(IsProjectsSection), nameof(IsComponentsSection));
        }
    }

    public bool IsProjectsSection => _section == ShellSection.Projects;

    public bool IsComponentsSection => _section == ShellSection.Components;

    public void ShowProjects() => Section = ShellSection.Projects;

    public void ShowComponents() => Section = ShellSection.Components;

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
                nameof(ShowEmptyState),
                nameof(ComponentHint),
                nameof(HasComponents));

            _ = LoadProjectFactsAsync(value);
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
            return $"{template} · 创建于 {_selected.CreatedAt:yyyy-MM-dd HH:mm}";
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

    // ── 组件页 ──────────────────────────────────────────────────────────

    /// <summary>选中项目的组件清单（实时扫描结果，不落库）。</summary>
    public IReadOnlyList<ProjectComponent> Components
    {
        get => _components;
        private set
        {
            if (SetProperty(ref _components, value))
            {
                Raise(nameof(HasComponents), nameof(ComponentHint), nameof(ComponentCountText));
            }
        }
    }

    public bool HasComponents => _components.Count > 0;

    public string ComponentCountText => $"{_components.Count} 个";

    public string ComponentHint
    {
        get
        {
            if (_selected is null)
            {
                return "先在左侧选择一个项目，再查看它的组件。";
            }

            if (_isLoadingFacts)
            {
                return "正在扫描目录…";
            }

            if (HasComponents)
            {
                return string.Empty;
            }

            return string.IsNullOrEmpty(_selected.TemplateName)
                ? "该项目没有组件声明（缺少 .pm-scaffold.json），无法推断组件边界。"
                : "该项目没有声明组件，或其声明目录在当前磁盘上不存在。";
        }
    }

    // ── 详情页的 Git 实时状态 ────────────────────────────────────────────

    /// <summary>Git 状态摘要。每次打开项目现查，不落库（方案第 4 节：Git 实时状态不入库）。</summary>
    public string GitSummary
    {
        get => _gitSummary;
        private set => SetProperty(ref _gitSummary, value);
    }

    public bool IsLoadingFacts
    {
        get => _isLoadingFacts;
        private set
        {
            if (SetProperty(ref _isLoadingFacts, value))
            {
                Raise(nameof(ComponentHint));
            }
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

    /// <summary>
    /// 选中项目后异步取"当下事实"：组件扫描 + Git 状态。
    /// 两者都可能慢（大目录 / 起 git 进程），所以放后台线程并丢弃过期结果。
    /// </summary>
    private async Task LoadProjectFactsAsync(StoredProject? project)
    {
        var version = ++_factsVersion;

        if (project is null)
        {
            Components = Array.Empty<ProjectComponent>();
            GitSummary = string.Empty;
            return;
        }

        GitSummary = "读取中…";

        var whitelist = ResolveComponentWhitelist(project);
        IReadOnlyList<ProjectComponent> components;
        try
        {
            components = await Task.Run(() => _context.Components.Scan(project.Path, whitelist)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            components = Array.Empty<ProjectComponent>();
        }

        if (version != _factsVersion)
        {
            return;
        }

        Components = components;

        try
        {
            var status = await _context.Git.GetStatusAsync(project.Path).ConfigureAwait(true);
            if (version == _factsVersion)
            {
                GitSummary = status.Summary;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (version == _factsVersion)
            {
                GitSummary = $"Git 状态不可用：{ex.Message}";
            }
        }
    }

    /// <summary>
    /// 组件白名单来自模板声明（方案第 3 节：只认 components 声明的顶层目录）。
    /// 外部项目没有模板 → 返回空，组件页显示"缺少组件声明"而不是乱猜边界。
    /// </summary>
    private IReadOnlyList<string> ResolveComponentWhitelist(StoredProject project)
        => _context.Templates.TryGetValue(project.TemplateName, out var template)
            ? template.Components
            : Array.Empty<string>();

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
