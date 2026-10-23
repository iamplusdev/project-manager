using System.IO;
using ProjectManager.Application.Components;
using ProjectManager.Application.Projects;
using ProjectManager.Application.Repositories;
using ProjectManager.Application.Templates;
using ProjectManager.Application.Wizard;
using ProjectManager.App.Theming;
using ProjectManager.CliInterop;
using ProjectManager.Scaffolding;
using ProjectManager.Scaffolding.Models;
using ProjectManager.Scaffolding.Scaffolding;
using ProjectManager.Scaffolding.Templates;
using ProjectManager.Storage;

namespace ProjectManager.App.Services;

/// <summary>
/// 应用级依赖容器（手工组装，不引 DI 框架：依赖数量固定且层级简单，
/// 引入容器只会增加启动开销与理解成本）。
/// </summary>
public sealed class ShellContext
{
    public required AppPaths Paths { get; init; }

    public required AppSettings Settings { get; init; }

    public required ISettingsStore SettingsStore { get; init; }

    /// <summary>
    /// 已加载的模板。加载失败时为空集合，主壳仍可用但"新建项目"禁用。
    /// </summary>
    /// <remarks>
    /// 可替换（而非 <c>init</c>）：设置页导入模板后要能在**不重启**的情况下生效。
    /// 向导每次打开都读 <see cref="Templates"/>，所以替换后新建的项目立即用新模板。
    /// </remarks>
    public IReadOnlyDictionary<string, Template> Templates { get; private set; } =
        new Dictionary<string, Template>();

    /// <summary>模板加载的降级说明（来源被跳过、配置损坏等），必须展示给用户。</summary>
    public string? TemplateWarning { get; private set; }

    /// <summary>模板来源展示名（设置页只读展示）。</summary>
    public string TemplateSourceText { get; private set; } = "未加载";

    /// <summary>模板库维护：检查 / 导入 / 停用用户配置。</summary>
    public required TemplateLibraryService Library { get; init; }

    public required ScaffoldGenerator Generator { get; init; }

    public required GenerationCoordinator Coordinator { get; init; }

    public required ProjectListModel Projects { get; init; }

    public required ThemeService Theme { get; init; }

    public required IProjectRepository Repository { get; init; }

    /// <summary>「打开已有项目」：把已存在的目录接入清单。</summary>
    public required ProjectImporter Importer { get; init; }

    /// <summary>组件识别（只读扫描，不落库）。</summary>
    public required ComponentScanner Components { get; init; }

    /// <summary>模板升级检测与"只加不删"补结构。</summary>
    public required TemplateUpgradeService Upgrades { get; init; }

    /// <summary>跨项目仓库清单（限流查 Git 状态）。</summary>
    public required RepositoryOverview Repositories { get; init; }

    /// <summary>Git CLI 封装：生成时 init，详情页查实时状态。</summary>
    public required IGitRunner Git { get; init; }

    public bool CanScaffold => Templates.Count > 0;

    /// <summary>
    /// 用一次成功加载的结果替换当前模板。加载器已按「用户 &gt; 安装目录 &gt; 内置」回退过，
    /// 这里只负责把结果落到上下文并刷新展示文案。
    /// </summary>
    public void ApplyTemplateLoad(TemplateLoadResult load)
    {
        Templates = load.Templates;
        TemplateSourceText = load.Source switch
        {
            TemplateSource.User => $"用户配置（{load.SourcePath}）",
            TemplateSource.Install => $"安装目录（{load.SourcePath}）",
            _ => "内置默认",
        };

        // 来源被跳过（用户配置坏了、回退到安装目录）也走这条路径，必须让用户看见
        TemplateWarning = load.Warnings.Count > 0 ? string.Join("；", load.Warnings) : null;
    }

    /// <summary>所有来源都不可用：模板置空（"新建项目"禁用），但主壳继续可用。</summary>
    public void MarkTemplateFailure(ScaffoldException error)
    {
        Templates = new Dictionary<string, Template>();
        TemplateSourceText = "未加载";
        TemplateWarning = $"[{error.ErrorCode}] {error.Message}";
    }

    /// <summary>把启动阶段的其它告警（用户目录不可写等）并入模板告警，避免两处提示。</summary>
    public void AppendTemplateWarning(string message)
    {
        TemplateWarning = string.IsNullOrEmpty(TemplateWarning)
            ? message
            : $"{TemplateWarning}；{message}";
    }

    /// <summary>
    /// 重新按三源优先级加载模板（设置页「重新加载」与导入成功后调用）。
    /// 不重启就能生效的前提是：向导每次打开都重新读 <see cref="Templates"/>，而不是构造时快照。
    /// </summary>
    public TemplateReloadResult ReloadTemplates()
    {
        var loader = TemplateLoader.CreateDefault(Paths.TemplatesFile, Paths.InstallTemplatesFile);
        try
        {
            // 刻意不调 TrySeedUserCopy：重载是"按磁盘现状重新读"，
            // 播种会把刚被「停用」的用户配置又复制回来，让回退变成假动作
            ApplyTemplateLoad(loader.Load());
            return new TemplateReloadResult(true, Templates.Count, null);
        }
        catch (ScaffoldException ex)
        {
            MarkTemplateFailure(ex);
            return new TemplateReloadResult(false, 0, $"[{ex.ErrorCode}] {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MarkTemplateFailure(new ScaffoldException(ScaffoldErrorCode.TemplateInvalid, $"模板配置读取失败：{ex.Message}", ex));
            return new TemplateReloadResult(false, 0, $"模板配置读取失败：{ex.Message}");
        }
    }

    /// <summary>保存设置；失败只记录，不打断用户。</summary>
    public async Task SaveSettingsAsync()
    {
        try
        {
            Settings.Theme = Theme.Current;
            await SettingsStore.SaveAsync(Settings).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 设置保存失败不影响使用，下次启动回到默认值即可
        }
    }
}

/// <summary>一次模板重载的结果（供设置页给出准确的"成了没成"反馈）。</summary>
public sealed record TemplateReloadResult(bool Succeeded, int TemplateCount, string? Failure);
