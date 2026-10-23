using System.IO;
using ProjectManager.Application.Components;
using ProjectManager.Application.Projects;
using ProjectManager.Application.Wizard;
using ProjectManager.App.Theming;
using ProjectManager.CliInterop;
using ProjectManager.Scaffolding.Models;
using ProjectManager.Scaffolding.Scaffolding;
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

    /// <summary>已加载的模板。加载失败时为空集合，主壳仍可用但"新建项目"禁用。</summary>
    public required IReadOnlyDictionary<string, Template> Templates { get; init; }

    /// <summary>模板加载的降级说明（来源被跳过、配置损坏等），必须展示给用户。</summary>
    public string? TemplateWarning { get; init; }

    /// <summary>模板来源展示名（设置页只读展示）。</summary>
    public string TemplateSourceText { get; init; } = string.Empty;

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

    /// <summary>Git CLI 封装：生成时 init，详情页查实时状态。</summary>
    public required IGitRunner Git { get; init; }

    public bool CanScaffold => Templates.Count > 0;

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
