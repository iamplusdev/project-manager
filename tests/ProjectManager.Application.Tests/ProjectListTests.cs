using ProjectManager.Application.Projects;
using ProjectManager.Storage;

namespace ProjectManager.Application.Tests;

/// <summary>顶栏搜索：过滤规则、大小写、路径命中、无命中与清空复原。</summary>
public static class ProjectListTests
{
    public static void Run()
    {
        Check.Section("List · 搜索过滤");
        BlankSearchShowsAll();
        FiltersByNameIgnoringCase();
        FiltersByPath();
        NoMatchKeepsListButHidesAll();
        ClearingSearchRestoresAll();
    }

    private static ProjectListModel NewModel(TempWorkspace workspace)
    {
        var repository = new JsonProjectRepository(workspace.Path2("projects.json"));
        var seeds = new[]
        {
            ("OrderDesk", "OrderDesk"),
            ("web-portal", "apps/web-portal"),
            ("LegacyTool", "legacy-tool"),
        };

        for (var index = 0; index < seeds.Length; index++)
        {
            var (name, folder) = seeds[index];
            repository.UpsertAsync(new StoredProject
            {
                Id = $"p{index}",
                Name = name,
                Path = workspace.Path2(folder.Replace('/', Path.DirectorySeparatorChar)),
                LastOpenedAt = new DateTimeOffset(2026, 10, 23, 10 + index, 0, 0, TimeSpan.FromHours(8)),
            }).GetAwaiter().GetResult();
        }

        var model = new ProjectListModel(repository);
        model.LoadAsync().GetAwaiter().GetResult();
        return model;
    }

    private static string Names(IEnumerable<StoredProject> projects) => string.Join("|", projects.Select(p => p.Name));

    private static void BlankSearchShowsAll()
    {
        using var workspace = new TempWorkspace("list-blank");
        var model = NewModel(workspace);

        Check.Equal(3, model.Projects.Count, "清单应有 3 个项目");
        Check.Equal("LegacyTool|web-portal|OrderDesk", Names(model.VisibleProjects), "未搜索时应按最近打开倒序显示全部");
        Check.That(!model.IsFiltering, "未搜索时 IsFiltering 应为 false");
        Check.That(model.HasVisibleProjects, "有项目时 HasVisibleProjects 应为 true");
    }

    private static void FiltersByNameIgnoringCase()
    {
        using var workspace = new TempWorkspace("list-name");
        var model = NewModel(workspace);

        model.SearchText = "legacy";
        Check.Equal("LegacyTool", Names(model.VisibleProjects), "名称命中应大小写不敏感");
        Check.That(model.IsFiltering, "有搜索词时 IsFiltering 应为 true");

        model.SearchText = "  ORDER  ";
        Check.Equal("OrderDesk", Names(model.VisibleProjects), "搜索词应忽略首尾空格");
    }

    private static void FiltersByPath()
    {
        using var workspace = new TempWorkspace("list-path");
        var model = NewModel(workspace);

        model.SearchText = "apps";
        Check.Equal("web-portal", Names(model.VisibleProjects), "路径命中应参与过滤");
    }

    private static void NoMatchKeepsListButHidesAll()
    {
        using var workspace = new TempWorkspace("list-nomatch");
        var model = NewModel(workspace);

        model.SearchText = "zzz-not-exist";
        Check.Equal(0, model.VisibleProjects.Count, "无命中时可见列表为空");
        Check.That(!model.HasVisibleProjects, "无命中时 HasVisibleProjects 应为 false");
        Check.Equal(3, model.Projects.Count, "过滤不得改变清单本身");
        Check.That(!model.IsEmpty, "清单非空时 IsEmpty 不应为 true（空态文案要靠 IsFiltering 区分）");
    }

    private static void ClearingSearchRestoresAll()
    {
        using var workspace = new TempWorkspace("list-restore");
        var model = NewModel(workspace);

        model.SearchText = "web";
        Check.Equal(1, model.VisibleProjects.Count, "过滤后应剩 1 个");

        model.SearchText = string.Empty;
        Check.Equal(3, model.VisibleProjects.Count, "清空搜索后应恢复全部");
        Check.That(!model.IsFiltering, "清空后 IsFiltering 应为 false");
    }
}
