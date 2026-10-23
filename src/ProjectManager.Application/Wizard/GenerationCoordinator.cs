using ProjectManager.CliInterop;
using ProjectManager.Scaffolding;
using ProjectManager.Scaffolding.Models;
using ProjectManager.Scaffolding.Scaffolding;
using ProjectManager.Storage;

namespace ProjectManager.Application.Wizard;

/// <summary>一次生成作业的输入。</summary>
public sealed record GenerationJob(
    GenerationRequest Request,
    Template Template,
    GenerationStrategy Strategy,
    bool InitGit);

/// <summary>
/// 生成编排：调用引擎 → 可选 <c>git init</c> → 注册到项目清单。
/// </summary>
/// <remarks>
/// 三条语义在此固化（评审 P0-3 / P0-4）：
/// <list type="number">
/// <item>取消只在<b>阶段间隙</b>生效，落盘阶段一旦开始就交给引擎的 journal 回滚；</item>
/// <item><c>git init</c> 失败<b>不影响</b>生成结果，仅作为提示带回；</item>
/// <item>文件生成成功但注册失败时，结果不是"失败"——而是"文件已生成 + 可重试注册"，
///       否则用户会重复生成并覆盖自己刚改的内容。</item>
/// </list>
/// </remarks>
public sealed class GenerationCoordinator
{
    private readonly ScaffoldGenerator _generator;
    private readonly IProjectRepository _projects;
    private readonly IGitRunner _git;
    private readonly TimeProvider _time;

    public GenerationCoordinator(
        ScaffoldGenerator generator,
        IProjectRepository projects,
        IGitRunner git,
        TimeProvider? time = null)
    {
        _generator = generator;
        _projects = projects;
        _git = git;
        _time = time ?? TimeProvider.System;
    }

    public async Task<GenerationOutcome> RunAsync(
        GenerationJob job,
        IProgress<GenerationStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        try
        {
            progress?.Report(GenerationStage.Preparing);
            cancellationToken.ThrowIfCancellationRequested();

            if (job.Strategy == GenerationStrategy.PreviewOnly)
            {
                var preview = _generator.Generate(
                    WithDryRun(job.Request, dryRun: true),
                    job.Template,
                    cancellationToken);
                progress?.Report(GenerationStage.Completed);
                return new GenerationOutcome
                {
                    Kind = GenerationOutcomeKind.PreviewOnly,
                    Result = preview,
                };
            }

            progress?.Report(GenerationStage.Generating);
            var result = _generator.Generate(job.Request, job.Template, cancellationToken);

            var gitSucceeded = false;
            string? gitMessage = null;
            string? gitError = null;
            if (job.InitGit)
            {
                progress?.Report(GenerationStage.InitializingGit);
                cancellationToken.ThrowIfCancellationRequested();

                var git = await _git.InitAsync(result.ProjectPath, cancellationToken).ConfigureAwait(false);
                gitSucceeded = git.Success;
                if (git.Success)
                {
                    gitMessage = "已初始化 Git 仓库";
                }
                else
                {
                    gitError = git.Message;
                }
            }

            progress?.Report(GenerationStage.Registering);
            var now = _time.GetLocalNow();
            var stored = new StoredProject
            {
                Id = Guid.NewGuid().ToString("n"),
                Name = job.Request.ProjectName,
                Path = result.ProjectPath,
                TemplateName = job.Template.Name,
                CreatedAt = now,
                LastOpenedAt = now,
                GitInitialized = gitSucceeded,
                Generated = true,
            };

            StoredProject? registered = null;
            string? registrationError = null;
            try
            {
                registered = await _projects.UpsertAsync(stored, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                registrationError = ex.Message;
            }

            progress?.Report(GenerationStage.Completed);
            return new GenerationOutcome
            {
                Kind = GenerationOutcomeKind.Success,
                Result = result,
                Project = registered,
                RegistrationFailed = registered is null,
                RegistrationError = registrationError,
                GitSucceeded = gitSucceeded,
                GitFailed = job.InitGit && !gitSucceeded,
                GitMessage = gitMessage ?? gitError,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new GenerationOutcome
            {
                Kind = GenerationOutcomeKind.Cancelled,
                ErrorCode = ScaffoldErrorCode.Cancelled.ToCode(),
                Message = "已取消生成。本次新建的空目录与占位文件已回滚。",
            };
        }
        catch (ScaffoldException ex) when (ex.Code == ScaffoldErrorCode.Cancelled)
        {
            return new GenerationOutcome
            {
                Kind = GenerationOutcomeKind.Cancelled,
                ErrorCode = ex.ErrorCode,
                Message = ex.Message,
            };
        }
        catch (ScaffoldException ex) when (ex.Code == ScaffoldErrorCode.RollbackFailed)
        {
            return new GenerationOutcome
            {
                Kind = GenerationOutcomeKind.RollbackFailed,
                ErrorCode = ex.ErrorCode,
                Message = ex.Message,
                Details = ex.Details,
                ManualCleanupList = ex.ManualCleanupList,
            };
        }
        catch (ScaffoldException ex)
        {
            return new GenerationOutcome
            {
                Kind = GenerationOutcomeKind.Failed,
                ErrorCode = ex.ErrorCode,
                Message = ex.Message,
                Details = ex.Details,
            };
        }
        catch (Exception ex)
        {
            return new GenerationOutcome
            {
                Kind = GenerationOutcomeKind.Failed,
                ErrorCode = ScaffoldErrorCode.GenerationFailed.ToCode(),
                Message = "生成过程中发生未预期错误，已完成部分已尝试回滚。",
                Details = ex.Message,
            };
        }
    }

    /// <summary>结果页「重试注册」：文件已存在，只补偿清单写入。</summary>
    public async Task<StoredProject?> RetryRegistrationAsync(
        string projectPath,
        string projectName,
        string templateName,
        bool gitInitialized,
        CancellationToken cancellationToken = default)
    {
        var now = _time.GetLocalNow();
        return await _projects.UpsertAsync(new StoredProject
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = projectName,
            Path = projectPath,
            TemplateName = templateName,
            CreatedAt = now,
            LastOpenedAt = now,
            GitInitialized = gitInitialized,
            Generated = true,
        }, cancellationToken).ConfigureAwait(false);
    }

    private static GenerationRequest WithDryRun(GenerationRequest request, bool dryRun) => new()
    {
        ProjectName = request.ProjectName,
        Description = request.Description,
        TargetDirectory = request.TargetDirectory,
        SelectedModules = request.SelectedModules,
        DryRun = dryRun,
    };
}
