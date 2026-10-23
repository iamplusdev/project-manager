using System.Text;
using ProjectManager.Application.Git;
using ProjectManager.CliInterop;
using static ProjectManager.Application.Tests.GitTestSupport;

namespace ProjectManager.Application.Tests;

/// <summary>
/// Git 工作台（M5 · WP5.1 / WP5.2）：解析器纯函数 + 真 git 走一遍。
/// 无 git 环境时真机部分自动跳过，纯函数部分照跑。
/// </summary>
public static class GitWorkspaceTests
{
    public static void Run()
    {
        Check.Section("Git · 变更清单解析（porcelain v2）");
        ParsesOrdinaryEntries();
        ParsesBothSides();
        ParsesRename();
        ParsesUntrackedAndDirectory();
        ParsesUnmerged();
        StatusTexts();

        Check.Section("Git · 差异解析（unified diff）");
        ParsesSingleFileHunk();
        ParsesNewAndDeletedFiles();
        ParsesMultipleFiles();
        ParsesHunkWithoutCount();
        ParsesNoNewlineNote();
        ParsesBinary();

        Check.Section("Git · 工作台编排");
        OrdersChangesForDisplay();
        SuggestsCommitTitle();
        RejectsEmptyTitle();

        Check.Section("Git · 真仓库链路");
        WorkingTreeRoundTrip();
        UnstageArgumentsPerHead();
        StagesAndUnstagesWithoutHead();
        CommitsAndReadsBack();
        HookRejectionSurfaces();
        RenamesInRealRepository();
    }

    // ── porcelain v2 ─────────────────────────────────────────────────────

    private static void ParsesOrdinaryEntries()
    {
        // 1 XY sub mH mI mW hH hI path
        var output = "1 .M N... 100644 100644 100644 aaa bbb src/App.cs\0"
                     + "1 M. N... 100644 100644 100644 aaa bbb src/New.cs\0";

        var entries = GitStatusParser.ParsePorcelainV2(output);

        Check.Equal(2, entries.Count, "两条普通记录应解析为两个条目");
        Check.Equal("src/App.cs", entries[0].Path, "路径应原样保留（-z 不转义）");
        Check.Equal(GitChangeKind.Unmodified, entries[0].Staged, "X=. 表示索引区无变化");
        Check.Equal(GitChangeKind.Modified, entries[0].Unstaged, "Y=M 表示工作区已修改");
        Check.That(!entries[0].HasStagedChanges, "只有工作区改动时不算已暂存");

        Check.Equal(GitChangeKind.Modified, entries[1].Staged, "X=M 表示索引区已暂存修改");
        Check.That(entries[1].HasStagedChanges, "索引区有内容即已暂存");
        Check.That(!entries[1].HasUnstagedChanges, "Y=. 表示工作区没有再改");
    }

    private static void ParsesBothSides()
    {
        // add 之后又改了一笔：两侧都有变化，必须能如实显示
        var entry = GitStatusParser.ParsePorcelainV2("1 MM N... 100644 100644 100644 aaa bbb a.cs\0")[0];

        Check.That(entry.HasStagedChanges, "索引区有内容");
        Check.That(entry.HasUnstagedChanges, "工作区也有内容");
        Check.Equal("已暂存修改 · 又修改", entry.StatusText, "两侧都有时文案要说明两件事");
    }

    private static void ParsesRename()
    {
        // 2 XY sub mH mI mW hH hI Xscore path \0 origPath
        var output = "2 R. N... 100644 100644 100644 aaa bbb R100 new name.cs\0old name.cs\0"
                     + "? later.md\0";

        var entries = GitStatusParser.ParsePorcelainV2(output);

        Check.Equal(2, entries.Count, "重命名记录后的未跟踪记录不应被源路径吞掉");
        Check.Equal("new name.cs", entries[0].Path, "重命名的新路径");
        Check.Equal("old name.cs", entries[0].OriginalPath, "源路径来自独立字段，含空格也不歧义");
        Check.Equal("old name.cs → new name.cs", entries[0].DisplayPath, "展示为 旧 → 新");
        Check.Equal("later.md", entries[1].Path, "下一条记录要正确解析");
    }

    private static void ParsesUntrackedAndDirectory()
    {
        var entries = GitStatusParser.ParsePorcelainV2("? notes.md\0? docs/\0");

        Check.Equal(2, entries.Count, "两条未跟踪记录");
        Check.Equal(GitChangeKind.Untracked, entries[0].Staged, "未跟踪在索引区记作 Untracked");
        Check.Equal("未跟踪", entries[0].StatusText, "未跟踪文件文案");
        Check.That(!entries[1].IsConflicted && entries[1].IsDirectory, "尾斜杠应识别为目录");
        Check.Equal("未跟踪目录", entries[1].StatusText, "目录文案要区分");
    }

    private static void ParsesUnmerged()
    {
        // u XY sub m1 m2 m3 mW h1 h2 h3 path
        var entry = GitStatusParser.ParsePorcelainV2(
            "u UU N... 100644 100644 100644 100644 aaa bbb ccc conflict.cs\0")[0];

        Check.That(entry.IsConflicted, "u 记录即冲突");
        Check.Equal("冲突 · 未解决", entry.StatusText, "冲突优先于其它状态展示");
        Check.That(entry.HasUnstagedChanges, "冲突文件仍需处理");
    }

    private static void StatusTexts()
    {
        Check.Equal("已暂存 · 新增", Staged('A'), "新增的暂存文案");
        Check.Equal("已暂存 · 删除", Staged('D'), "删除的暂存文案");
        Check.Equal("删除", Working('D'), "删除的工作区文案");
        Check.Equal("已暂存 · 重命名", Rename(), "重命名的暂存文案");
        Check.Equal("无变化", new GitChangeEntry { Path = "x" }.StatusText, "两侧都无变化时不应伪装成有改动");

        static string Staged(char code)
        {
            var entry = GitStatusParser.ParsePorcelainV2($"1 {code}. N... 100644 100644 100644 aaa bbb f.cs\0")[0];
            return entry.StatusText;
        }

        static string Working(char code)
        {
            var entry = GitStatusParser.ParsePorcelainV2($"1 .{code} N... 100644 100644 100644 aaa bbb f.cs\0")[0];
            return entry.StatusText;
        }

        static string Rename()
        {
            var entry = GitStatusParser.ParsePorcelainV2("2 R. N... 100644 100644 100644 aaa bbb R100 b.cs\0a.cs\0")[0];
            return entry.StatusText;
        }
    }

    // ── unified diff ─────────────────────────────────────────────────────

    private const string SingleFileDiff =
        "diff --git a/src/App.cs b/src/App.cs\n" +
        "index 1234567..89abcde 100644\n" +
        "--- a/src/App.cs\n" +
        "+++ b/src/App.cs\n" +
        "@@ -1,3 +1,4 @@\n" +
        " using System;\n" +
        " \n" +
        "-Console.WriteLine(1);\n" +
        "+Console.WriteLine(2);\n" +
        "+Console.WriteLine(3);\n";

    private static void ParsesSingleFileHunk()
    {
        var files = UnifiedDiffParser.Parse(SingleFileDiff);

        Check.Equal(1, files.Count, "单文件 diff 应解析为一个条目");
        Check.Equal("src/App.cs", files[0].Path, "路径应去掉 a/ b/ 前缀");
        Check.Equal(1, files[0].Hunks.Count, "一个 hunk");

        var hunk = files[0].Hunks[0];
        Check.Equal(1, hunk.OldStart, "旧起始行");
        Check.Equal(3, hunk.OldCount, "旧行数");
        Check.Equal(1, hunk.NewStart, "新起始行");
        Check.Equal(4, hunk.NewCount, "新行数");
        Check.Equal(5, hunk.Lines.Count, "5 行正文（含空上下文行）");

        Check.Equal(2, files[0].AddedLines, "新增 2 行");
        Check.Equal(1, files[0].RemovedLines, "删除 1 行");
        Check.Equal("+2 −1", files[0].Summary, "摘要格式");

        // 行号：上下文行两侧都有，新增行只有新侧，删除行只有旧侧
        var added = hunk.Lines.First(line => line.Kind == DiffLineKind.Added);
        Check.That(added.OldLineNumber is null, "新增行没有旧行号");
        Check.Equal(3, added.NewLineNumber, "第一处新增在新文件第 3 行（前两行是上下文）");
        Check.Equal("Console.WriteLine(2);", added.Text, "正文应剥掉前缀标记");

        var removed = hunk.Lines.First(line => line.Kind == DiffLineKind.Removed);
        Check.Equal(3, removed.OldLineNumber, "删除行保留旧行号");
        Check.That(removed.NewLineNumber is null, "删除行没有新行号");
    }

    private static void ParsesNewAndDeletedFiles()
    {
        const string newFile =
            "diff --git a/notes.md b/notes.md\n" +
            "new file mode 100644\n" +
            "index 0000000..1111111\n" +
            "--- /dev/null\n" +
            "+++ b/notes.md\n" +
            "@@ -0,0 +1,2 @@\n" +
            "+# 标题\n" +
            "+正文\n";

        var added = UnifiedDiffParser.Parse(newFile)[0];
        Check.That(added.IsNew, "new file mode 应标记为新增");
        Check.Equal("notes.md", added.Path, "/dev/null 不参与路径推导");
        Check.Equal("新增文件", added.KindText, "新增文件标签");
        Check.Equal(2, added.AddedLines, "两行新增");

        const string deletedFile =
            "diff --git a/old.md b/old.md\n" +
            "deleted file mode 100644\n" +
            "index 1111111..0000000\n" +
            "--- a/old.md\n" +
            "+++ /dev/null\n" +
            "@@ -1,1 +0,0 @@\n" +
            "-旧内容\n";

        var deleted = UnifiedDiffParser.Parse(deletedFile)[0];
        Check.That(deleted.IsDeleted, "deleted file mode 应标记为删除");
        Check.Equal("old.md", deleted.Path, "删除的文件取旧路径");
        Check.Equal("删除文件", deleted.KindText, "删除文件标签");
        Check.Equal(1, deleted.RemovedLines, "一行删除");
    }

    private static void ParsesMultipleFiles()
    {
        var output = SingleFileDiff +
                     "diff --git a/README.md b/README.md\n" +
                     "--- a/README.md\n" +
                     "+++ b/README.md\n" +
                     "@@ -1 +1 @@\n" +
                     "-old\n" +
                     "+new\n";

        var files = UnifiedDiffParser.Parse(output);

        Check.Equal(2, files.Count, "应解析出两个文件");
        Check.Equal("src/App.cs", files[0].Path, "第一个文件");
        Check.Equal("README.md", files[1].Path, "第二个文件");
        Check.Equal(1, files[1].AddedLines, "第二个文件一行新增");
    }

    private static void ParsesHunkWithoutCount()
    {
        // @@ -1 +1 @@ 省略 count 时按 1 处理（git 的省略规则）
        var files = UnifiedDiffParser.Parse(
            "diff --git a/x.txt b/x.txt\n" +
            "--- a/x.txt\n" +
            "+++ b/x.txt\n" +
            "@@ -1 +1 @@\n" +
            "-a\n" +
            "+b\n");

        Check.Equal(1, files[0].Hunks[0].OldCount, "省略的旧行数按 1");
        Check.Equal(1, files[0].Hunks[0].NewCount, "省略的新行数按 1");
        Check.Equal(1, files[0].AddedLines, "换一行也应算一增一删");
    }

    private static void ParsesNoNewlineNote()
    {
        var files = UnifiedDiffParser.Parse(
            "diff --git a/x.txt b/x.txt\n" +
            "--- a/x.txt\n" +
            "+++ b/x.txt\n" +
            "@@ -1 +1 @@\n" +
            "-a\n" +
            "\\ No newline at end of file\n" +
            "+b\n" +
            "\\ No newline at end of file\n");

        var lines = files[0].Hunks[0].Lines;
        Check.Equal(2, lines.Count(line => line.Kind == DiffLineKind.NoNewline), "两条尾注各算一行");
        Check.Equal(1, files[0].RemovedLines, "尾注不能被算成删除行");
        Check.Equal(1, files[0].AddedLines, "尾注不能被算成新增行");
    }

    private static void ParsesBinary()
    {
        var files = UnifiedDiffParser.Parse(
            "diff --git a/logo.png b/logo.png\n" +
            "index 1111111..2222222 100644\n" +
            "Binary files a/logo.png and b/logo.png differ\n");

        Check.Equal(1, files.Count, "二进制文件也要有条目，不能凭空消失");
        Check.That(files[0].IsBinary, "应标记为二进制");
        Check.Equal("二进制改动", files[0].KindText, "二进制标签");
        Check.Equal("二进制文件", files[0].Summary, "没有行数可报时直说");
    }

    // ── 编排 ─────────────────────────────────────────────────────────────

    private static void OrdersChangesForDisplay()
    {
        var changes = new List<GitChangeEntry>
        {
            new() { Path = "z-untracked.md", Staged = GitChangeKind.Untracked, Unstaged = GitChangeKind.Untracked },
            new() { Path = "b-modified.cs", Unstaged = GitChangeKind.Modified },
            new() { Path = "a-staged.cs", Staged = GitChangeKind.Modified },
            new() { Path = "c-conflict.cs", Staged = GitChangeKind.Unmerged, Unstaged = GitChangeKind.Unmerged, IsConflicted = true },
        };

        var ordered = GitWorkspaceService.OrderForDisplay(changes);

        Check.Equal("c-conflict.cs", ordered[0].Path, "冲突排最前（它阻塞提交）");
        Check.Equal("a-staged.cs", ordered[1].Path, "其次是已暂存");
        Check.Equal("b-modified.cs", ordered[2].Path, "再次是工作区改动");
        Check.Equal("z-untracked.md", ordered[3].Path, "未跟踪最后");

        // 顺序必须稳定：同样的输入按路径序排，不依赖 git 输出顺序
        var again = GitWorkspaceService.OrderForDisplay(changes);
        Check.Equal(
            string.Join('|', ordered.Select(item => item.Path)),
            string.Join('|', again.Select(item => item.Path)),
            "两次排序结果必须一致");

        Check.Equal(0, GitWorkspaceService.OrderForDisplay(Array.Empty<GitChangeEntry>()).Count, "空清单不炸");
    }

    private static void SuggestsCommitTitle()
    {
        Check.Equal("App.cs", ViewModelsTitleProxy.Single("src/App.cs"), "单文件用文件名当标题");

        Check.Equal(
            "更新 components",
            ViewModelsTitleProxy.Many(new[] { "components/web/a.ts", "components/api/b.ts" }),
            "多文件取公共父目录的最后一段");

        Check.Equal(
            string.Empty,
            ViewModelsTitleProxy.Many(new[] { "src/a.cs", "docs/b.md" }),
            "没有公共父目录时不硬编标题");
    }

    private static void RejectsEmptyTitle()
    {
        var service = new GitWorkspaceService(new NullRunner());
        var outcome = service.CommitAsync(".", "   ", null).GetAwaiter().GetResult();

        Check.That(!outcome.Succeeded, "空标题必须被拦下");
        Check.That(outcome.Message.Contains("标题", StringComparison.Ordinal), "提示应指出是标题的问题");

        var tooLong = new string('字', GitWorkspaceService.MaxTitleLength + 1);
        var second = service.CommitAsync(".", tooLong, null).GetAwaiter().GetResult();
        Check.That(!second.Succeeded, "超长标题必须被拦下");
        Check.That(second.Message.Contains("过长", StringComparison.Ordinal), "提示应说明过长");
    }

    // ── 真 git ───────────────────────────────────────────────────────────

    /// <summary>中文 + 空格路径是 Windows 上最容易翻车的地方，走一遍真实链路。</summary>
    private static void WorkingTreeRoundTrip()
    {
        if (!GitAvailable())
        {
            Console.WriteLine("  · 已跳过真 git 工作区链路（本机无 git）");
            return;
        }

        using var workspace = new TempWorkspace("git-worktree");
        var git = new GitRunner();
        Check.That(git.InitAsync(workspace.Root).GetAwaiter().GetResult().Success, "init 应成功");

        File.WriteAllText(Path.Combine(workspace.Root, "文档 说明.md"), "# 说明");
        Directory.CreateDirectory(Path.Combine(workspace.Root, "src"));
        File.WriteAllText(Path.Combine(workspace.Root, "src", "App.cs"), "class App {}");

        var tree = git.GetWorkingTreeAsync(workspace.Root).GetAwaiter().GetResult();

        Check.That(tree.IsRepository, "应识别为仓库");
        Check.Equal("main", tree.Branch, "分支为 main");
        Check.Equal(2, tree.Changes.Count, "两个未跟踪条目");
        Check.That(
            tree.Changes.Any(change => change.Path == "文档 说明.md"),
            "中文 + 空格的路径必须原样返回，不能被转义或截断");
        Check.Equal(0, tree.StagedCount, "还没暂存，可提交数为 0");

        // 非仓库目录
        using var plain = new TempWorkspace("git-worktree-plain");
        var none = git.GetWorkingTreeAsync(plain.Root).GetAwaiter().GetResult();
        Check.That(!none.IsRepository, "普通目录不是仓库");
        Check.Equal("未初始化 Git", none.Summary, "非仓库摘要");
    }

    /// <summary>
    /// 取消暂存的参数选择：有 HEAD 走 reset，无 HEAD 走 rm --cached。
    /// 后者在现代 git 上其实用不到（reset -- path 已能处理），但它是对老版本 git 的兜底，
    /// 参数正确性必须钉住，否则哪天有人"顺手简化"就会让老环境上的取消暂存直接失败。
    /// </summary>
    private static void UnstageArgumentsPerHead()
    {
        var withHead = GitRunner.UnstageArguments(hasHead: true);
        Check.Equal("reset", withHead[0], "有 HEAD 时用 reset（工作区不动）");
        Check.Equal("--", withHead[^1], "路径前必须有 -- 分隔，否则像 -x 这样的文件名会被当成选项");

        var withoutHead = GitRunner.UnstageArguments(hasHead: false);
        Check.Equal("rm", withoutHead[0], "无 HEAD 时退回 rm --cached");
        Check.That(withoutHead.Contains("--cached"), "rm 必须带 --cached，绝不能真的删工作区文件");
        Check.That(withoutHead.Contains("-r"), "目录条目需要 -r 才能递归摘除");
    }

    /// <summary>零提交仓库里取消暂存：不能失败，也不能动工作区文件。</summary>
    private static void StagesAndUnstagesWithoutHead()
    {
        if (!GitAvailable())
        {
            Console.WriteLine("  · 已跳过暂存链路（本机无 git）");
            return;
        }

        using var workspace = new TempWorkspace("git-stage");
        var git = new GitRunner();
        git.InitAsync(workspace.Root).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(workspace.Root, "a.cs"), "class A {}");

        var stage = git.StageAsync(workspace.Root, new[] { "a.cs" }).GetAwaiter().GetResult();
        Check.That(stage.Success, $"暂存应成功，实际：{stage.Message}");

        var staged = git.GetWorkingTreeAsync(workspace.Root).GetAwaiter().GetResult();
        Check.Equal(1, staged.StagedCount, "暂存后索引区应有 1 项");
        Check.Equal(GitChangeKind.Added, staged.Changes[0].Staged, "暂存态应为新增");

        var unstage = git.UnstageAsync(workspace.Root, new[] { "a.cs" }).GetAwaiter().GetResult();
        Check.That(unstage.Success, $"零提交仓库取消暂存也必须成功，实际：{unstage.Message}");

        var after = git.GetWorkingTreeAsync(workspace.Root).GetAwaiter().GetResult();
        Check.Equal(0, after.StagedCount, "取消暂存后索引区应为空");
        Check.That(
            File.Exists(Path.Combine(workspace.Root, "a.cs")),
            "取消暂存不能动工作区文件");

        // 空列表：不该报错（UI 上"没勾选就点暂存"）
        var noop = git.StageAsync(workspace.Root, Array.Empty<string>()).GetAwaiter().GetResult();
        Check.That(noop.Success, "空路径列表应视为无操作而不是失败");
    }

    private static void CommitsAndReadsBack()
    {
        if (!GitAvailable())
        {
            Console.WriteLine("  · 已跳过提交链路（本机无 git）");
            return;
        }

        using var workspace = new TempWorkspace("git-commit");
        var git = new GitRunner();
        git.InitAsync(workspace.Root).GetAwaiter().GetResult();
        ConfigureIdentity(workspace.Root);

        File.WriteAllText(Path.Combine(workspace.Root, "a.cs"), "class A {}");
        git.StageAsync(workspace.Root, new[] { "a.cs" }).GetAwaiter().GetResult();

        var service = new GitWorkspaceService(git);
        var outcome = service.CommitAsync(workspace.Root, "feat: 初始提交", "第一版").GetAwaiter().GetResult();

        Check.That(outcome.Succeeded, $"提交应成功，实际：{outcome.Message}");
        Check.That(outcome.Hash.Length >= 7, "应回读到提交 hash");
        Check.That(
            outcome.Message.Contains("feat: 初始提交", StringComparison.Ordinal),
            $"成功文案应带上实际提交标题，实际：{outcome.Message}");

        var after = git.GetWorkingTreeAsync(workspace.Root).GetAwaiter().GetResult();
        Check.Equal(0, after.Changes.Count, "提交后工作区应变干净");
        Check.Equal(0, after.StagedCount, "提交后索引区应为空");

        // 索引区空着提交：必须被拦下而不是让 git 报 nothing to commit
        var empty = service.CommitAsync(workspace.Root, "空提交", null).GetAwaiter().GetResult();
        Check.That(!empty.Succeeded, "索引区为空时提交必须失败");
        Check.That(empty.Message.Contains("暂存", StringComparison.Ordinal), "提示应告诉用户先暂存");
    }

    /// <summary>
    /// 关键约束（设计方案 §2.1）：提交走 CLI 才会执行仓库 hooks。
    /// 这条用例同时证明两件事——hook 真的被执行了，且拒绝理由原样回到了用户面前。
    /// </summary>
    private static void HookRejectionSurfaces()
    {
        if (!GitAvailable())
        {
            Console.WriteLine("  · 已跳过 hook 链路（本机无 git）");
            return;
        }

        using var workspace = new TempWorkspace("git-hook");
        var git = new GitRunner();
        git.InitAsync(workspace.Root).GetAwaiter().GetResult();
        ConfigureIdentity(workspace.Root);

        var hookDir = Path.Combine(workspace.Root, ".git", "hooks");
        Directory.CreateDirectory(hookDir);
        File.WriteAllText(
            Path.Combine(hookDir, "pre-commit"),
            "#!/bin/sh\necho \"提交被测试 hook 拒绝\" >&2\nexit 1\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        File.WriteAllText(Path.Combine(workspace.Root, "b.cs"), "class B {}");
        git.StageAsync(workspace.Root, new[] { "b.cs" }).GetAwaiter().GetResult();

        var service = new GitWorkspaceService(git);
        var outcome = service.CommitAsync(workspace.Root, "feat: 会被拒绝", null).GetAwaiter().GetResult();

        Check.That(!outcome.Succeeded, "pre-commit 拒绝时提交必须失败");
        Check.That(
            outcome.Message.Contains("测试 hook 拒绝", StringComparison.Ordinal),
            $"hook 的原文必须透给用户，实际：{outcome.Message}");

        var after = git.GetWorkingTreeAsync(workspace.Root).GetAwaiter().GetResult();
        Check.Equal(1, after.StagedCount, "被拒绝后索引区应保持原样，不能悄悄清空");

        // "提交失败"还不够：要确认真的没有留下提交对象，否则历史里会多一条空提交
        var head = RunGit(workspace.Root, "rev-parse", "--verify", "--quiet", "HEAD");
        Check.That(head.ExitCode != 0, "hook 拒绝后不应产生任何提交对象（HEAD 仍不存在）");
    }

    /// <summary>
    /// 真仓库里的重命名：porcelain v2 的 type-2 记录比 type-1 多一个字段，
    /// 只能靠真 git 验证下标没写错（纯函数用例里的样本是手写的，可能一错俱错）。
    /// </summary>
    private static void RenamesInRealRepository()
    {
        if (!GitAvailable())
        {
            Console.WriteLine("  · 已跳过重命名链路（本机无 git）");
            return;
        }

        using var workspace = new TempWorkspace("git-rename");
        var git = new GitRunner();
        git.InitAsync(workspace.Root).GetAwaiter().GetResult();
        ConfigureIdentity(workspace.Root);

        File.WriteAllText(Path.Combine(workspace.Root, "old-name.cs"), "class A { }");
        git.StageAsync(workspace.Root, new[] { "old-name.cs" }).GetAwaiter().GetResult();
        var service = new GitWorkspaceService(git);
        Check.That(
            service.CommitAsync(workspace.Root, "chore: 初始", null).GetAwaiter().GetResult().Succeeded,
            "重命名用例的前置提交应成功");

        RunGit(workspace.Root, "mv", "old-name.cs", "new-name.cs");

        var tree = git.GetWorkingTreeAsync(workspace.Root).GetAwaiter().GetResult();
        var renamed = tree.Changes.FirstOrDefault();

        Check.NotNull(renamed, "git mv 后应有一条变更");
        Check.Equal("new-name.cs", renamed?.Path, "新路径");
        Check.Equal("old-name.cs", renamed?.OriginalPath, "源路径来自独立字段，解析下标必须对");
        Check.Equal("old-name.cs → new-name.cs", renamed?.DisplayPath, "展示为 旧 → 新");
    }

    // ── 辅助（GitAvailable / ConfigureIdentity / RunGit 见 GitTestSupport） ──

    /// <summary>标题建议的纯函数在应用层，包一层只是让上面的断言读起来集中。</summary>
    private static class ViewModelsTitleProxy
    {
        public static string Single(string path) => Many(new[] { path });

        public static string Many(IReadOnlyList<string> paths) => GitWorkspaceService.SuggestTitle(paths);
    }

    /// <summary>只为验证"参数校验在调 git 之前完成"：所有读写都记下来但不落盘。</summary>
    private sealed class NullRunner : IGitWorkspaceRunner
    {
        public Task<GitWorkingTree> GetWorkingTreeAsync(string repositoryRoot, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("空标题校验不应走到这里");

        public Task<FileDiff> GetDiffAsync(string repositoryRoot, string path, bool staged, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("空标题校验不应走到这里");

        public Task<GitCommandResult> StageAsync(string repositoryRoot, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("空标题校验不应走到这里");

        public Task<GitCommandResult> UnstageAsync(string repositoryRoot, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("空标题校验不应走到这里");

        public Task<GitCommitResult> CommitAsync(string repositoryRoot, string title, string? body, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("空标题校验不应走到这里");
    }
}
