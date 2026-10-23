using ProjectManager.Application.Mvvm;
using ProjectManager.Application.Projects;
using ProjectManager.App.Services;
using ProjectManager.Storage;

namespace ProjectManager.App.ViewModels;

/// <summary>主壳模型：项目列表 + 空状态 + 降级提示。生成后由窗口触发刷新。</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly ShellContext _context;
    private StoredProject? _selected;

    public MainViewModel(ShellContext context)
    {
        _context = context;
    }

    public ProjectListModel Projects => _context.Projects;

    public bool CanScaffold => _context.CanScaffold;

    public string? TemplateWarning => _context.TemplateWarning;

    public bool HasTemplateWarning => !string.IsNullOrEmpty(_context.TemplateWarning);

    public string TemplateSourceText => _context.TemplateSourceText;

    public StoredProject? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value))
            {
                return;
            }

            Raise(nameof(HasSelection), nameof(SelectedName), nameof(SelectedPath), nameof(SelectedMeta));
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

    public string EmptyHint => CanScaffold
        ? "用脚手架生成一个规范的项目骨架，生成后自动加入项目清单。"
        : "模板配置加载失败，暂时无法新建项目。请检查设置中的模板来源。";

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await Projects.LoadAsync(cancellationToken).ConfigureAwait(true);
        OnPropertyChanged(nameof(CanScaffold));
        OnPropertyChanged(nameof(HasTemplateWarning));
    }
}
