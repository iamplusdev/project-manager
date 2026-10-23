using ProjectManager.Application.Wizard;
using ProjectManager.Scaffolding.IO;
using ProjectManager.Scaffolding.Models;
using ProjectManager.Scaffolding.Scaffolding;
using ProjectManager.Scaffolding.Templates;
using ProjectManager.Storage;

namespace ProjectManager.Application.Tests;

/// <summary>
/// 向导状态机与生成编排。重点验证三件事：
/// 步骤导航受校验约束、预览与模块联动、"生成即注册"及注册失败降级。
/// </summary>
public static class WizardTests
{
    public static void Run()
    {
        Check.Section("Wizard · 初始状态与校验");
        InitialState();
        InvalidNameBlocksNext();
        InvalidPathBlocksNext();

        Check.Section("Wizard · 导航与预览");
        NavigationToPreview();
        DerivedNamesFollowTemplate();
        ModuleSelectionChangesPreview();
        TargetExistedIsReported();

        Check.Section("Wizard · 生成与注册");
        HappyPathGeneratesAndRegisters();
        PreviewOnlyWritesNothing();
        RegistrationFailureDegrades();
        GitFailureDoesNotFailGeneration();
    }

    private static void InitialState()
    {
        using var workspace = new TempWorkspace("wizard-initial");
        var wizard = NewWizard(workspace, out _, out _);

        Check.Equal(WizardStep.Basic, wizard.Step, "初始应在第 1 步");
        Check.Equal("单体桌面工具", wizard.SelectedTemplate?.Key, "默认应选中「单体桌面工具」");
        Check.Ok(wizard.SelectedTemplate?.IsRecommended == true, "该模板应带推荐标记");
        Check.Equal(3, wizard.Modules.Count, "该模板应有 3 个模块");
        Check.Ok(wizard.Modules.All(module => module.IsSelected), "模块默认全选（与 Forge 一致）");
        Check.Ok(!wizard.CanGoNext, "未填名称时不应能进入下一步");
        Check.Ok(!wizard.CanGoBack, "第 1 步不应能后退");
        Check.Ok(!wizard.HasAnyInput, "初始不应视为已有输入");
    }

    private static void InvalidNameBlocksNext()
    {
        using var workspace = new TempWorkspace("wizard-badname");
        var wizard = NewWizard(workspace, out _, out _);
        wizard.TargetDirectory = workspace.NewTarget();
        wizard.Description = "说明";

        wizard.ProjectName = "My/App";
        Check.NotNull(wizard.NameError, "含 / 的名称应报错");
        Check.Ok(!wizard.Next(), "校验失败时 Next 应返回 false");
        Check.Equal(WizardStep.Basic, wizard.Step, "校验失败应留在第 1 步");

        wizard.ProjectName = "CON";
        Check.NotNull(wizard.NameError, "Windows 保留名应报错");

        wizard.ProjectName = new string('a', 51);
        Check.NotNull(wizard.NameError, "超过 50 码点应报错");

        wizard.ProjectName = "MyApp";
        Check.Ok(wizard.NameError is null, "合法名称不应报错");
    }

    private static void InvalidPathBlocksNext()
    {
        using var workspace = new TempWorkspace("wizard-badpath");
        var wizard = NewWizard(workspace, out _, out _);
        wizard.ProjectName = "MyApp";
        wizard.Description = "说明";
        wizard.TargetDirectory = workspace.Path2("does-not-exist");

        Check.NotNull(wizard.PathError, "不存在的路径应报错");
        Check.Ok(!wizard.Next(), "路径无效时 Next 应返回 false");
    }

    private static void NavigationToPreview()
    {
        using var workspace = new TempWorkspace("wizard-nav");
        var wizard = NewWizard(workspace, out _, out _);
        Fill(wizard, workspace, "NavApp");

        Check.Ok(wizard.CanGoNext, "填完必填项后应可前进");
        Check.Ok(wizard.Next(), "应进入第 2 步");
        Check.Equal(WizardStep.Template, wizard.Step, "应在第 2 步");
        Check.Ok(wizard.CanGoBack, "第 2 步应可后退");

        Check.Ok(wizard.Next(), "应进入第 3 步");
        Check.Equal(WizardStep.Preview, wizard.Step, "应在预览步");
        Check.Ok(wizard.Summary.DirectoriesToCreate > 0, "预览应列出待创建目录");
        Check.Ok(wizard.PreviewRoots.Count > 0, "预览树应有节点");
        Check.Ok(!wizard.CanGoNext, "预览步的「下一步」应隐藏（改用生成）");

        Check.Ok(wizard.Back(), "预览步应可后退");
        Check.Equal(WizardStep.Template, wizard.Step, "后退应回到第 2 步");
        Check.Equal("NavApp", wizard.ProjectName, "后退不应丢失已填内容");
    }

    private static void DerivedNamesFollowTemplate()
    {
        using var workspace = new TempWorkspace("wizard-derived");
        var wizard = NewWizard(workspace, out _, out _);

        wizard.ProjectName = "my project";
        Check.Equal("MyProject", wizard.Derived?.NamespaceText, "命名空间应为 PascalCase");
        Check.Equal("my-project", wizard.Derived?.PackageText, "包名应为 kebab-case");
        Check.Equal("MyProject", wizard.Derived?.DirectoryText, "目录名参考应为 PascalCase（仅展示）");

        wizard.ProjectName = string.Empty;
        Check.Ok(wizard.Derived is null, "名称为空时不应有派生结果");
    }

    private static void ModuleSelectionChangesPreview()
    {
        using var workspace = new TempWorkspace("wizard-modules");
        var wizard = NewWizard(workspace, out _, out _);
        Fill(wizard, workspace, "ModuleApp");
        wizard.Next();
        wizard.Next();

        var full = wizard.Summary.DirectoriesToCreate;
        Check.Ok(full > 0, "全选时应列出目录");

        wizard.ClearAllModules();
        var minimal = wizard.Summary.DirectoriesToCreate;
        Check.Ok(minimal < full, $"清空模块后目录数应减少（全选 {full} → 清空 {minimal}）");

        wizard.SelectAllModules();
        Check.Equal(full, wizard.Summary.DirectoriesToCreate, "重新全选应恢复原目录数");
    }

    private static void TargetExistedIsReported()
    {
        using var workspace = new TempWorkspace("wizard-exists");
        var wizard = NewWizard(workspace, out _, out _);
        var target = workspace.NewTarget();
        Directory.CreateDirectory(Path.Combine(target, "ExistApp"));

        Fill(wizard, workspace, "ExistApp", target);
        wizard.Next();
        wizard.Next();

        Check.Ok(wizard.HasTargetExisted, "目标目录已存在时应给出提示");
        Check.Ok(wizard.Next() || wizard.Step == WizardStep.Preview, "目标已存在不应阻断进入预览");
    }

    private static void HappyPathGeneratesAndRegisters()
    {
        using var workspace = new TempWorkspace("wizard-happy");
        var wizard = NewWizard(workspace, out var repository, out var git);
        var target = workspace.NewTarget();
        Fill(wizard, workspace, "HappyApp", target);

        wizard.Next();
        wizard.ClearAllModules();
        wizard.Next();

        var outcome = wizard.GenerateAsync().GetAwaiter().GetResult();
        var projectPath = Path.Combine(target, "HappyApp");

        Check.Equal(GenerationOutcomeKind.Success, outcome.Kind, $"应生成成功（{outcome.Message}）");
        Check.Ok(Directory.Exists(projectPath), "项目目录应已创建");
        Check.Ok(File.Exists(Path.Combine(projectPath, "README.md")), "README 应已写入");
        Check.Equal(WizardStep.Result, wizard.Step, "生成后应进入结果页");

        var loaded = repository.LoadAsync().GetAwaiter().GetResult();
        Check.Equal(1, loaded.Projects.Count, "生成后应立即出现在项目清单中");
        Check.Equal("HappyApp", loaded.Projects[0].Name, "清单中的名称应正确");
        Check.Equal("单体桌面工具", loaded.Projects[0].TemplateName, "清单应记录模板名");
        Check.Ok(git.InitCalls == 1, "勾选 InitGit 时应调用一次 git init");
        Check.Ok(loaded.Projects[0].GitInitialized, "git init 成功应记录在清单中");
        Check.Ok(!outcome.RegistrationFailed, "正常路径不应标记注册失败");
        Check.Ok(!wizard.CanRetryRegistration, "正常路径不应提供重试注册");
    }

    private static void PreviewOnlyWritesNothing()
    {
        using var workspace = new TempWorkspace("wizard-preview-only");
        var wizard = NewWizard(workspace, out var repository, out var git);
        var target = workspace.NewTarget();
        Fill(wizard, workspace, "PreviewApp", target);

        wizard.Next();
        wizard.Next();
        wizard.IsPreviewOnlyStrategy = true;

        var outcome = wizard.GenerateAsync().GetAwaiter().GetResult();

        Check.Equal(GenerationOutcomeKind.PreviewOnly, outcome.Kind, "应返回仅预览结果");
        Check.Ok(!Directory.Exists(Path.Combine(target, "PreviewApp")), "仅预览不应创建项目目录");
        Check.Equal(0, repository.LoadAsync().GetAwaiter().GetResult().Projects.Count, "仅预览不应注册项目");
        Check.Equal(0, git.InitCalls, "仅预览不应执行 git init");
    }

    private static void RegistrationFailureDegrades()
    {
        using var workspace = new TempWorkspace("wizard-register-fail");
        var generator = new ScaffoldGenerator();
        var coordinator = new GenerationCoordinator(generator, new ThrowingRepository(), new FakeGitRunner());
        var wizard = new NewProjectWizard(
            LoadTemplates().Values,
            generator,
            coordinator,
            new AppSettings(),
            new PhysicalFileSystem());

        var target = workspace.NewTarget();
        Fill(wizard, workspace, "RegFailApp", target);
        wizard.Next();
        wizard.ClearAllModules();
        wizard.Next();

        var outcome = wizard.GenerateAsync().GetAwaiter().GetResult();

        Check.Equal(GenerationOutcomeKind.Success, outcome.Kind, "注册失败不应把结果判为失败（文件已生成）");
        Check.Ok(outcome.RegistrationFailed, "应标记注册失败");
        Check.NotNull(outcome.RegistrationError, "应带出注册失败原因");
        Check.Ok(Directory.Exists(Path.Combine(target, "RegFailApp")), "文件仍应存在");
        Check.Ok(wizard.CanRetryRegistration, "应提供重试注册入口");
        Check.Equal("文件已生成，但未能加入项目清单", outcome.Headline, "结论文案应区分于普通成功/失败");
        Check.NotNull(wizard.OutcomeWarning, "结果页应显示降级提示");
    }

    private static void GitFailureDoesNotFailGeneration()
    {
        using var workspace = new TempWorkspace("wizard-git-fail");
        var generator = new ScaffoldGenerator();
        var repository = new JsonProjectRepository(workspace.Path2("projects.json"));
        var coordinator = new GenerationCoordinator(generator, repository, new FakeGitRunner { Success = false });
        var wizard = new NewProjectWizard(
            LoadTemplates().Values,
            generator,
            coordinator,
            new AppSettings(),
            new PhysicalFileSystem());

        var target = workspace.NewTarget();
        Fill(wizard, workspace, "GitFailApp", target);
        wizard.Next();
        wizard.ClearAllModules();
        wizard.Next();

        var outcome = wizard.GenerateAsync().GetAwaiter().GetResult();

        Check.Equal(GenerationOutcomeKind.Success, outcome.Kind, "git 失败不应影响生成结果");
        Check.Ok(outcome.GitFailed, "应标记 git 未初始化");
        Check.Equal("生成成功（Git 仓库未初始化）", outcome.Headline, "结论文案应说明 git 未初始化");
        Check.Equal(1, repository.LoadAsync().GetAwaiter().GetResult().Projects.Count, "项目仍应注册");
    }

    private static NewProjectWizard NewWizard(
        TempWorkspace workspace,
        out JsonProjectRepository repository,
        out FakeGitRunner git)
    {
        var generator = new ScaffoldGenerator();
        repository = new JsonProjectRepository(workspace.Path2("projects.json"));
        git = new FakeGitRunner();
        var coordinator = new GenerationCoordinator(generator, repository, git);
        return new NewProjectWizard(
            LoadTemplates().Values,
            generator,
            coordinator,
            new AppSettings(),
            new PhysicalFileSystem());
    }

    private static void Fill(NewProjectWizard wizard, TempWorkspace workspace, string name, string? target = null)
    {
        wizard.ProjectName = name;
        wizard.Description = "测试项目简介";
        wizard.TargetDirectory = target ?? workspace.NewTarget("target-" + name);
    }

    private static IReadOnlyDictionary<string, Template> LoadTemplates()
    {
        var loader = new TemplateLoader(userPath: string.Empty, installPath: string.Empty);
        var bytes = File.ReadAllBytes(RepoFiles.BuiltinTemplatesFile);
        return loader.LoadFromBytes(bytes, TemplateSource.Builtin, "templates.default.json").Templates;
    }
}
