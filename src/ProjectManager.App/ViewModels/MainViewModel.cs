using System.ComponentModel;
using System.IO;
using ProjectManager.Application.Components;
using ProjectManager.Application.Git;
using ProjectManager.Application.Mvvm;
using ProjectManager.Application.Projects;
using ProjectManager.Application.Repositories;
using ProjectManager.App.Services;
using ProjectManager.CliInterop;
using ProjectManager.Storage;

namespace ProjectManager.App.ViewModels;

/// <summary>侧栏主导航。M3 开了「项目」「组件」「仓库」三页，M5 补上「变更」。</summary>
public enum ShellSection
{
    Projects,
    Components,
    Repositories,

    /// <summary>Git 工作台：待提交改动 + 差异预览 + 提交。</summary>
    Changes,
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
    private UpgradePlan? _upgradePlan;
    private IReadOnlyList<RepositoryEntry> _repositories = Array.Empty<RepositoryEntry>();
    private bool _isLoadingRepositories;
    private int _repositoryVersion;
    private string _gitSummary = string.Empty;
    private bool _isLoadingFacts;
    private int _factsVersion;

    // ── 变更页（Git 工作台，WP5.1 / WP5.2） ──────────────────────────────
    private GitWorkspaceView? _workspace;
    private IReadOnlyList<GitChangeItem> _changeItems = Array.Empty<GitChangeItem>();
    private GitChangeItem? _selectedChange;
    private IReadOnlyList<DiffRow> _diffRows = Array.Empty<DiffRow>();
    private string _diffTitle = string.Empty;
    private string _diffStat = string.Empty;
    private string _diffHint = DiffRow.ExplainEmpty(null);
    private string _commitTitle = string.Empty;
    private string _commitBody = string.Empty;
    private string _changesMessage = string.Empty;
    private bool _changesMessageIsError;
    private bool _isChangesBusy;
    private int _changesVersion;

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

            Raise(nameof(IsProjectsSection), nameof(IsComponentsSection), nameof(IsRepositoriesSection), nameof(IsChangesSection));

            // 切到仓库页即现查一遍：清单是"当下事实"，缓存只会在用户以为准确时骗人
            if (value == ShellSection.Repositories)
            {
                _ = LoadRepositoriesAsync();
            }

            // 变更页同理：工作区可能刚在别的工具里被改过
            if (value == ShellSection.Changes)
            {
                _ = LoadChangesAsync();
            }
        }
    }

    public bool IsProjectsSection => _section == ShellSection.Projects;

    public bool IsComponentsSection => _section == ShellSection.Components;

    public bool IsRepositoriesSection => _section == ShellSection.Repositories;

    public bool IsChangesSection => _section == ShellSection.Changes;

    public void ShowProjects() => Section = ShellSection.Projects;

    public void ShowComponents() => Section = ShellSection.Components;

    public void ShowRepositories() => Section = ShellSection.Repositories;

    public void ShowChanges() => Section = ShellSection.Changes;

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

            // 换项目后工作台的事实全变了：先把上一个项目的东西清掉，
            // 否则在"新项目不是仓库"时会短暂显示旧项目的改动与 diff
            ResetWorkspaceState();
            if (_section == ShellSection.Changes)
            {
                _ = LoadChangesAsync();
            }
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

    // ── 仓库页（WP3.2 剩余：跨项目仓库清单） ──────────────────────────────

    /// <summary>所有项目当下的仓库事实。进入页面即现查，不缓存、不落库。</summary>
    public IReadOnlyList<RepositoryEntry> Repositories
    {
        get => _repositories;
        private set
        {
            if (SetProperty(ref _repositories, value))
            {
                Raise(nameof(HasRepositories), nameof(RepositoryStatsText), nameof(RepositoryHint));
            }
        }
    }

    public bool IsLoadingRepositories
    {
        get => _isLoadingRepositories;
        private set
        {
            if (SetProperty(ref _isLoadingRepositories, value))
            {
                Raise(nameof(RepositoryHint));
            }
        }
    }

    public bool HasRepositories => _repositories.Count > 0;

    /// <summary>一行统计：仓库数 / 有改动 / 未初始化 / 不可用。文案口径在应用层，可单测。</summary>
    public string RepositoryStatsText => RepositoryStats.From(_repositories).Text;

    public string RepositoryHint
    {
        get
        {
            if (_isLoadingRepositories)
            {
                return "正在查询各项目的 Git 状态…";
            }

            if (_repositories.Count > 0)
            {
                return string.Empty;
            }

            return Projects.Projects.Count == 0
                ? "还没有项目。生成或打开一个项目后，这里会列出它们的仓库状态。"
                : "当前筛选下没有项目。清空顶栏搜索框再试。";
        }
    }

    /// <summary>重新查一遍（页面上的「刷新」）。</summary>
    public Task RefreshRepositoriesAsync() => LoadRepositoriesAsync();

    private async Task LoadRepositoriesAsync()
    {
        var version = ++_repositoryVersion;

        // 与项目列表同一份筛选条件：顶栏搜索是全局的，仓库页不该成为例外
        var targets = Projects.VisibleProjects.ToList();
        if (targets.Count == 0)
        {
            IsLoadingRepositories = false;
            Repositories = Array.Empty<RepositoryEntry>();
            return;
        }

        IsLoadingRepositories = true;

        IReadOnlyList<RepositoryEntry> entries;
        try
        {
            entries = await _context.Repositories.QueryAsync(targets).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (version == _repositoryVersion)
            {
                IsLoadingRepositories = false;
                Repositories = targets
                    .Select(project => RepositoryEntry.Unavailable(project, $"查询失败：{ex.Message}"))
                    .ToList();
            }

            return;
        }

        if (version != _repositoryVersion)
        {
            return;
        }

        IsLoadingRepositories = false;
        Repositories = entries;
    }

    // ── 变更页：Git 工作台（WP5.1 / WP5.2） ──────────────────────────────

    /// <summary>当前项目工作区的变更清单（实时查询，不落库）。</summary>
    public IReadOnlyList<GitChangeItem> ChangeItems
    {
        get => _changeItems;
        private set
        {
            if (SetProperty(ref _changeItems, value))
            {
                Raise(nameof(HasChanges), nameof(ChangesSummary), nameof(CanCommit), nameof(CommitHint));
            }
        }
    }

    public bool HasChanges => _changeItems.Count > 0;

    public string ChangesSummary => _workspace?.Summary ?? string.Empty;

    public string ChangesHint
    {
        get
        {
            if (_selected is null)
            {
                return "先在左侧选择一个项目，再查看它的未提交改动。";
            }

            if (_isChangesBusy && _workspace is null)
            {
                return "正在读取工作区…";
            }

            return _workspace?.EmptyHint ?? string.Empty;
        }
    }

    /// <summary>右侧预览的对象。换文件即重新取差异。</summary>
    public GitChangeItem? SelectedChange
    {
        get => _selectedChange;
        set
        {
            if (!SetProperty(ref _selectedChange, value))
            {
                return;
            }

            _ = LoadDiffAsync(value);
        }
    }

    public IReadOnlyList<DiffRow> DiffRows
    {
        get => _diffRows;
        private set
        {
            if (SetProperty(ref _diffRows, value))
            {
                Raise(nameof(HasDiffRows));
            }
        }
    }

    public bool HasDiffRows => _diffRows.Count > 0;

    public string DiffTitle
    {
        get => _diffTitle;
        private set => SetProperty(ref _diffTitle, value);
    }

    /// <summary>差异统计：<c>修改 · +12 −3</c>；没有 hunk 时为空。</summary>
    public string DiffStat
    {
        get => _diffStat;
        private set => SetProperty(ref _diffStat, value);
    }

    /// <summary>没有可显示的行时给一句解释（未跟踪 / 二进制 / 读取失败各不相同）。</summary>
    public string DiffHint
    {
        get => _diffHint;
        private set
        {
            if (SetProperty(ref _diffHint, value))
            {
                Raise(nameof(HasDiffHint));
            }
        }
    }

    public bool HasDiffHint => _diffHint.Length > 0;

    public string CommitTitle
    {
        get => _commitTitle;
        set
        {
            if (SetProperty(ref _commitTitle, value))
            {
                Raise(nameof(CanCommit), nameof(CommitHint));
            }
        }
    }

    public string CommitBody
    {
        get => _commitBody;
        set => SetProperty(ref _commitBody, value);
    }

    /// <summary>可提交 = 索引区有内容 + 标题非空 + 没有未解决冲突 + 不忙。</summary>
    public bool CanCommit
        => !_isChangesBusy
           && _workspace is { IsRepository: true }
           && _commitTitle.Trim().Length > 0
           && StagedCount > 0
           && !HasConflict;

    public int StagedCount => _workspace?.Tree.StagedCount ?? 0;

    private bool HasConflict => _changeItems.Any(item => item.IsConflicted);

    /// <summary>按钮旁的一句解释：为什么现在点不了提交。</summary>
    public string CommitHint
    {
        get
        {
            if (_selected is null)
            {
                return "未选择项目";
            }

            if (_workspace is { IsRepository: false })
            {
                return "当前目录不是 Git 仓库";
            }

            if (HasConflict)
            {
                return "有未解决的冲突，先解决再提交";
            }

            if (StagedCount == 0)
            {
                return "先勾选文件并暂存";
            }

            if (_commitTitle.Trim().Length == 0)
            {
                return "填写提交标题后即可提交";
            }

            return $"已暂存 {StagedCount} 个文件，可立即提交";
        }
    }

    public string ChangesMessage
    {
        get => _changesMessage;
        private set
        {
            if (SetProperty(ref _changesMessage, value))
            {
                Raise(nameof(HasChangesMessage));
            }
        }
    }

    public bool HasChangesMessage => _changesMessage.Length > 0;

    public bool ChangesMessageIsError
    {
        get => _changesMessageIsError;
        private set => SetProperty(ref _changesMessageIsError, value);
    }

    public bool IsChangesBusy
    {
        get => _isChangesBusy;
        private set
        {
            if (SetProperty(ref _isChangesBusy, value))
            {
                Raise(nameof(CanCommit), nameof(CommitHint));
            }
        }
    }

    public Task RefreshChangesAsync() => LoadChangesAsync();

    /// <summary>清空工作台展示态（换项目、丢了选中时）。不动 UI 上的提交标题——那是用户正在写的东西。</summary>
    private void ResetWorkspaceState()
    {
        _workspace = null;
        ChangeItems = Array.Empty<GitChangeItem>();
        SelectedChange = null;
        DiffRows = Array.Empty<DiffRow>();
        DiffTitle = string.Empty;
        DiffStat = string.Empty;
        DiffHint = DiffRow.ExplainEmpty(null);
        ChangesMessage = string.Empty;
        ChangesMessageIsError = false;
        Raise(nameof(ChangesSummary), nameof(ChangesHint), nameof(CanCommit), nameof(CommitHint), nameof(StagedCount));
    }

    /// <summary>暂存/取消暂存当前勾选的文件。返回结果供界面提示。</summary>
    public async Task<WorkspaceFeedback> SetStagedAsync(bool staged)
    {
        if (_selected is not { } project)
        {
            return WorkspaceFeedback.Fail("未选择项目。");
        }

        var paths = _changeItems.Where(item => item.IsSelected).Select(item => item.Path).ToList();
        if (paths.Count == 0)
        {
            return WorkspaceFeedback.Fail(staged ? "请先勾选要暂存的文件。" : "请先勾选要取消暂存的文件。");
        }

        IsChangesBusy = true;
        try
        {
            var result = await _context.Workspaces
                .SetStagedAsync(project.Path, paths, staged)
                .ConfigureAwait(true);

            if (!result.Success)
            {
                return WorkspaceFeedback.Fail(result.Message);
            }

            var action = staged ? "已暂存" : "已取消暂存";
            var executedPaths = paths;

            // 暂存后立刻刷新：状态栏里的「已暂存 · 修改」必须当场变，否则用户会以为没生效
            await LoadChangesAsync(preserveSelection: true).ConfigureAwait(true);

            // 提交标题可以顺手预填：勾选的文件全在一个顶层目录下时，用目录名当标题是常见起点
            if (staged && _commitTitle.Trim().Length == 0 && executedPaths.Count > 0)
            {
                CommitTitle = GitWorkspaceService.SuggestTitle(executedPaths);
            }

            return WorkspaceFeedback.Ok($"{action} {executedPaths.Count} 个文件。");
        }
        finally
        {
            IsChangesBusy = false;
        }
    }

    /// <summary>提交索引区内容。成功后清空标题/描述并回读工作区。</summary>
    public async Task<WorkspaceFeedback> CommitAsync()
    {
        if (_selected is not { } project)
        {
            return WorkspaceFeedback.Fail("未选择项目。");
        }

        IsChangesBusy = true;
        try
        {
            var outcome = await _context.Workspaces
                .CommitAsync(project.Path, _commitTitle, _commitBody)
                .ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                return WorkspaceFeedback.Fail(outcome.Message);
            }

            // 只有成功才清空输入：失败时用户最需要的就是"刚写的那段还在"
            CommitTitle = string.Empty;
            CommitBody = string.Empty;

            await LoadChangesAsync(preserveSelection: true).ConfigureAwait(true);

            // 详情页的"N 处未提交改动"也过时了
            _ = LoadProjectFactsAsync(project);

            return WorkspaceFeedback.Ok(outcome.Message);
        }
        finally
        {
            IsChangesBusy = false;
        }
    }

    private async Task LoadChangesAsync(bool preserveSelection = false)
    {
        var version = ++_changesVersion;

        if (_selected is not { } project)
        {
            _workspace = null;
            ChangeItems = Array.Empty<GitChangeItem>();
            SelectedChange = null;
            DiffRows = Array.Empty<DiffRow>();
            DiffTitle = string.Empty;
            DiffStat = string.Empty;
            DiffHint = DiffRow.ExplainEmpty(null);
            Raise(nameof(ChangesSummary), nameof(ChangesHint), nameof(CanCommit), nameof(CommitHint));
            return;
        }

        IsChangesBusy = true;

        // 记住旧的勾选与选中，刷新后按路径还原：否则每点一次暂存，用户的勾选就全没了
        var previousSelection = preserveSelection
            ? _changeItems.Where(item => item.IsSelected).Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var previousSelectedPath = _selectedChange?.Path;

        GitWorkspaceView view;
        try
        {
            view = await _context.Workspaces.LoadAsync(project.Path).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (version == _changesVersion)
            {
                IsChangesBusy = false;
                _workspace = GitWorkspaceView.From(GitWorkingTree.Unavailable(ex.Message));
                ChangeItems = Array.Empty<GitChangeItem>();
                ChangesMessage = $"读取工作区失败：{ex.Message}";
                ChangesMessageIsError = true;
                Raise(nameof(ChangesSummary), nameof(ChangesHint), nameof(CanCommit), nameof(CommitHint));
            }

            return;
        }

        if (version != _changesVersion)
        {
            return;
        }

        _workspace = view;

        var items = view.Changes
            .Select(entry =>
            {
                var item = new GitChangeItem(entry);
                if (previousSelection is not null)
                {
                    item.IsSelected = previousSelection.Contains(entry.Path);
                }

                return item;
            })
            .ToList();

        ChangeItems = items;
        IsChangesBusy = false;

        // 选中项：原文件还在就保持，否则选第一个，让右侧不留空
        var next = previousSelectedPath is null
            ? items.FirstOrDefault()
            : items.FirstOrDefault(item => string.Equals(item.Path, previousSelectedPath, StringComparison.OrdinalIgnoreCase))
              ?? items.FirstOrDefault();

        if (!ReferenceEquals(next, _selectedChange))
        {
            SelectedChange = next;
        }
        else if (next is not null)
        {
            // 同一个对象但内容变了（刚暂存过），差异要重取
            _ = LoadDiffAsync(next);
        }

        Raise(nameof(ChangesSummary), nameof(ChangesHint), nameof(CanCommit), nameof(CommitHint), nameof(StagedCount));
    }

    private async Task LoadDiffAsync(GitChangeItem? item)
    {
        if (_selected is not { } project || item is null)
        {
            DiffRows = Array.Empty<DiffRow>();
            DiffTitle = string.Empty;
            DiffStat = string.Empty;

            // 没有可预览的行有两种原因，别用同一句话应付：
            // 工作区本来就干净 vs 有改动但用户还没选文件
            DiffHint = _workspace is { IsRepository: true, HasChanges: false }
                ? "工作区干净，没有可预览的改动。"
                : DiffRow.ExplainEmpty(null);
            return;
        }

        DiffTitle = item.DisplayPath;
        DiffStat = string.Empty;
        DiffRows = Array.Empty<DiffRow>();
        DiffHint = "正在读取差异…";

        FileDiff? diff;
        try
        {
            diff = await _context.Workspaces.LoadDiffAsync(project.Path, item.Entry).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            DiffRows = Array.Empty<DiffRow>();
            DiffHint = $"读取差异失败：{ex.Message}";
            return;
        }

        // 用户可能已经切到别的文件：过期结果直接丢，否则会出现"标题是 A、内容是 B"
        if (!ReferenceEquals(_selectedChange, item))
        {
            return;
        }

        DiffRows = DiffRow.FromHunks(diff.Hunks);
        DiffStat = DiffRows.Count == 0 ? string.Empty : $"{diff.KindText} · {diff.Summary}";
        DiffHint = DiffRow.ExplainEmpty(diff);
    }

    // ── 详情页的模板同步（WP3.5） ────────────────────────────────────────
    /// <summary>与当前模板的对齐预览。只读比对，选中项目时计算。</summary>
    public UpgradePlan? UpgradePlan
    {
        get => _upgradePlan;
        private set
        {
            if (!SetProperty(ref _upgradePlan, value))
            {
                return;
            }

            Raise(nameof(UpgradeSummary), nameof(CanApplyUpgrade), nameof(MissingDirectories), nameof(HasMissingDirectories), nameof(Modules), nameof(HasModules));
        }
    }

    public string UpgradeSummary => _upgradePlan?.Message ?? string.Empty;

    /// <summary>是否可以执行"补新增目录"（仅待升级且确有缺失时）。</summary>
    public bool CanApplyUpgrade => _upgradePlan?.CanApply == true;

    public IReadOnlyList<string> MissingDirectories => _upgradePlan?.MissingDirectories ?? Array.Empty<string>();

    public bool HasMissingDirectories => MissingDirectories.Count > 0;

    /// <summary>生成时勾选的模块（详情页展示"这个项目当初选了什么"，WP3.3）。</summary>
    public IReadOnlyList<string> Modules => _upgradePlan?.Modules ?? Array.Empty<string>();

    public bool HasModules => Modules.Count > 0;

    /// <summary>
    /// 执行"只加不删"补结构。返回结果供 UI 提示；执行后重新比对，提示自动变成"已对齐"。
    /// </summary>
    public UpgradeOutcome? ApplyUpgrade()
    {
        if (_selected is not { } project || _upgradePlan is not { } plan || !plan.CanApply)
        {
            return null;
        }

        if (!_context.Templates.TryGetValue(project.TemplateName, out var template))
        {
            return null;
        }

        var outcome = _context.Upgrades.Apply(project.Path, template, plan);
        _ = LoadProjectFactsAsync(project);
        return outcome;
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
        RefreshTemplateState();
    }

    /// <summary>
    /// 设置页可能导入/停用了模板配置，回到主壳要把"能不能新建""告警是什么"重新算一遍，
    /// 否则用户改完了界面还停在旧结论上。
    /// </summary>
    public void RefreshTemplateState() =>
        Raise(nameof(CanScaffold), nameof(TemplateWarning), nameof(HasTemplateWarning), nameof(TemplateSourceText));

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
            UpgradePlan = null;
            GitSummary = string.Empty;
            return;
        }

        GitSummary = "读取中…";

        var whitelist = ResolveComponentWhitelist(project);
        _context.Templates.TryGetValue(project.TemplateName, out var template);

        IReadOnlyList<ProjectComponent> components;
        UpgradePlan plan;
        try
        {
            // 扫描与比对都要读磁盘，一起丢到后台；两者都不写文件
            (components, plan) = await Task.Run(() => (
                _context.Components.Scan(project.Path, whitelist),
                _context.Upgrades.Plan(project.Path, project.TemplateName, template))).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            components = Array.Empty<ProjectComponent>();
            plan = new UpgradePlan
            {
                State = UpgradeState.NotManaged,
                Message = $"读取项目目录失败：{ex.Message}",
            };
        }

        if (version != _factsVersion)
        {
            return;
        }

        Components = components;
        UpgradePlan = plan;

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

        // 清单变了（首次加载、生成新项目、导入、搜索过滤）而用户正停在仓库页：那一页的数据已经过期
        if (_section == ShellSection.Repositories)
        {
            _ = LoadRepositoriesAsync();
        }

        // 变更页同理：导入/删除项目会改变"当前项目"的含义
        if (_section == ShellSection.Changes)
        {
            _ = LoadChangesAsync();
        }
    }
}
