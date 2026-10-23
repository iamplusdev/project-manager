using ProjectManager.Scaffolding.IO;
using ProjectManager.Scaffolding.Models;
using ProjectManager.Scaffolding.Validation;

namespace ProjectManager.Scaffolding.Scaffolding;

/// <summary>引擎选项。默认值即产品默认行为，测试通过它注入边界值。</summary>
public sealed record ScaffoldOptions
{
    /// <summary>路径长度上限（不含终止符）。Windows 默认 MAX_PATH=260，故 260 视为超限。</summary>
    public int MaxPathLength { get; init; } = 260;

    /// <summary>是否写 <c>.pm-scaffold.json</c>（G10）；等价性测试中关闭以便直接比对。</summary>
    public bool WriteMetadata { get; init; } = true;

    public string EngineVersion { get; init; } = "0.1.0";

    /// <summary><c>files</c> 注入的模板文件来源；为空时按模板配置所在目录解析。</summary>
    public ITemplateFileSource? FileSource { get; init; }
}

/// <summary>
/// 脚手架生成引擎（WP1.5–WP1.9）。零 UI / 零存储依赖，可在无界面环境完整单测（G7）。
/// </summary>
/// <remarks>
/// <para><b>五阶段</b>：预检 → 建目录 → 写文本产物 → 注入文件 → 占位与元数据。</para>
/// <para>
/// <b>取消语义（G8）</b>：<see cref="CancellationToken"/> 只在阶段间隙被检查；
/// 一旦抛错进入回滚，取消被彻底忽略，回滚必须跑完。
/// </para>
/// <para>
/// <b>回滚语义（G5）</b>：按 journal 逆序撤销，只删除本次新建且仍为空的目录；
/// 被覆盖的既有文件从 <c>.bak</c> 还原，用户既有的其他文件一律不动。
/// </para>
/// </remarks>
public sealed class ScaffoldGenerator
{
    private readonly IFileSystem _fs;
    private readonly IClock _clock;
    private readonly ScaffoldOptions _options;

    public ScaffoldGenerator(IFileSystem? fileSystem = null, IClock? clock = null, ScaffoldOptions? options = null)
    {
        _fs = fileSystem ?? PhysicalFileSystem.Instance;
        _clock = clock ?? SystemClock.Instance;
        _options = options ?? new ScaffoldOptions();
    }

    /// <summary>
    /// 生成前预览。与 <see cref="Generate"/> 共用同一套计算，保证"预览所见 = 生成所得"。
    /// 不做预检（预检抛错由 <see cref="Validate"/> 承担），因此可用于 Step 3 的实时预览。
    /// </summary>
    public ScaffoldPlan Preview(GenerationRequest request, Template template)
    {
        var tree = TreeBuilder.Build(template, request.SelectedModules);
        var projectPath = Path.GetFullPath(request.ResolveProjectPath());
        var plan = new ScaffoldPlan
        {
            ProjectPath = projectPath,
            TargetExisted = _fs.DirectoryExists(projectPath),
        };

        foreach (var directory in TreeBuilder.FlattenAbsolute(projectPath, tree))
        {
            if (!_fs.DirectoryExists(directory))
            {
                plan.DirectoriesToCreate.Add(Relative(projectPath, directory));
            }
        }

        ClassifyDocuments(request, template, projectPath, plan);
        ClassifyInjectedFiles(request, template, projectPath, plan);

        var ignored = TreeBuilder.IgnoredDirectoryPrefixes(template.Gitignore);
        foreach (var leaf in TreeBuilder.EmptyLeafDirectories(tree))
        {
            if (TreeBuilder.MatchesIgnoredDirectory(leaf, ignored))
            {
                continue;
            }

            var placeholder = Path.Combine(projectPath, leaf.Replace('/', Path.DirectorySeparatorChar), TreeBuilder.PlaceholderFileName);
            if (!_fs.FileExists(placeholder))
            {
                plan.PlaceholdersToCreate.Add(Relative(projectPath, placeholder));
            }
        }

        return plan;
    }

    /// <summary>预检（G4）。失败抛 <see cref="ScaffoldException"/>，UI 按错误码分支。</summary>
    public void Validate(GenerationRequest request, Template template)
    {
        var tree = TreeBuilder.Build(template, request.SelectedModules);
        var projectPath = Path.GetFullPath(request.ResolveProjectPath());
        Preflight(request, template, tree, projectPath);
    }

    public GenerationResult Generate(
        GenerationRequest request,
        Template template,
        CancellationToken cancellationToken = default)
    {
        var tree = TreeBuilder.Build(template, request.SelectedModules);
        var projectPath = Path.GetFullPath(request.ResolveProjectPath());
        Preflight(request, template, tree, projectPath);

        if (request.DryRun)
        {
            return BuildDryRunResult(request, template, projectPath);
        }

        var result = new GenerationResult
        {
            ProjectPath = projectPath,
            TargetExisted = _fs.DirectoryExists(projectPath),
        };

        var journal = new ScaffoldJournal(_fs);
        var now = _clock.LocalNow;

        try
        {
            // 阶段 1：建目录（父在前，保证顺序撤销时空目录可删）
            cancellationToken.ThrowIfCancellationRequested();
            if (!_fs.DirectoryExists(projectPath))
            {
                _fs.CreateDirectory(projectPath);
                journal.RecordDirectory(projectPath);
                result.DirectoriesCreated.Add(".");
            }

            foreach (var directory in TreeBuilder.FlattenAbsolute(projectPath, tree))
            {
                if (_fs.DirectoryExists(directory))
                {
                    continue;
                }

                _fs.CreateDirectory(directory);
                journal.RecordDirectory(directory);
                result.DirectoriesCreated.Add(Relative(projectPath, directory));
            }

            // 阶段 2：文本产物
            cancellationToken.ThrowIfCancellationRequested();
            WriteDocument(
                Path.Combine(projectPath, Documents.ReadmeFileName),
                Documents.Readme(template, request.ProjectName, request.Description),
                projectPath,
                result,
                journal);

            WriteDocument(
                Path.Combine(projectPath, Documents.StructureFileName),
                Documents.Structure(template, request.ProjectName, tree),
                projectPath,
                result,
                journal);

            WriteGitignoreIfAbsent(template, projectPath, result, journal);

            // 阶段 3：files 注入
            cancellationToken.ThrowIfCancellationRequested();
            InjectFiles(request, template, projectPath, now, result, journal);

            // 阶段 4：空目录占位
            cancellationToken.ThrowIfCancellationRequested();
            WritePlaceholders(tree, template, projectPath, result, journal);

            // 阶段 5：元数据
            if (_options.WriteMetadata)
            {
                var metadataPath = Path.Combine(projectPath, ScaffoldMetadata.FileName);
                _fs.WriteAllText(metadataPath, ScaffoldMetadata.Build(request, template, now, _options.EngineVersion));
                journal.RecordFile(metadataPath, null);
                result.FilesWritten.Add(Relative(projectPath, metadataPath));
            }
        }
        catch (OperationCanceledException)
        {
            var report = Rollback(journal);
            throw new ScaffoldException(
                ScaffoldErrorCode.Cancelled,
                $"已取消生成。{DescribeRollback(report)}")
            {
                Details = report.Failures.Count == 0 ? string.Empty : string.Join(" | ", report.Failures),
            };
        }
        catch (ScaffoldException)
        {
            // 引擎自身的语义化异常（如注入路径非法）：同样需要回滚，但不改写错误码
            var report = Rollback(journal);
            throw new ScaffoldException(
                ScaffoldErrorCode.GenerationFailed,
                $"生成失败，已撤销本次改动。{DescribeRollback(report)}")
            {
                Details = report.Failures.Count == 0 ? string.Empty : string.Join(" | ", report.Failures),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            var report = Rollback(journal);

            if (!report.Succeeded)
            {
                throw new ScaffoldException(
                    ScaffoldErrorCode.RollbackFailed,
                    $"生成失败且回滚未完全成功：{ex.Message}\n{DescribeRollback(report)}",
                    ex)
                {
                    ManualCleanupList = report.Failures.ToArray(),
                };
            }

            throw new ScaffoldException(
                ScaffoldErrorCode.GenerationFailed,
                $"生成失败：{ex.Message}\n{DescribeRollback(report)}已有目录和你手工放置的文件都不会被删除，可修正问题后重新生成。",
                ex);
        }

        return result;
    }

    /// <summary>G8：回滚不接受取消令牌，调用方无法中断它。</summary>
    private RollbackReport Rollback(ScaffoldJournal journal) => journal.Rollback();

    private static string DescribeRollback(RollbackReport report)
    {
        var text = report.RemovedDirectories > 0 || report.RemovedFiles > 0
            ? $"已回滚：删除 {report.RemovedDirectories} 个新建目录、{report.RemovedFiles} 个新建文件"
            : "本次没有需要回滚的新建目录";

        if (report.RestoredFiles > 0)
        {
            text += $"，并从备份还原 {report.RestoredFiles} 个文件";
        }

        return report.Succeeded
            ? text + "。"
            : text + $"；{report.Failures.Count} 项未能撤销，见「本次新增清单」。";
    }

    private void Preflight(GenerationRequest request, Template template, DirNode tree, string projectPath)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectName))
        {
            throw new ScaffoldException(ScaffoldErrorCode.InvalidRequest, "项目名称不能为空。")
            {
                Details = "projectName",
            };
        }

        var parent = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrEmpty(parent) || !_fs.DirectoryExists(parent))
        {
            throw new ScaffoldException(
                ScaffoldErrorCode.PathNotFound,
                $"生成路径不存在或不是文件夹：{(string.IsNullOrEmpty(parent) ? projectPath : parent)}")
            {
                Details = parent ?? projectPath,
            };
        }

        if (!_fs.IsWritable(parent))
        {
            throw new ScaffoldException(ScaffoldErrorCode.PathNotWritable, $"生成路径不可写：{parent}")
            {
                Details = parent,
            };
        }

        // 4.6 路径形态边界：拒绝在 .git 内生成，拒绝把符号链接/junction 当作目标（避免越界写入）
        if (string.Equals(Path.GetFileName(parent), ".git", StringComparison.OrdinalIgnoreCase))
        {
            throw new ScaffoldException(
                ScaffoldErrorCode.SymlinkRejected,
                "不能在 .git 目录内生成项目，请选择普通目录。")
            {
                Details = parent,
            };
        }

        if (_fs.DirectoryExists(projectPath) && _fs.IsSymbolicLink(projectPath))
        {
            throw new ScaffoldException(
                ScaffoldErrorCode.SymlinkRejected,
                $"目标目录是符号链接或 junction，拒绝写入以避免越界：{projectPath}")
            {
                Details = projectPath,
            };
        }

        var (plannedDirectories, plannedFiles) = PlannedAbsolutePaths(request, template, tree, projectPath);

        // 目录位置被同名文件占用 → 无法建目录
        foreach (var path in plannedDirectories)
        {
            if (_fs.FileExists(path))
            {
                throw new ScaffoldException(
                    ScaffoldErrorCode.PathOccupiedByFile,
                    $"以下位置被同名文件占用，无法创建目录：{Relative(projectPath, path)}")
                {
                    Details = Relative(projectPath, path),
                };
            }
        }

        // 文件位置被同名目录占用 → 无法写文件（已存在的同名*文件*不在此列：走 .bak 备份，见 G1）
        foreach (var path in plannedFiles)
        {
            if (_fs.DirectoryExists(path))
            {
                throw new ScaffoldException(
                    ScaffoldErrorCode.PathOccupiedByFile,
                    $"以下位置是已存在的目录，无法写入同名文件：{Relative(projectPath, path)}")
                {
                    Details = Relative(projectPath, path),
                };
            }
        }

        var longest = 0;
        string? longestPath = null;
        foreach (var path in plannedDirectories.Concat(plannedFiles))
        {
            if (path.Length > longest)
            {
                longest = path.Length;
                longestPath = path;
            }
        }

        if (longest >= _options.MaxPathLength)
        {
            throw new ScaffoldException(
                ScaffoldErrorCode.PathTooLong,
                $"生成路径过长（{longest} 字符，上限 {_options.MaxPathLength - 1}）。请缩短项目名或选择更浅的目录。")
            {
                Details = longestPath ?? string.Empty,
            };
        }
    }

    /// <summary>所有会被创建的位置，按「目录」与「文件」分开返回：二者的冲突判定方向相反。</summary>
    private (IReadOnlyList<string> Directories, IReadOnlyList<string> Files) PlannedAbsolutePaths(
        GenerationRequest request,
        Template template,
        DirNode tree,
        string projectPath)
    {
        var directories = new List<string> { projectPath };
        directories.AddRange(TreeBuilder.FlattenAbsolute(projectPath, tree));

        var files = new List<string>
        {
            Path.Combine(projectPath, Documents.ReadmeFileName),
            Path.Combine(projectPath, Documents.StructureFileName),
            Path.Combine(projectPath, Documents.GitignoreFileName),
        };

        if (_options.WriteMetadata)
        {
            files.Add(Path.Combine(projectPath, ScaffoldMetadata.FileName));
        }

        foreach (var key in ResolveInjectedTargets(request, template).Keys)
        {
            files.Add(Path.Combine(projectPath, key.Replace('/', Path.DirectorySeparatorChar)));
        }

        var ignored = TreeBuilder.IgnoredDirectoryPrefixes(template.Gitignore);
        foreach (var leaf in TreeBuilder.EmptyLeafDirectories(tree))
        {
            if (!TreeBuilder.MatchesIgnoredDirectory(leaf, ignored))
            {
                files.Add(Path.Combine(
                    projectPath,
                    leaf.Replace('/', Path.DirectorySeparatorChar),
                    TreeBuilder.PlaceholderFileName));
            }
        }

        return (directories, files);
    }

    private void ClassifyDocuments(GenerationRequest request, Template template, string projectPath, ScaffoldPlan plan)
    {
        foreach (var name in new[] { Documents.ReadmeFileName, Documents.StructureFileName })
        {
            var path = Path.Combine(projectPath, name);
            plan.TextFilesToWrite.Add(name);
            if (_fs.FileExists(path))
            {
                plan.FilesToBackup.Add(name);
            }
        }

        var gitignorePath = Path.Combine(projectPath, Documents.GitignoreFileName);
        if (_fs.FileExists(gitignorePath))
        {
            plan.FilesToPreserve.Add(Documents.GitignoreFileName);
        }
        else
        {
            // 与 Forge 一致：即使模板没有忽略规则也落一个空 .gitignore，不省略文件
            plan.TextFilesToWrite.Add(Documents.GitignoreFileName);
        }
    }

    private void ClassifyInjectedFiles(GenerationRequest request, Template template, string projectPath, ScaffoldPlan plan)
    {
        var targets = ResolveInjectedTargets(request, template);
        foreach (var target in targets.Keys)
        {
            if (_fs.FileExists(Path.Combine(projectPath, target.Replace('/', Path.DirectorySeparatorChar))))
            {
                plan.FilesToPreserve.Add(target);
            }
            else
            {
                plan.FilesToInject.Add(target);
            }
        }
    }

    private void WriteDocument(
        string path,
        string content,
        string projectPath,
        GenerationResult result,
        ScaffoldJournal journal)
    {
        string? backup = null;
        if (_fs.FileExists(path))
        {
            backup = BuildBackupPath(path);
            _fs.WriteAllText(backup, _fs.ReadAllText(path));
            journal.RecordFile(backup, null);
            result.Backups.Add(Relative(projectPath, backup));
        }

        _fs.WriteAllText(path, content);
        journal.RecordFile(path, backup);
        result.FilesWritten.Add(Relative(projectPath, path));
    }

    private void WriteGitignoreIfAbsent(
        Template template,
        string projectPath,
        GenerationResult result,
        ScaffoldJournal journal)
    {
        var path = Path.Combine(projectPath, Documents.GitignoreFileName);
        if (_fs.FileExists(path))
        {
            // G2：用户既有 .gitignore 一律保留，不备份不覆盖
            journal.RecordPreserved(path);
            result.Preserved.Add(Documents.GitignoreFileName);
            return;
        }

        var content = Documents.Gitignore(template);

        // 模板无规则时也落一个空文件：与 Forge 行为一致（省略文件会让"等价"多出第二条偏差）
        _fs.WriteAllText(path, content);
        journal.RecordFile(path, null);
        result.FilesWritten.Add(Relative(projectPath, path));
    }

    private void InjectFiles(
        GenerationRequest request,
        Template template,
        string projectPath,
        DateTimeOffset now,
        GenerationResult result,
        ScaffoldJournal journal)
    {
        if (template.Files.Count == 0)
        {
            return;
        }

        var source = _options.FileSource ?? DirectoryTemplateFileSource.ForTemplate(template);
        var values = Placeholders.Values.From(request, template, now);
        var targets = ResolveInjectedTargets(request, template);

        foreach (var (target, templateRelative) in targets)
        {
            var destination = Path.Combine(projectPath, target.Replace('/', Path.DirectorySeparatorChar));

            // G9：目标已存在 → 跳过，绝不覆盖用户文件
            if (_fs.FileExists(destination))
            {
                journal.RecordPreserved(destination);
                result.SkippedExisting.Add(target);
                continue;
            }

            var raw = source.TryReadText(templateRelative)
                ?? throw new ScaffoldException(
                    ScaffoldErrorCode.TemplateInvalid,
                    $"模板「{template.Name}」的 files 引用了不存在的文件：{templateRelative}")
                {
                    Details = $"{template.Name}.files[{target}]",
                };

            var parentDirectory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parentDirectory) && !_fs.DirectoryExists(parentDirectory))
            {
                _fs.CreateDirectory(parentDirectory);
                journal.RecordDirectory(parentDirectory);
                result.DirectoriesCreated.Add(Relative(projectPath, parentDirectory));
            }

            _fs.WriteAllText(destination, Placeholders.Apply(raw, values));
            journal.RecordFile(destination, null);
            result.InjectedFiles.Add(target);
            result.FilesWritten.Add(target);
        }
    }

    /// <summary>解析 <c>files</c> 的落盘路径（含键上的占位符替换并校验合法性）。</summary>
    private IReadOnlyDictionary<string, string> ResolveInjectedTargets(GenerationRequest request, Template template)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (template.Files.Count == 0)
        {
            return result;
        }

        var values = Placeholders.Values.From(request, template, _clock.LocalNow);
        foreach (var (key, templateRelative) in template.Files)
        {
            var resolved = Placeholders.Apply(key, values).Replace('\\', '/');
            if (resolved.Contains("{{", StringComparison.Ordinal))
            {
                throw new ScaffoldException(
                    ScaffoldErrorCode.TemplateInvalid,
                    $"模板「{template.Name}」的 files 键包含未识别的占位符：{key}")
                {
                    Details = $"{template.Name}.files[{key}]",
                };
            }

            foreach (var segment in resolved.Split('/'))
            {
                if (segment.Length == 0 || segment is "." or "..")
                {
                    throw new ScaffoldException(
                        ScaffoldErrorCode.TemplateInvalid,
                        $"模板「{template.Name}」的 files 键生成了非法路径：{resolved}")
                    {
                        Details = $"{template.Name}.files[{key}]",
                    };
                }
            }

            result[resolved] = templateRelative;
        }

        return result;
    }

    private void WritePlaceholders(
        DirNode tree,
        Template template,
        string projectPath,
        GenerationResult result,
        ScaffoldJournal journal)
    {
        var ignored = TreeBuilder.IgnoredDirectoryPrefixes(template.Gitignore);

        foreach (var leaf in TreeBuilder.EmptyLeafDirectories(tree))
        {
            if (TreeBuilder.MatchesIgnoredDirectory(leaf, ignored))
            {
                continue;
            }

            var path = Path.Combine(
                projectPath,
                leaf.Replace('/', Path.DirectorySeparatorChar),
                TreeBuilder.PlaceholderFileName);

            // G3：已存在则跳过，不重写（用户可能在其中写过说明）
            if (_fs.FileExists(path))
            {
                continue;
            }

            _fs.WriteAllText(path, string.Empty);
            journal.RecordFile(path, null);
            result.Placeholders.Add(Relative(projectPath, path));
        }
    }

    private string BuildBackupPath(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileName(path);
        var stamp = _clock.LocalNow.ToString("yyyyMMdd-HHmmss");

        var candidate = Path.Combine(directory, $"{name}.{stamp}.bak");
        var index = 0;
        while (_fs.FileExists(candidate))
        {
            index++;
            candidate = Path.Combine(directory, $"{name}.{stamp}.bak-{index}");
        }

        return candidate;
    }

    private GenerationResult BuildDryRunResult(GenerationRequest request, Template template, string projectPath)
    {
        var plan = Preview(request, template);
        var result = new GenerationResult
        {
            ProjectPath = projectPath,
            TargetExisted = plan.TargetExisted,
            DryRun = true,
        };

        result.DirectoriesCreated.AddRange(plan.DirectoriesToCreate);
        result.FilesWritten.AddRange(plan.TextFilesToWrite);
        result.FilesWritten.AddRange(plan.FilesToInject);
        result.Backups.AddRange(plan.FilesToBackup);
        result.Preserved.AddRange(plan.FilesToPreserve);
        result.Placeholders.AddRange(plan.PlaceholdersToCreate);
        result.InjectedFiles.AddRange(plan.FilesToInject);
        return result;
    }

    /// <summary>绝对路径 → 相对项目根的 <c>/</c> 路径（对外结果一律用相对路径，不泄露绝对路径）。</summary>
    private static string Relative(string projectPath, string absolutePath)
    {
        var prefix = projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (absolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            absolutePath = absolutePath[prefix.Length..];
        }

        return absolutePath.Replace('\\', '/').TrimStart('/');
    }
}
