using ProjectManager.Application.Git;
using ProjectManager.CliInterop;
using static ProjectManager.Application.Tests.GitTestSupport;

namespace ProjectManager.Application.Tests;

/// <summary>
/// 提交历史（M5 · WP5.4）：解析器纯函数 + 真 git 走一遍。
/// 无 git 环境时真机部分自动跳过，纯函数部分照跑。
/// </summary>
public static class GitHistoryTests
{
    public static void Run()
    {
        Check.Section("Git · 提交历史解析（log）");
        ParsesLogRecords();
        ClassifiesParentCount();
        ClassifiesRefs();
        DropsMalformedLines();
        ToleratesUnknownDate();

        Check.Section("Git · numstat 解析");
        ParsesNumstat();
        SummarisesFiles();

        Check.Section("Git · 历史展示规则");
        DescribesRelativeTime();
        DescribesRefsLine();
        ValidatesCommitHash();

        Check.Section("Git · 真仓库历史");
        ReadsHistoryNewestFirst();
        PaginatesWithSkip();
        HandlesEmptyRepository();
        HandlesNonRepository();
        ReportsParentRepository();
        ReadsCommitDetail();
        KeepsChineseAndSpacePaths();
        CountsMergeAgainstFirstParent();
        MarksBinaryFiles();
    }

    // ── 纯函数：log 解析 ─────────────────────────────────────────────────

    private static void ParsesLogRecords()
    {
        const string Output =
            "aaa1111\taaa\t张三\tzhang@example.invalid\t2026-10-20T10:30:00+08:00\tbbb2222\tHEAD -> refs/heads/main, tag: refs/tags/v1.0\tfeat: 第一个提交\n" +
            "bbb2222\tbbb\t李四\tli@example.invalid\t2026-10-19T08:00:00+08:00\t\trefs/heads/feature\tfix: 第二个提交\n";

        var entries = GitHistoryParser.ParseLog(Output.Replace("\t", "\0"));

        Check.Equal(2, entries.Count, "两条记录都应解析出来");
        Check.Equal("aaa1111", entries[0].Hash, "完整 hash");
        Check.Equal("aaa", entries[0].ShortHash, "短 hash 用 git 自己给的值，不自己截");
        Check.Equal("张三", entries[0].Author, "作者名（中文不转义）");
        Check.Equal("zhang@example.invalid", entries[0].AuthorEmail, "作者邮箱");
        Check.Equal("feat: 第一个提交", entries[0].Subject, "标题");
        Check.Equal(2026, entries[0].AuthorDate?.Year ?? 0, "作者时间");
        Check.Equal(TimeSpan.FromHours(8), entries[0].AuthorDate?.Offset ?? TimeSpan.Zero, "时区偏移要保留，不能被本地时区吃掉");
        Check.Equal("fix: 第二个提交", entries[1].Subject, "第二条标题");
    }

    private static void ClassifiesParentCount()
    {
        Check.Equal(0, Entry(parents: string.Empty).ParentCount, "无父提交");
        Check.That(Entry(parents: string.Empty).IsRoot, "无父提交应标记为根提交");
        Check.Equal("根提交", Entry(parents: string.Empty).KindNote, "根提交有类型注记");

        var normal = Entry(parents: "bbb2222");
        Check.Equal(1, normal.ParentCount, "一个父提交");
        Check.That(!normal.IsMerge && !normal.IsRoot, "普通提交两种标记都不是");
        Check.Equal(string.Empty, normal.KindNote, "普通提交不加注记（避免每行都是噪声）");

        var merge = Entry(parents: "bbb2222 ccc3333");
        Check.Equal(2, merge.ParentCount, "两个父提交");
        Check.That(merge.IsMerge, "两个父提交就是合并提交");
        Check.Equal("合并", merge.KindNote, "合并提交的注记");
    }

    private static void ClassifiesRefs()
    {
        var head = GitHistoryParser.ParseRefs("HEAD -> refs/heads/main");
        Check.Equal(1, head.Count, "一个 ref");
        Check.Equal(GitRefKind.Branch, head[0].Kind, "HEAD -> 指向的是本地分支");
        Check.Equal("main", head[0].Name, "限定前缀要剥掉");
        Check.That(head[0].IsCurrent, "它就是当前分支");
        Check.Equal("*main", head[0].Display, "当前分支加星标");

        var remote = GitHistoryParser.ParseRefs("refs/remotes/origin/main");
        Check.Equal(GitRefKind.Remote, remote[0].Kind, "远程跟踪分支");
        Check.Equal("origin/main", remote[0].Name, "远程分支保留 remote 名");

        var tag = GitHistoryParser.ParseRefs("tag: refs/tags/v1.0");
        Check.Equal(GitRefKind.Tag, tag[0].Kind, "tag: 前缀要被识别");
        Check.Equal("v1.0", tag[0].Name, "标签名");

        var many = GitHistoryParser.ParseRefs("HEAD -> refs/heads/main, refs/remotes/origin/main, tag: refs/tags/v1.0");
        Check.Equal(3, many.Count, "逗号+空格分隔三个 ref");
        Check.Equal(GitRefKind.Remote, many[1].Kind, "中间那个是远程分支");
        Check.Equal(GitRefKind.Tag, many[2].Kind, "最后那个是标签");

        Check.Equal(0, GitHistoryParser.ParseRefs(string.Empty).Count, "没有 ref 时为空");
        Check.Equal(0, GitHistoryParser.ParseRefs(null).Count, "null 输入不抛异常");

        // 本地分支 feature/x 与远程 origin/x 都带斜杠：靠限定名区分，而不是猜
        var slashy = GitHistoryParser.ParseRefs("refs/heads/feature/x");
        Check.Equal(GitRefKind.Branch, slashy[0].Kind, "带斜杠的本地分支仍是 Branch");
        Check.Equal("feature/x", slashy[0].Name, "分层分支名原样保留");
    }

    private static void DropsMalformedLines()
    {
        const string Output = "只有一行的垃圾输出\n" +
                              "aaa1111\tShort\0Author\0mail\0not-a-date\0\0\0subject\n";

        var entries = GitHistoryParser.ParseLog(Output.Replace("\t", "\0", StringComparison.Ordinal));

        Check.Equal(1, entries.Count, "字段不足的行应被丢弃，而不是错位取值");
        Check.Equal("aaa1111", entries[0].Hash, "保留能解析的那条");
        Check.Equal(0, GitHistoryParser.ParseLog(string.Empty).Count, "空输出");
        Check.Equal(0, GitHistoryParser.ParseLog(null).Count, "null 输出");
    }

    private static void ToleratesUnknownDate()
    {
        var entry = GitHistoryParser.ParseLog("aaa1111\0aaa\0A\0a@b\0不是日期\0\0\0s\n")[0];
        Check.That(entry.AuthorDate is null, "无法解析的日期必须是 null，不能悄悄当成本地时间");
        Check.Equal("时间未知", GitHistoryService.DescribeWhen(DateTimeOffset.Now, entry.AuthorDate), "展示层兜住 null");
    }

    // ── 纯函数：numstat ─────────────────────────────────────────────────

    private static void ParsesNumstat()
    {
        const string Text = "\n\n3\t1\tsrc/App.cs\n0\t0\told.txt => new.txt\n-\t-\tlogo.png\n";

        var files = GitHistoryParser.ParseNumstat(Text);

        Check.Equal(3, files.Count, "三行有效记录（前导空行不算）");
        Check.Equal("src/App.cs", files[0].Path, "路径");
        Check.Equal(3, files[0].Added ?? -1, "新增行数");
        Check.Equal(1, files[0].Deleted ?? -1, "删除行数");
        Check.Equal("+3 −1", files[0].ChangeText, "改动文案");

        Check.That(files[1].IsRename, "numstat 的 \"旧 => 新\" 应识别为重命名");
        Check.That(!files[1].IsBinary, "重命名不是二进制");

        Check.That(files[2].IsBinary, "-/- 表示二进制");
        Check.Equal("二进制", files[2].ChangeText, "二进制不给出行数（0/0 会误导）");
        Check.That(files[2].Added is null, "二进制的行数必须是 null 而不是 0");
    }

    private static void SummarisesFiles()
    {
        var commit = Entry(parents: "p");
        var detail = new GitCommitDetail
        {
            Commit = commit,
            Files = new[]
            {
                new GitCommitFile { Path = "a.cs", Added = 10, Deleted = 2 },
                new GitCommitFile { Path = "b.cs", Added = 5, Deleted = 0 },
                new GitCommitFile { Path = "c.png", Added = null, Deleted = null },
            },
        };

        Check.Equal(15, detail.TotalAdded, "合计新增（二进制不计）");
        Check.Equal(2, detail.TotalDeleted, "合计删除");
        Check.That(detail.FilesSummary.Contains("3 个文件"), $"摘要应含文件数，实际：{detail.FilesSummary}");
        Check.That(detail.FilesSummary.Contains("+15"), $"摘要应含合计新增，实际：{detail.FilesSummary}");
        Check.That(detail.FilesSummary.Contains("含 1 个二进制"), $"摘要应标出二进制，实际：{detail.FilesSummary}");

        var empty = new GitCommitDetail { Commit = commit };
        Check.Equal("这次提交没有文件改动。", empty.FilesSummary, "空清单要说清为什么空");

        var merge = new GitCommitDetail { Commit = Entry(parents: "p1 p2") };
        Check.That(merge.FilesSummary.Contains("第一父提交"), $"合并提交的空清单要解释口径，实际：{merge.FilesSummary}");

        var failed = new GitCommitDetail { Commit = commit, Error = "读取失败" };
        Check.Equal("读取失败", failed.FilesSummary, "有错误时优先显示错误");
    }

    // ── 纯函数：展示规则 ────────────────────────────────────────────────

    private static void DescribesRelativeTime()
    {
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(8));

        Check.Equal("刚刚", GitHistoryService.DescribeWhen(now, now.AddSeconds(-30)), "30 秒内");
        Check.Equal("刚刚", GitHistoryService.DescribeWhen(now, now), "同一时刻");
        Check.Equal("刚刚", GitHistoryService.DescribeWhen(now, now.AddMinutes(5)), "未来时间（时钟偏差）也说刚刚");
        Check.Equal("5 分钟前", GitHistoryService.DescribeWhen(now, now.AddMinutes(-5)), "分钟级");
        Check.Equal("59 分钟前", GitHistoryService.DescribeWhen(now, now.AddMinutes(-59)), "刚不到一小时");
        Check.Equal("3 小时前", GitHistoryService.DescribeWhen(now, now.AddHours(-3)), "小时级");
        Check.Equal("昨天", GitHistoryService.DescribeWhen(now, now.AddDays(-1)), "一天前说昨天");
        Check.Equal("5 天前", GitHistoryService.DescribeWhen(now, now.AddDays(-5)), "天级");
        Check.Equal("2026-08-01", GitHistoryService.DescribeWhen(now, new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.FromHours(8))), "超过 30 天改用日期");

        // 东八区写的提交，在西半球看仍是"3 小时前"：比较的是绝对时刻，不是墙上时间
        var authorInCst = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(8));
        var viewerInUtc = authorInCst.ToUniversalTime().AddHours(3);
        Check.Equal("3 小时前", GitHistoryService.DescribeWhen(viewerInUtc, authorInCst), "时区不同的两端结论一致");
    }

    private static void DescribesRefsLine()
    {
        Check.Equal(string.Empty, GitHistoryService.DescribeRefs(Array.Empty<GitRefLabel>()), "没有 ref 时为空串");

        var one = new[] { new GitRefLabel(GitRefKind.Branch, "main", true) };
        Check.Equal("*main", GitHistoryService.DescribeRefs(one), "单个 ref");

        var four = new[]
        {
            new GitRefLabel(GitRefKind.Branch, "main", true),
            new GitRefLabel(GitRefKind.Remote, "origin/main", false),
            new GitRefLabel(GitRefKind.Tag, "v1.0", false),
            new GitRefLabel(GitRefKind.Tag, "v0.9", false),
        };

        var text = GitHistoryService.DescribeRefs(four);
        Check.That(text.EndsWith('…'), $"超过 3 个要省略，实际：{text}");
        Check.That(!text.Contains("v0.9", StringComparison.Ordinal), "第 4 个标签不显示");
    }

    private static void ValidatesCommitHash()
    {
        Check.That(GitRunner.IsCommitHash("bce03a7"), "7 位短 hash 合法");
        Check.That(GitRunner.IsCommitHash(new string('a', 40)), "40 位完整 hash 合法");
        Check.That(!GitRunner.IsCommitHash("bce03"), "太短不合法");
        Check.That(!GitRunner.IsCommitHash("--upload-pack=calc"), "以 -- 开头的字符串不能进命令行");
        Check.That(!GitRunner.IsCommitHash("zzzzzzz"), "非十六进制不合法");
        Check.That(!GitRunner.IsCommitHash(null), "null 不合法");
    }

    // ── 真 git ──────────────────────────────────────────────────────────

    private static void ReadsHistoryNewestFirst()
    {
        if (!GitAvailable())
        {
            Console.WriteLine("  · 已跳过真仓库历史（本机无 git）");
            return;
        }

        using var workspace = new TempWorkspace("history-order");
        var git = CreateRepository(workspace.Root);

        File.WriteAllText(workspace.Path2("a.txt"), "a\n");
        CommitAll(workspace.Root, "feat: 第一条");
        File.WriteAllText(workspace.Path2("b.txt"), "b\n");
        CommitAll(workspace.Root, "feat: 第二条");

        var page = git.GetHistoryAsync(workspace.Root, 10).GetAwaiter().GetResult();

        Check.That(page.IsRepository, "应识别为仓库");
        Check.That(!page.HasNoCommits, "有提交");
        Check.Equal(2, page.Commits.Count, "两条提交");
        Check.Equal("feat: 第二条", page.Commits[0].Subject, "最新的排在最前");
        Check.Equal("feat: 第一条", page.Commits[1].Subject, "旧的在后");
        Check.That(page.Commits[1].IsRoot, "最早的提交是根提交");
        Check.Equal("PM Test", page.Commits[0].Author, "作者来自仓库配置");
        Check.That(page.Commits[0].AuthorDate is not null, "作者时间应解析成功");

        // HEAD -> 当前分支的标记必须落在第一条上
        var head = page.Commits[0].Refs.FirstOrDefault(label => label.IsCurrent);
        Check.NotNull(head, "最新提交应带当前分支标记");

        // 分支名跟 git 自己报的对齐（不写死 master/main：init.defaultBranch 因机器而异）
        var branch = RunGit(workspace.Root, "branch", "--show-current").Output.Trim();
        Check.Equal(branch, head!.Name, "分支名取自 refs/heads/ 并剥掉前缀");

        // 必须断言**类型**，不只是名字：不加 --decorate=full 时 git 也给出裸名 "main"，
        // 名字照样对得上，但它丢掉了 "refs/heads/" 前缀，分类会退化成 Other——
        // 只有断言 Kind 才抓得住这个退化
        Check.Equal(GitRefKind.Branch, head.Kind, "带 HEAD 指针的本地分支应分类为 Branch");
        Check.That(!page.HasMore, "两条都取到了，没有下一页");
        Check.Equal(2, page.LoadedCount, "已加载计数");
    }

    private static void PaginatesWithSkip()
    {
        if (!GitAvailable())
        {
            return;
        }

        using var workspace = new TempWorkspace("history-page");
        var git = CreateRepository(workspace.Root);

        for (var i = 1; i <= 5; i++)
        {
            File.WriteAllText(workspace.Path2("f.txt"), $"line {i}\n");
            CommitAll(workspace.Root, $"chore: 第 {i} 次");
        }

        var first = git.GetHistoryAsync(workspace.Root, 2).GetAwaiter().GetResult();
        Check.Equal(2, first.Commits.Count, "首页取 2 条");
        Check.That(first.HasMore, "还有更多：必须靠多取一条判定，不能靠 Commits.Count == limit 猜");
        Check.Equal("chore: 第 5 次", first.Commits[0].Subject, "首页第一条是最新的");

        var second = git.GetHistoryAsync(workspace.Root, 2, skip: 2).GetAwaiter().GetResult();
        Check.Equal(2, second.Commits.Count, "第二页 2 条");
        Check.Equal("chore: 第 3 次", second.Commits[0].Subject, "skip 生效：跳过前两条");
        Check.Equal(4, second.LoadedCount, "已加载总数 = skip + 本页");

        var last = git.GetHistoryAsync(workspace.Root, 2, skip: 4).GetAwaiter().GetResult();
        Check.Equal(1, last.Commits.Count, "最后一页只剩 1 条");
        Check.That(!last.HasMore, "到头了");
        Check.Equal("chore: 第 1 次", last.Commits[0].Subject, "最后一条是最早的");

        var past = git.GetHistoryAsync(workspace.Root, 2, skip: 99).GetAwaiter().GetResult();
        Check.Equal(0, past.Commits.Count, "越过末尾返回空页而不是报错");
        Check.That(!past.HasMore, "越过末尾没有更多");
    }

    private static void HandlesEmptyRepository()
    {
        if (!GitAvailable())
        {
            return;
        }

        using var workspace = new TempWorkspace("history-empty");
        var git = CreateRepository(workspace.Root);

        var page = git.GetHistoryAsync(workspace.Root, 10).GetAwaiter().GetResult();

        // 关键：空仓库不是错误。git log 在空仓库上以 128 退出，若照搬退出码，
        // 刚 init 的项目会看到一片红色错误提示
        Check.That(page.IsRepository, "空仓库仍是仓库");
        Check.That(page.HasNoCommits, "应标记为「还没有提交」");
        Check.That(page.Error is null, $"空仓库不能当错误，实际：{page.Error}");
        Check.Equal(0, page.Commits.Count, "没有提交");

        var view = new GitHistoryView { Page = page };
        Check.That(view.EmptyHint.Contains("还没有提交"), $"空态要说明原因，实际：{view.EmptyHint}");
        Check.That(!view.HasCommits, "空态");
    }

    private static void HandlesNonRepository()
    {
        if (!GitAvailable())
        {
            return;
        }

        using var workspace = new TempWorkspace("history-plain");

        var page = new GitRunner().GetHistoryAsync(workspace.Root, 10).GetAwaiter().GetResult();

        Check.That(!page.IsRepository, "普通目录不是仓库");
        Check.That(page.Error is null, "「不是仓库」是正常状态，不是错误");
        Check.Equal(0, page.Commits.Count, "没有提交");

        var view = new GitHistoryView { Page = page };
        Check.That(view.EmptyHint.Contains("独立仓库"), $"空态要指向「去初始化」，实际：{view.EmptyHint}");
    }

    private static void ReportsParentRepository()
    {
        if (!GitAvailable())
        {
            return;
        }

        using var workspace = new TempWorkspace("history-nested");
        CreateRepository(workspace.Root);

        var child = workspace.Path2("child");
        Directory.CreateDirectory(child);

        var page = new GitRunner().GetHistoryAsync(child, 10).GetAwaiter().GetResult();

        // 子目录里 git log 是能跑通的（会读上层仓库的历史）。照单全收就等于把别人的提交
        // 当成这个项目的——和 WP3.5 修的那个 git 误判是同一个坑
        Check.That(!page.IsRepository, "子目录不能被当作独立仓库");
        Check.NotNull(page.Error, "要告诉用户原因");
        Check.That(page.Error!.Contains("上层仓库", StringComparison.Ordinal), $"提示应指出位于上层仓库，实际：{page.Error}");
        Check.Equal(0, page.Commits.Count, "不能把上层仓库的提交列出来");
    }

    private static void ReadsCommitDetail()
    {
        if (!GitAvailable())
        {
            return;
        }

        using var workspace = new TempWorkspace("history-detail");
        var git = CreateRepository(workspace.Root);

        File.WriteAllText(workspace.Path2("a.txt"), "one\ntwo\n");
        File.WriteAllText(workspace.Path2("b.txt"), "only\n");
        CommitAll(workspace.Root, "feat: 加上两个文件", "正文第一行\n正文第二行");

        var page = git.GetHistoryAsync(workspace.Root, 5).GetAwaiter().GetResult();
        var detail = git.GetCommitDetailAsync(workspace.Root, page.Commits[0]).GetAwaiter().GetResult();

        Check.That(detail.Error is null, $"读取详情不该失败，实际：{detail.Error}");
        Check.That(detail.Message.Contains("feat: 加上两个文件"), $"完整信息应含标题，实际：{detail.Message}");
        Check.That(detail.Message.Contains("正文第二行"), $"完整信息应含正文，实际：{detail.Message}");
        Check.That(!detail.Message.EndsWith('\n'), "尾部换行不是提交信息的一部分");
        Check.Equal(2, detail.Files.Count, "两个文件");
        Check.Equal(3, detail.TotalAdded, "共 3 行新增（a.txt 两行 + b.txt 一行）");
        Check.Equal(0, detail.TotalDeleted, "没有删除");

        // 不存在的 hash 要被挡在命令行之外，而不是让 git 去报一句难懂的话
        var bogus = git.GetCommitDetailAsync(workspace.Root, new GitCommitEntry { Hash = "not-a-hash", ShortHash = "x" })
            .GetAwaiter().GetResult();
        Check.NotNull(bogus.Error, "非法 hash 直接拒绝");
        Check.Equal(0, bogus.Files.Count, "拒绝时不返回任何文件");
    }

    private static void KeepsChineseAndSpacePaths()
    {
        if (!GitAvailable())
        {
            return;
        }

        using var workspace = new TempWorkspace("history-i18n");
        var git = CreateRepository(workspace.Root);

        File.WriteAllText(workspace.Path2("中文 文件.txt"), "内容\n");
        CommitAll(workspace.Root, "feat: 中文路径");

        var page = git.GetHistoryAsync(workspace.Root, 5).GetAwaiter().GetResult();
        var detail = git.GetCommitDetailAsync(workspace.Root, page.Commits[0]).GetAwaiter().GetResult();

        Check.Equal(1, detail.Files.Count, "一个文件");
        Check.Equal("中文 文件.txt", detail.Files[0].Path, "中文路径不被转义成八进制（core.quotepath=false）");
        Check.That(!detail.Files[0].Path.StartsWith('"'), "路径不带引号");
    }

    private static void CountsMergeAgainstFirstParent()
    {
        if (!GitAvailable())
        {
            return;
        }

        using var workspace = new TempWorkspace("history-merge");
        var git = CreateRepository(workspace.Root);

        File.WriteAllText(workspace.Path2("base.txt"), "base\n");
        CommitAll(workspace.Root, "chore: 起点");

        // 分支名不写死 master/main：init.defaultBranch 因机器而异，写死会让
        // checkout 静默失败、后面的 merge 变成 "Already up to date"，测试就测了个寂寞
        var trunk = RunGit(workspace.Root, "branch", "--show-current").Output.Trim();

        RunGit(workspace.Root, "checkout", "-q", "-b", "feature");
        File.WriteAllText(workspace.Path2("only-feature.txt"), "x\n");
        CommitAll(workspace.Root, "feat: 分支上的文件");

        RunGit(workspace.Root, "checkout", "-q", trunk);
        File.WriteAllText(workspace.Path2("only-main.txt"), "y\n");
        CommitAll(workspace.Root, "feat: 主线上的文件");

        var merged = RunGit(workspace.Root, "merge", "-q", "--no-ff", "feature", "-m", "merge: 合并 feature");
        Check.Equal(0, merged.ExitCode, $"合并这一步必须真的成功，否则后面测的是普通提交：{merged.Output}");

        var page = git.GetHistoryAsync(workspace.Root, 5).GetAwaiter().GetResult();
        Check.Equal("merge: 合并 feature", page.Commits[0].Subject, "合并提交在最前");
        Check.That(page.Commits[0].IsMerge, "应识别为合并提交");

        var detail = git.GetCommitDetailAsync(workspace.Root, page.Commits[0]).GetAwaiter().GetResult();

        // 断言的是**结果**（1 个文件、来自第一父），不是"有没有加 --first-parent"这个动作。
        // 实测：现代 git 不加选项也等价于第一父；会翻倍的是 -m。两种改法都试过，
        // 只有"去掉 --first-parent 且换成 -m"才让这条变红——这正是它要守住的翻倍场景
        Check.Equal(1, detail.Files.Count, "按第一父提交统计：只应有 1 个文件");
        Check.Equal("only-feature.txt", detail.Files[0].Path, "差异是主线缺少的那一个文件");
    }

    private static void MarksBinaryFiles()
    {
        if (!GitAvailable())
        {
            return;
        }

        using var workspace = new TempWorkspace("history-binary");
        var git = CreateRepository(workspace.Root);

        // 带 NUL 字节：git 据此判定为二进制
        File.WriteAllBytes(workspace.Path2("logo.png"), new byte[] { 0x89, 0x50, 0x00, 0x01, 0x02 });
        File.WriteAllText(workspace.Path2("readme.md"), "text\n");
        CommitAll(workspace.Root, "chore: 加一个二进制文件");

        var page = git.GetHistoryAsync(workspace.Root, 5).GetAwaiter().GetResult();
        var detail = git.GetCommitDetailAsync(workspace.Root, page.Commits[0]).GetAwaiter().GetResult();

        var binary = detail.Files.FirstOrDefault(file => file.Path == "logo.png");
        Check.NotNull(binary, "二进制文件应出现在清单里");
        Check.That(binary!.IsBinary, "应标记为二进制");
        Check.Equal("二进制", binary.ChangeText, "二进制不给行数");
        Check.Equal(1, detail.Files.Count(file => file.IsBinary), "只有一个二进制文件");
        Check.That(detail.FilesSummary.Contains("二进制"), $"摘要要提醒有二进制，实际：{detail.FilesSummary}");
    }

    // ── 辅助 ─────────────────────────────────────────────────────────────

    /// <summary>拼一条最少字段的 log 记录，只关心父提交数与 refs。</summary>
    private static GitCommitEntry Entry(string parents)
    {
        var output = string.Join(
            '\0',
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "aaaaaaa",
            "PM Test",
            "pm@example.invalid",
            "2026-10-20T10:00:00+08:00",
            parents,
            string.Empty,
            "chore: 样例");

        return GitHistoryParser.ParseLog(output + "\n")[0];
    }
}
