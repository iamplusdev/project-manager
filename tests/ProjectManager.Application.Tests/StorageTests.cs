using ProjectManager.Scaffolding.Models;
using ProjectManager.Scaffolding.Scaffolding;
using ProjectManager.Scaffolding.Templates;
using ProjectManager.Storage;

namespace ProjectManager.Application.Tests;

/// <summary>项目清单持久化：原子写、同路径去重、损坏隔离、最近排序。</summary>
public static class StorageTests
{
    public static void Run()
    {
        Check.Section("Storage · 项目清单");
        UpsertThenReload();
        UpsertSamePathKeepsIdAndCreatedAt();
        TouchReordersRecent();
        RemoveDropsEntry();
        CorruptFileIsQuarantinedThenUsable();
        TempFileNotLeftBehind();

        Check.Section("Storage · 设置");
        SettingsRoundTrip();
        BrokenSettingsFallBackToDefaults();
    }

    private static void UpsertThenReload()
    {
        using var workspace = new TempWorkspace("storage-upsert");
        var repository = new JsonProjectRepository(workspace.Path2("projects.json"));

        var stored = repository.UpsertAsync(NewProject("Alpha", workspace.Path2("Alpha"))).GetAwaiter().GetResult();
        Check.That(stored.Id.Length > 0, "Upsert 应返回带 Id 的记录");
        Check.That(File.Exists(workspace.Path2("projects.json")), "清单文件应已落盘");

        var loaded = repository.LoadAsync().GetAwaiter().GetResult();
        Check.Equal(1, loaded.Projects.Count, "重新加载应有 1 个项目");
        Check.Equal("Alpha", loaded.Projects[0].Name, "项目名应保持");
        Check.Ok(loaded.Warning is null, "正常清单不应产生警告");
    }

    private static void UpsertSamePathKeepsIdAndCreatedAt()
    {
        using var workspace = new TempWorkspace("storage-dedupe");
        var repository = new JsonProjectRepository(workspace.Path2("projects.json"));
        var path = workspace.Path2("SamePath");

        var first = repository.UpsertAsync(NewProject("First", path, DateTimeOffset.Parse("2026-01-01T00:00:00+08:00")))
            .GetAwaiter().GetResult();
        var second = repository.UpsertAsync(NewProject("Second", path, DateTimeOffset.Parse("2026-02-02T00:00:00+08:00")))
            .GetAwaiter().GetResult();

        Check.Equal(first.Id, second.Id, "同一路径重复注册应复用 Id");
        Check.Equal("Second", second.Name, "名称应以最新一次注册为准");
        Check.Equal(first.CreatedAt, second.CreatedAt, "首次创建时间不应被覆盖");

        var loaded = repository.LoadAsync().GetAwaiter().GetResult();
        Check.Equal(1, loaded.Projects.Count, "同一路径不应出现两条记录");
    }

    private static void TouchReordersRecent()
    {
        using var workspace = new TempWorkspace("storage-touch");
        var repository = new JsonProjectRepository(workspace.Path2("projects.json"));

        var older = repository.UpsertAsync(NewProject("Older", workspace.Path2("Older"), DateTimeOffset.Parse("2026-01-01T00:00:00+08:00")))
            .GetAwaiter().GetResult();
        repository.UpsertAsync(NewProject("Newer", workspace.Path2("Newer"), DateTimeOffset.Parse("2026-01-02T00:00:00+08:00")))
            .GetAwaiter().GetResult();

        var before = repository.ListRecentAsync(10).GetAwaiter().GetResult();
        Check.Equal("Newer", before[0].Name, "默认按 LastOpenedAt 倒序");

        repository.TouchAsync(older.Id, DateTimeOffset.Parse("2026-03-03T00:00:00+08:00")).GetAwaiter().GetResult();
        var after = repository.ListRecentAsync(10).GetAwaiter().GetResult();
        Check.Equal("Older", after[0].Name, "Touch 后应排到最前");

        var missing = repository.TouchAsync("not-exist", DateTimeOffset.Now).GetAwaiter().GetResult();
        Check.Ok(missing is null, "Touch 不存在的 Id 应返回 null");
    }

    private static void RemoveDropsEntry()
    {
        using var workspace = new TempWorkspace("storage-remove");
        var repository = new JsonProjectRepository(workspace.Path2("projects.json"));
        var stored = repository.UpsertAsync(NewProject("ToRemove", workspace.Path2("ToRemove"))).GetAwaiter().GetResult();

        Check.Ok(repository.RemoveAsync(stored.Id).GetAwaiter().GetResult(), "删除已存在的项目应返回 true");
        Check.Ok(!repository.RemoveAsync(stored.Id).GetAwaiter().GetResult(), "重复删除应返回 false");
        Check.Equal(0, repository.LoadAsync().GetAwaiter().GetResult().Projects.Count, "删除后清单应为空");
    }

    private static void CorruptFileIsQuarantinedThenUsable()
    {
        using var workspace = new TempWorkspace("storage-corrupt");
        var file = workspace.Path2("projects.json");
        File.WriteAllText(file, "{ 这不是 JSON ");

        var repository = new JsonProjectRepository(file);
        var loaded = repository.LoadAsync().GetAwaiter().GetResult();

        Check.Equal(0, loaded.Projects.Count, "损坏清单应降级为空列表");
        Check.NotNull(loaded.Warning, "损坏清单必须带出警告（不能静默丢失）");

        var quarantined = Directory.GetFiles(workspace.Root, "projects.json.corrupt-*");
        Check.Equal(1, quarantined.Length, "损坏文件应被改名留存，而不是直接覆盖");
        Check.Ok(!File.Exists(file), "原文件应已移走");

        // 隔离后应能正常写入：用户不会因为一次损坏就永久失去清单
        repository.UpsertAsync(NewProject("AfterQuarantine", workspace.Path2("AfterQuarantine")))
            .GetAwaiter().GetResult();
        Check.Equal(1, repository.LoadAsync().GetAwaiter().GetResult().Projects.Count, "隔离后应可正常写入并读回");
    }

    private static void TempFileNotLeftBehind()
    {
        using var workspace = new TempWorkspace("storage-atomic");
        var repository = new JsonProjectRepository(workspace.Path2("projects.json"));
        repository.UpsertAsync(NewProject("Atomic", workspace.Path2("Atomic"))).GetAwaiter().GetResult();

        var temp = Directory.GetFiles(workspace.Root, "*.tmp");
        Check.Equal(0, temp.Length, "原子写完成后不应残留 .tmp 文件");
    }

    private static void SettingsRoundTrip()
    {
        using var workspace = new TempWorkspace("settings-roundtrip");
        var store = new JsonSettingsStore(workspace.Path2("settings.json"));

        var (defaults, defaultWarning) = store.LoadAsync().GetAwaiter().GetResult();
        Check.Ok(defaultWarning is null, "首次读取不应有警告");
        Check.Equal(string.Empty, defaults.DefaultTargetDirectory, "默认路径应为空");
        Check.Equal(ThemePreference.System, defaults.Theme, "默认主题应跟随系统");

        store.SaveAsync(new AppSettings
        {
            DefaultTargetDirectory = @"D:\Projects",
            DefaultTemplateName = "单体桌面工具",
            OpenFolderAfterGeneration = false,
            Theme = ThemePreference.Dark,
        }).GetAwaiter().GetResult();

        var (loaded, warning) = store.LoadAsync().GetAwaiter().GetResult();
        Check.Ok(warning is null, "正常设置不应有警告");
        Check.Equal(@"D:\Projects", loaded.DefaultTargetDirectory, "路径应保持");
        Check.Equal(ThemePreference.Dark, loaded.Theme, "主题应保持");
        Check.Ok(!loaded.OpenFolderAfterGeneration, "开关应保持");
    }

    private static void BrokenSettingsFallBackToDefaults()
    {
        using var workspace = new TempWorkspace("settings-broken");
        var file = workspace.Path2("settings.json");
        File.WriteAllText(file, "not json at all");

        var store = new JsonSettingsStore(file);
        var (settings, warning) = store.LoadAsync().GetAwaiter().GetResult();

        Check.NotNull(warning, "损坏设置应带出警告");
        Check.Equal(ThemePreference.System, settings.Theme, "损坏设置应回退默认值");
    }

    private static StoredProject NewProject(string name, string path, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.Now;
        return new StoredProject
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = name,
            Path = path,
            TemplateName = "轻量工具",
            CreatedAt = now,
            LastOpenedAt = now,
            Generated = true,
        };
    }
}
