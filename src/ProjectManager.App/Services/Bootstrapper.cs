using System.IO;
using System.Windows;
using ProjectManager.Application.Projects;
using ProjectManager.Application.Wizard;
using ProjectManager.App.Theming;
using ProjectManager.CliInterop;
using ProjectManager.Scaffolding;
using ProjectManager.Scaffolding.Models;
using ProjectManager.Scaffolding.Scaffolding;
using ProjectManager.Scaffolding.Templates;
using ProjectManager.Storage;

namespace ProjectManager.App.Services;

/// <summary>启动装配：读设置 → 定位模板 → 构造引擎与仓储 → 应用主题。</summary>
public static class Bootstrapper
{
    public static ShellContext Create(System.Windows.Application application)
    {
        var paths = new AppPaths();
        var bootWarnings = new List<string>();

        try
        {
            paths.EnsureUserDataDirectory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 用户目录不可写（漫游配置被策略锁定等）：继续用内置模板，只是设置与清单存不下
            bootWarnings.Add($"无法创建用户数据目录：{ex.Message}（本次不会保存设置与项目清单）");
        }

        var settingsStore = new JsonSettingsStore(paths);
        var (settings, settingsWarning) = RunSync(() => settingsStore.LoadAsync());
        if (settingsWarning is not null)
        {
            bootWarnings.Add(settingsWarning);
        }

        var repository = new JsonProjectRepository(paths);

        var loader = TemplateLoader.CreateDefault(paths.TemplatesFile, paths.InstallTemplatesFile);
        IReadOnlyDictionary<string, Template> templates = new Dictionary<string, Template>();
        string? templateWarning = null;
        var sourceText = "未加载";

        try
        {
            loader.TrySeedUserCopy();
            var loaded = loader.Load();
            templates = loaded.Templates;
            sourceText = loaded.Source switch
            {
                TemplateSource.User => $"用户配置（{loaded.SourcePath}）",
                TemplateSource.Install => $"安装目录（{loaded.SourcePath}）",
                _ => "内置默认",
            };

            if (loaded.Warnings.Count > 0)
            {
                templateWarning = string.Join("；", loaded.Warnings);
            }
        }
        catch (ScaffoldException ex)
        {
            templateWarning = $"[{ex.ErrorCode}] {ex.Message}";
        }

        if (bootWarnings.Count > 0)
        {
            templateWarning = templateWarning is null
                ? string.Join("；", bootWarnings)
                : $"{templateWarning}；{string.Join("；", bootWarnings)}";
        }

        var generator = new ScaffoldGenerator();
        var git = new GitRunner();
        var coordinator = new GenerationCoordinator(generator, repository, git);

        var theme = new ThemeService(application);
        theme.Apply(settings.Theme);

        return new ShellContext
        {
            Paths = paths,
            Settings = settings,
            SettingsStore = settingsStore,
            Templates = templates,
            TemplateWarning = templateWarning,
            TemplateSourceText = sourceText,
            Generator = generator,
            Coordinator = coordinator,
            Projects = new ProjectListModel(repository),
            Theme = theme,
            Repository = repository,
        };
    }

    /// <summary>
    /// 启动期同步等待。用 <see cref="Task.Run{TResult}(Func{TResult})"/> 把工作移到线程池：
    /// 存储层内部虽是 <c>ConfigureAwait(false)</c>，但在 UI 线程上直接阻塞等待仍有死锁风险，
    /// 挪到线程池可以彻底避开。
    /// </summary>
    private static T RunSync<T>(Func<Task<T>> factory) => Task.Run(factory).GetAwaiter().GetResult();
}
