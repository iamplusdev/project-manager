using System.Collections.ObjectModel;
using System.ComponentModel;
using ProjectManager.Application.Mvvm;
using ProjectManager.Scaffolding;
using ProjectManager.Scaffolding.IO;
using ProjectManager.Scaffolding.Models;
using ProjectManager.Scaffolding.Naming;
using ProjectManager.Scaffolding.Scaffolding;
using ProjectManager.Scaffolding.Validation;
using ProjectManager.Storage;

namespace ProjectManager.Application.Wizard;

/// <summary>
/// 新建项目向导的单一 ViewModel：三步（基本信息 → 模板与模块 → 预览确认）+ 结果页。
/// </summary>
/// <remarks>
/// <para>
/// 刻意<b>不依赖 WPF</b>：状态机、校验聚合、预览树构建、生成编排全部在此，
/// 因此可以在无窗口环境完整单测（含"生成即注册"闭环）。
/// </para>
/// <para>
/// 步骤切换用同一个实例，后退不丢数据；Result 是终态，不允许回退——
/// 回退到表单再生成一次，只会让用户重复覆盖刚生成的内容。
/// </para>
/// </remarks>
public sealed class NewProjectWizard : ObservableObject
{
    /// <summary>首次启动默认选中的模板键；模板集里没有它时回退到第一个。</summary>
    public const string RecommendedTemplateKey = "单体桌面工具";

    private readonly ScaffoldGenerator _generator;
    private readonly GenerationCoordinator _coordinator;
    private readonly IFileSystem _fs;
    private readonly AppSettings _settings;
    private readonly List<TemplateCard> _templates;

    private WizardStep _step = WizardStep.Basic;
    private TemplateCard? _selectedTemplate;
    private bool _isBusy;
    private bool _initGit = true;
    private GenerationStrategy _strategy = GenerationStrategy.Merge;
    private GenerationOutcome? _outcome;
    private string _projectName = string.Empty;
    private string _description = string.Empty;
    private string _targetDirectory = string.Empty;
    private string? _nameError;
    private string? _descriptionError;
    private string? _pathError;
    private string? _previewWarning;
    private string _statusText = string.Empty;
    private NameDeriver.DerivedNames? _derived;
    private PreviewSummary _summary = PreviewSummary.Empty;
    private IReadOnlyList<PreviewNode> _previewRoots = Array.Empty<PreviewNode>();

    public NewProjectWizard(
        IEnumerable<Template> templates,
        ScaffoldGenerator generator,
        GenerationCoordinator coordinator,
        AppSettings? settings = null,
        IFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(templates);
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _fs = fileSystem ?? new PhysicalFileSystem();
        _settings = settings ?? new AppSettings();

        _templates = templates
            .Select(template => new TemplateCard(template, template.Name == RecommendedTemplateKey))
            .ToList();

        if (_templates.Count == 0)
        {
            throw new ArgumentException("至少需要一个模板。", nameof(templates));
        }

        // 默认选中：推荐模板 > 设置里记住的模板 > 第一个
        _selectedTemplate =
            _templates.FirstOrDefault(card => card.IsRecommended)
            ?? _templates.FirstOrDefault(card => card.Key == _settings.DefaultTemplateName)
            ?? _templates[0];

        ProjectName = string.Empty;
        Description = string.Empty;
        TargetDirectory = _settings.DefaultTargetDirectory;
        RebuildModules();
    }

    // ── 数据 ───────────────────────────────────────────────────────────────

    public IReadOnlyList<TemplateCard> Templates => _templates;

    public TemplateCard? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (!SetProperty(ref _selectedTemplate, value))
            {
                return;
            }

            RebuildModules();
            OnPropertyChanged(nameof(HasModules));
            OnPropertyChanged(nameof(ModuleHint));
            RaiseNavigation();
        }
    }

    public Template? CurrentTemplate => _selectedTemplate?.Template;

    public ObservableCollection<ModuleSelection> Modules { get; } = new();

    public bool HasModules => Modules.Count > 0;

    public string ModuleHint => HasModules
        ? $"{Modules.Count} 个模块可勾选，全部勾选即生成完整骨架"
        : "该模板无需配置模块，预览树将展示全部目录";

    public string ProjectName
    {
        get => _projectName;
        set
        {
            if (!SetProperty(ref _projectName, value))
            {
                return;
            }

            NameError = ProjectValidator.ValidateName(value);
            UpdateDerived();
            RaiseNavigation();
            InvalidatePreview();
        }
    }

    public string Description
    {
        get => _description;
        set
        {
            if (!SetProperty(ref _description, value))
            {
                return;
            }

            DescriptionError = ProjectValidator.ValidateDescription(value);
            RaiseNavigation();
        }
    }

    public string TargetDirectory
    {
        get => _targetDirectory;
        set
        {
            if (!SetProperty(ref _targetDirectory, value))
            {
                return;
            }

            PathError = ValidatePathShallow(value);
            RaiseNavigation();
            InvalidatePreview();
        }
    }

    public string? NameError
    {
        get => _nameError;
        private set
        {
            if (SetProperty(ref _nameError, value))
            {
                OnPropertyChanged(nameof(HasNameError));
            }
        }
    }

    public bool HasNameError => !string.IsNullOrEmpty(NameError);

    public string? DescriptionError
    {
        get => _descriptionError;
        private set
        {
            if (SetProperty(ref _descriptionError, value))
            {
                OnPropertyChanged(nameof(HasDescriptionError));
            }
        }
    }

    public bool HasDescriptionError => !string.IsNullOrEmpty(DescriptionError);

    public string? PathError
    {
        get => _pathError;
        private set
        {
            if (SetProperty(ref _pathError, value))
            {
                OnPropertyChanged(nameof(HasPathError));
            }
        }
    }

    public bool HasPathError => !string.IsNullOrEmpty(PathError);

    /// <summary>派生标识（只读展示，不可编辑——引擎的切词规则在各处必须一致）。</summary>
    public NameDeriver.DerivedNames? Derived
    {
        get => _derived;
        private set => SetProperty(ref _derived, value);
    }

    // ── 步骤与状态 ─────────────────────────────────────────────────────────

    public WizardStep Step
    {
        get => _step;
        private set
        {
            if (!SetProperty(ref _step, value))
            {
                return;
            }

            Raise(nameof(IsBasicStep), nameof(IsTemplateStep), nameof(IsPreviewStep), nameof(IsResultStep));
            Raise(nameof(StepIndex), nameof(IsFirstStep));
            Raise(nameof(IsStep1Active), nameof(IsStep1Done), nameof(IsStep2Active), nameof(IsStep2Done));
            Raise(nameof(IsStep3Active), nameof(IsStep3Done), nameof(WindowTitle), nameof(FooterHint));
            Raise(nameof(IsNextVisible), nameof(IsNotResultStep));
            RaiseNavigation();
        }
    }

    public bool IsBasicStep => Step == WizardStep.Basic;

    public bool IsTemplateStep => Step == WizardStep.Template;

    public bool IsPreviewStep => Step == WizardStep.Preview;

    public bool IsResultStep => Step == WizardStep.Result;

    /// <summary>步骤条索引（0-based），供 UI 高亮。</summary>
    public int StepIndex => Step switch
    {
        WizardStep.Basic => 0,
        WizardStep.Template => 1,
        WizardStep.Preview => 2,
        _ => 3,
    };

    public bool IsFirstStep => Step == WizardStep.Basic;

    // 步骤条视觉状态。放在 VM 里是为了让步骤条保持纯绑定，不需要 code-behind 或转换器。
    public bool IsStep1Active => Step == WizardStep.Basic;

    public bool IsStep1Done => StepIndex > 0;

    public bool IsStep2Active => Step == WizardStep.Template;

    public bool IsStep2Done => StepIndex > 1;

    public bool IsStep3Active => Step == WizardStep.Preview;

    public bool IsStep3Done => StepIndex > 2;

    /// <summary>窗口标题随步骤变化（自绘标题栏前的低成本方案，同时利于无障碍朗读）。</summary>
    public string WindowTitle => Step switch
    {
        WizardStep.Basic => "新建项目 · 第 1 步，共 3 步：基本信息",
        WizardStep.Template => "新建项目 · 第 2 步，共 3 步：模板与模块",
        WizardStep.Preview => "新建项目 · 第 3 步，共 3 步：预览确认",
        _ => "新建项目 · 结果",
    };

    /// <summary>底栏左侧提示：说明当前步骤"下一步会发生什么"。</summary>
    public string FooterHint => Step switch
    {
        WizardStep.Basic => "名称将作为目录名与根命名空间来源。",
        WizardStep.Template => "勾选模块决定生成哪些目录；可随时返回修改。",
        WizardStep.Preview => "点「生成」写入磁盘；选「仅预览」则不写任何文件。",
        _ => "可打开目录查看，或在项目列表中随时回到这里。",
    };

    /// <summary>「下一步」在前两步可见，第三步换成「生成」。</summary>
    public bool IsNextVisible => Step is WizardStep.Basic or WizardStep.Template;

    /// <summary>结果页是终态：隐藏「上一步」「取消」。</summary>
    public bool IsNotResultStep => Step != WizardStep.Result;

    public bool CanGoNext => !IsBusy && Step switch
    {
        WizardStep.Basic => IsBasicValid,
        WizardStep.Template => _selectedTemplate is not null,
        _ => false,
    };

    public bool CanGoBack => !IsBusy && Step is WizardStep.Template or WizardStep.Preview;

    public bool CanGenerate => !IsBusy && Step == WizardStep.Preview && IsBasicValid && _selectedTemplate is not null;

    /// <summary>表单是否已有实质输入（关闭拦截用）。</summary>
    public bool HasAnyInput =>
        _projectName.Trim().Length > 0
        || _description.Trim().Length > 0
        || (Step != WizardStep.Basic && !IsResultStep);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsIdle));
            RaiseNavigation();
        }
    }

    public bool IsIdle => !IsBusy;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public GenerationOutcome? Outcome
    {
        get => _outcome;
        private set
        {
            if (!SetProperty(ref _outcome, value))
            {
                return;
            }

            Raise(nameof(HasOutcome), nameof(CanRetryRegistration), nameof(IsSuccess));
            Raise(nameof(ResultSummary), nameof(OutcomeWarning), nameof(HasOutcomeWarning), nameof(HasResultStats));
        }
    }

    public bool HasOutcome => _outcome is not null;

    public bool IsSuccess => _outcome?.Kind is GenerationOutcomeKind.Success or GenerationOutcomeKind.PreviewOnly;

    /// <summary>注册失败时可重试补偿，不必重新生成。</summary>
    public bool CanRetryRegistration => _outcome is { RegistrationFailed: true };

    public bool HasResultStats => _outcome?.Result is not null;

    /// <summary>结果页统计：只报数量，不铺设全部路径（避免刷屏）。</summary>
    public string ResultSummary
    {
        get
        {
            if (_outcome?.Result is not { } result)
            {
                return string.Empty;
            }

            if (result.DryRun)
            {
                return "本次为预览，未写入任何文件。";
            }

            return $"新建 {result.DirectoriesCreated.Count} 个目录 · 写入 {result.FilesWritten.Count} 个文件"
                + $" · 备份 {result.Backups.Count} 个 · 保留 {result.Preserved.Count} 个"
                + (result.SkippedExisting.Count > 0 ? $" · 跳过已存在 {result.SkippedExisting.Count} 个" : string.Empty);
        }
    }

    /// <summary>结果页需要额外提醒的事项（Git 未初始化 / 注册失败 / 回滚不完整）。</summary>
    public string? OutcomeWarning
    {
        get
        {
            if (_outcome is not { } outcome)
            {
                return null;
            }

            var parts = new List<string>();
            if (outcome.RegistrationFailed)
            {
                parts.Add($"项目清单写入失败：{outcome.RegistrationError ?? "未知原因"}。文件已生成，可点「重试注册」。");
            }

            if (outcome.GitFailed)
            {
                parts.Add($"Git 仓库未初始化：{outcome.GitMessage ?? "git 不可用"}。目录与文件不受影响。");
            }

            if (outcome.Kind == GenerationOutcomeKind.RollbackFailed)
            {
                parts.Add(ManualCleanupHint(outcome));
            }

            return parts.Count == 0 ? null : string.Join("\n", parts);
        }
    }

    public bool HasOutcomeWarning => !string.IsNullOrEmpty(OutcomeWarning);

    private static string ManualCleanupHint(GenerationOutcome outcome)
    {
        if (outcome.ManualCleanupList.Count == 0)
        {
            return "回滚未能完成，请手工检查目标目录并删除多余的空目录。";
        }

        var preview = string.Join("、", outcome.ManualCleanupList.Take(5));
        var more = outcome.ManualCleanupList.Count > 5 ? $" 等 {outcome.ManualCleanupList.Count} 项" : string.Empty;
        return $"回滚未能完成，以下位置需要手工清理：{preview}{more}。";
    }

    public GenerationStrategy Strategy
    {
        get => _strategy;
        set
        {
            if (SetProperty(ref _strategy, value))
            {
                Raise(nameof(IsMergeStrategy), nameof(IsPreviewOnlyStrategy), nameof(CanGenerate));
            }
        }
    }

    public bool IsMergeStrategy
    {
        get => _strategy == GenerationStrategy.Merge;
        set
        {
            if (value)
            {
                Strategy = GenerationStrategy.Merge;
            }
        }
    }

    public bool IsPreviewOnlyStrategy
    {
        get => _strategy == GenerationStrategy.PreviewOnly;
        set
        {
            if (value)
            {
                Strategy = GenerationStrategy.PreviewOnly;
            }
        }
    }

    /// <summary>生成后初始化 Git 仓库。默认勾选（脚手架产物本就该纳入版本控制）。</summary>
    public bool InitGit
    {
        get => _initGit;
        set => SetProperty(ref _initGit, value);
    }

    // ── 预览 ───────────────────────────────────────────────────────────────

    public IReadOnlyList<PreviewNode> PreviewRoots
    {
        get => _previewRoots;
        private set => SetProperty(ref _previewRoots, value);
    }

    public PreviewSummary Summary
    {
        get => _summary;
        private set
        {
            if (SetProperty(ref _summary, value))
            {
                Raise(nameof(SummaryCreateText), nameof(SummaryBackupText), nameof(SummaryPreserveText), nameof(HasTargetExisted));
            }
        }
    }

    public string SummaryCreateText => _summary.CreateText;

    public string SummaryBackupText => _summary.BackupText;

    public string SummaryPreserveText => _summary.PreserveText;

    public bool HasTargetExisted => _summary.TargetExisted;

    /// <summary>预检告警（例如路径过长、目录位置被文件占用）。非空时生成按钮仍可用，由用户决定。</summary>
    public string? PreviewWarning
    {
        get => _previewWarning;
        private set
        {
            if (SetProperty(ref _previewWarning, value))
            {
                OnPropertyChanged(nameof(HasPreviewWarning));
            }
        }
    }

    public bool HasPreviewWarning => !string.IsNullOrEmpty(PreviewWarning);

    // ── 导航 ───────────────────────────────────────────────────────────────

    public bool Next()
    {
        if (IsBusy)
        {
            return false;
        }

        switch (Step)
        {
            case WizardStep.Basic:
                ValidateBasic(deep: true);
                if (!IsBasicValid)
                {
                    return false;
                }

                Step = WizardStep.Template;
                return true;

            case WizardStep.Template:
                if (_selectedTemplate is null)
                {
                    return false;
                }

                RefreshPreview();
                Step = WizardStep.Preview;
                return true;

            default:
                return false;
        }
    }

    public bool Back()
    {
        if (!CanGoBack)
        {
            return false;
        }

        Step = Step == WizardStep.Preview ? WizardStep.Template : WizardStep.Basic;
        return true;
    }

    public void SelectAllModules()
    {
        foreach (var module in Modules)
        {
            module.IsSelected = true;
        }
    }

    public void ClearAllModules()
    {
        foreach (var module in Modules)
        {
            module.IsSelected = false;
        }
    }

    public void InvertModules()
    {
        foreach (var module in Modules)
        {
            module.IsSelected = !module.IsSelected;
        }
    }

    // ── 生成 ───────────────────────────────────────────────────────────────

    public GenerationRequest BuildRequest(bool dryRun = false) => new()
    {
        ProjectName = _projectName.Trim(),
        Description = _description.Trim(),
        TargetDirectory = _targetDirectory.Trim(),
        SelectedModules = Modules.Where(module => module.IsSelected).Select(module => module.Key).ToArray(),
        DryRun = dryRun,
    };

    /// <summary>
    /// 执行生成。取消只在阶段间隙生效（<c>CancellationToken</c> 由窗口在关闭/取消时触发）。
    /// </summary>
    public async Task<GenerationOutcome> GenerateAsync(
        IProgress<GenerationStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_selectedTemplate is null)
        {
            throw new InvalidOperationException("未选择模板。");
        }

        ValidateBasic(deep: true);
        if (!IsBasicValid)
        {
            throw new InvalidOperationException("表单校验未通过，不应到达生成步骤。");
        }

        IsBusy = true;
        StatusText = "正在生成…";
        try
        {
            var job = new GenerationJob(BuildRequest(), _selectedTemplate.Template, _strategy, _initGit);

            // Progress<T> 在 UI 线程构造，回调会 marshal 回 UI 线程；外部传入的 progress 继续转发
            var reporter = new Progress<GenerationStage>(stage =>
            {
                StatusText = stage switch
                {
                    GenerationStage.Preparing => "正在校验生成条件…",
                    GenerationStage.Generating => "正在创建目录与文件…",
                    GenerationStage.InitializingGit => "正在初始化 Git 仓库…",
                    GenerationStage.Registering => "正在加入项目清单…",
                    _ => "已完成",
                };
                progress?.Report(stage);
            });

            var outcome = await _coordinator.RunAsync(job, reporter, cancellationToken)
                .ConfigureAwait(true);

            Outcome = outcome;

            if (outcome.Kind == GenerationOutcomeKind.Success)
            {
                // 记住本次选择，下次预填（设置由 App 层在窗口关闭时落盘）
                _settings.DefaultTargetDirectory = _targetDirectory.Trim();
                _settings.DefaultTemplateName = _selectedTemplate.Key;
            }

            StatusText = outcome.Headline;
            Step = WizardStep.Result;
            return outcome;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>结果页「重试注册」：文件已在磁盘上，只补写清单。</summary>
    public async Task<bool> RetryRegistrationAsync(CancellationToken cancellationToken = default)
    {
        if (_outcome is not { } previous || previous.ProjectPath is null || _selectedTemplate is null)
        {
            return false;
        }

        try
        {
            var project = await _coordinator.RetryRegistrationAsync(
                previous.ProjectPath,
                _projectName.Trim(),
                _selectedTemplate.Key,
                previous.GitSucceeded,
                cancellationToken).ConfigureAwait(true);

            Outcome = new GenerationOutcome
            {
                Kind = previous.Kind,
                Result = previous.Result,
                Project = project,
                GitSucceeded = previous.GitSucceeded,
                GitFailed = previous.GitFailed,
                GitMessage = previous.GitMessage,
                RegistrationFailed = project is null,
            };

            StatusText = Outcome.Headline;
            return project is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Outcome = new GenerationOutcome
            {
                Kind = previous.Kind,
                Result = previous.Result,
                RegistrationFailed = true,
                RegistrationError = ex.Message,
                GitSucceeded = previous.GitSucceeded,
                GitFailed = previous.GitFailed,
            };
            return false;
        }
    }

    /// <summary>重新计算预览树与三类清单。Step 3 进入前调用；模块勾选变化时增量触发。</summary>
    public PreviewSummary RefreshPreview()
    {
        PreviewWarning = null;
        Summary = PreviewSummary.Empty;
        PreviewRoots = Array.Empty<PreviewNode>();

        var template = _selectedTemplate?.Template;
        if (template is null || !IsBasicValid)
        {
            return Summary;
        }

        try
        {
            var request = BuildRequest();
            var plan = _generator.Preview(request, template);
            var tree = TreeBuilder.Build(template, request.SelectedModules);

            var createDirs = new HashSet<string>(plan.DirectoriesToCreate, StringComparer.OrdinalIgnoreCase);
            var createFiles = new HashSet<string>(plan.TextFilesToWrite, StringComparer.OrdinalIgnoreCase);
            var injectFiles = new HashSet<string>(plan.FilesToInject, StringComparer.OrdinalIgnoreCase);
            var backupFiles = new HashSet<string>(plan.FilesToBackup, StringComparer.OrdinalIgnoreCase);
            var preserveFiles = new HashSet<string>(plan.FilesToPreserve, StringComparer.OrdinalIgnoreCase);
            var ignoredPrefixes = TreeBuilder.IgnoredDirectoryPrefixes(template.Gitignore);

            var roots = new List<PreviewNode>();
            foreach (var child in tree.Children)
            {
                roots.Add(BuildNode(child, child.Name, createDirs, backupFiles, preserveFiles, ignoredPrefixes));
            }

            // 文档与注入文件挂在根下，让"将写入哪些文件"在预览里可见
            foreach (var file in createFiles.Concat(injectFiles).OrderBy(name => name, StringComparer.Ordinal))
            {
                roots.Add(new PreviewNode(file, file, PreviewNodeKind.File, PreviewStatus.Create));
            }

            foreach (var file in backupFiles.OrderBy(name => name, StringComparer.Ordinal))
            {
                roots.Add(new PreviewNode(file, file, PreviewNodeKind.File, PreviewStatus.Backup));
            }

            foreach (var file in preserveFiles.OrderBy(name => name, StringComparer.Ordinal))
            {
                roots.Add(new PreviewNode(file, file, PreviewNodeKind.File, PreviewStatus.Preserve));
            }

            PreviewRoots = roots;
            Summary = new PreviewSummary(
                plan.DirectoriesToCreate.Count,
                plan.TextFilesToWrite.Count + plan.FilesToInject.Count,
                plan.FilesToBackup.Count,
                plan.FilesToPreserve.Count,
                plan.PlaceholdersToCreate.Count,
                plan.TargetExisted);

            return Summary;
        }
        catch (ScaffoldException ex)
        {
            PreviewWarning = $"[{ex.ErrorCode}] {ex.Message}";
            return Summary;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            PreviewWarning = $"预检失败：{ex.Message}";
            return Summary;
        }
    }

    private PreviewNode BuildNode(
        DirNode node,
        string relativePath,
        HashSet<string> createDirs,
        HashSet<string> backupFiles,
        HashSet<string> preserveFiles,
        IReadOnlyList<string> ignoredPrefixes)
    {
        var status = PreviewStatus.Skip;
        if (TreeBuilder.MatchesIgnoredDirectory(relativePath, ignoredPrefixes))
        {
            status = PreviewStatus.Ignored;
        }
        else if (createDirs.Contains(relativePath))
        {
            status = PreviewStatus.Create;
        }

        var preview = new PreviewNode(node.Name, relativePath, PreviewNodeKind.Directory, status)
        {
            IsExpanded = node.Children.Count > 0 && relativePath.Count(ch => ch == '/') < 1,
        };

        foreach (var child in node.Children)
        {
            preview.Children.Add(BuildNode(
                child,
                $"{relativePath}/{child.Name}",
                createDirs,
                backupFiles,
                preserveFiles,
                ignoredPrefixes));
        }

        return preview;
    }

    // ── 内部 ───────────────────────────────────────────────────────────────

    private bool IsBasicValid =>
        !HasNameError && !HasDescriptionError && !HasPathError
        && _projectName.Trim().Length > 0
        && _description.Trim().Length > 0
        && _targetDirectory.Trim().Length > 0;

    private void ValidateBasic(bool deep)
    {
        NameError = ProjectValidator.ValidateName(_projectName);
        DescriptionError = ProjectValidator.ValidateDescription(_description);
        PathError = deep
            ? ProjectValidator.ValidateTargetDirectory(_targetDirectory, _fs)
            : ValidatePathShallow(_targetDirectory);
    }

    /// <summary>轻量校验（只查存在性，不写探针文件）：供输入过程中即时反馈。</summary>
    private string? ValidatePathShallow(string? value)
    {
        var cleaned = (value ?? string.Empty).Trim();
        if (cleaned.Length == 0)
        {
            return "请选择生成路径";
        }

        return _fs.DirectoryExists(cleaned) ? null : "生成路径不存在或不是文件夹";
    }

    private void RebuildModules()
    {
        foreach (var module in Modules)
        {
            module.PropertyChanged -= OnModuleChanged;
        }

        Modules.Clear();
        var template = _selectedTemplate?.Template;
        if (template is not null)
        {
            foreach (var option in template.Modules)
            {
                // 与 Forge 一致：默认全选
                var selection = new ModuleSelection(option, isSelected: true);
                selection.PropertyChanged += OnModuleChanged;
                Modules.Add(selection);
            }
        }

        OnPropertyChanged(nameof(HasModules));
        OnPropertyChanged(nameof(ModuleHint));
        UpdateDerived();
        InvalidatePreview();
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ModuleSelection.IsSelected))
        {
            return;
        }

        // 勾选变化只影响预览：Step 2 已展示预览树，必须同步刷新
        if (Step == WizardStep.Preview || Step == WizardStep.Template)
        {
            RefreshPreview();
        }
    }

    private void UpdateDerived()
    {
        var name = _projectName.Trim();
        Derived = name.Length == 0
            ? null
            : NameDeriver.Derive(name, _selectedTemplate?.Template.Naming);
    }

    private void InvalidatePreview()
    {
        if (Step == WizardStep.Preview)
        {
            RefreshPreview();
        }
    }

    private void RaiseNavigation()
    {
        Raise(nameof(CanGoNext), nameof(CanGoBack), nameof(CanGenerate), nameof(HasAnyInput), nameof(IsBasicValid));
    }
}
