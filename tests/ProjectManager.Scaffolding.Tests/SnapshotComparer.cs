using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProjectManager.Scaffolding.Scaffolding;

namespace ProjectManager.Scaffolding.Tests;

/// <summary>基线快照中的一条记录（与 <c>tests/fixtures/tools/forge_baseline.py</c> 的产出格式一致）。</summary>
public sealed record BaselineEntry(
    string Path,
    string Type,
    string? Sha256,
    bool IsPlaceholder,
    bool Ignored);

/// <summary>
/// 快照比对器（WP1.10）。差异分四类输出，不做"只报一个总数"的敷衍：
/// <list type="bullet">
/// <item><b>Missing</b>：基线有、本次没有——结构漏生成；</item>
/// <item><b>Extra</b>：本次多出来——结构多生成或残留；</item>
/// <item><b>ContentDiff</b>：路径相同但内容不同；</item>
/// <item><b>FlagDiff</b>：<c>isPlaceholder</c> / <c>ignored</c> 标记不一致。</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>行尾归一化</b>：基线是 Forge 在 Windows 上产出的 CRLF，本项目统一写 LF，
/// 因此内容比较在 <c>CRLF→LF</c> 归一后进行；同时统计 <c>EolOnly</c> 数量，
/// 让"唯一差异就是行尾"这件事被<b>度量</b>而不是被掩盖。
/// </para>
/// <para>
/// <b>元数据</b>：<c>.pm-scaffold.json</c> 是 PM 的增量产物（G10），基线中不存在。
/// 比对时排除，但要求它确实存在且可解析——避免"排除了但其实没生成"的假通过。
/// </para>
/// </remarks>
public static class SnapshotComparer
{
    public const string MetadataFileName = ".pm-scaffold.json";

    private static readonly Regex TimestampedBackup =
        new(@"\.\d{8}-\d{6}\.bak(-\d+)?$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<BaselineEntry> LoadBaseline(string caseDirectory)
    {
        var treePath = Path.Combine(caseDirectory, "tree.json");
        using var document = JsonDocument.Parse(File.ReadAllText(treePath));

        var entries = new List<BaselineEntry>();
        foreach (var element in document.RootElement.GetProperty("entries").EnumerateArray())
        {
            entries.Add(new BaselineEntry(
                element.GetProperty("path").GetString() ?? string.Empty,
                element.GetProperty("type").GetString() ?? string.Empty,
                element.TryGetProperty("sha256", out var sha) && sha.ValueKind == JsonValueKind.String
                    ? sha.GetString()
                    : null,
                element.GetProperty("isPlaceholder").GetBoolean(),
                element.GetProperty("ignored").GetBoolean()));
        }

        return entries;
    }

    public static ComparisonReport Compare(
        string caseDirectory,
        string generatedProjectDirectory,
        IReadOnlyList<string> ignoredPrefixes)
    {
        var report = new ComparisonReport();
        var baseline = LoadBaseline(caseDirectory);

        // 1) 基线自校验：fixtures/files 的字节必须能对上 tree.json 记录的 sha256
        foreach (var entry in baseline.Where(item => item.Type == "file"))
        {
            var fixturePath = Path.Combine(caseDirectory, "files", entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fixturePath))
            {
                report.Missing.Add($"{entry.Path}（基线固件缺失）");
                continue;
            }

            var raw = File.ReadAllBytes(fixturePath);
            var hash = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
            var normalizedHash = Convert.ToHexString(SHA256.HashData(NormalizeEol(raw))).ToLowerInvariant();
            if (!string.Equals(hash, entry.Sha256, StringComparison.Ordinal)
                && !string.Equals(normalizedHash, entry.Sha256, StringComparison.Ordinal))
            {
                report.ContentDiff.Add($"{entry.Path}（基线固件与 tree.json 不一致，快照可能被改动）");
            }
        }

        // 2) 采集本次产物
        var actual = CollectActual(generatedProjectDirectory, ignoredPrefixes, report);

        // 3) 路径集合比对
        var baselineFiles = baseline.Where(item => item.Type == "file").ToDictionary(item => item.Path, StringComparer.Ordinal);
        var baselineDirs = baseline.Where(item => item.Type == "dir").Select(item => item.Path).ToHashSet(StringComparer.Ordinal);
        var actualFiles = actual.Where(item => item.Type == "file").ToDictionary(item => item.Path, StringComparer.Ordinal);
        var actualDirs = actual.Where(item => item.Type == "dir").Select(item => item.Path).ToHashSet(StringComparer.Ordinal);

        foreach (var path in baselineDirs.Except(actualDirs))
        {
            report.Missing.Add(path + "/");
        }

        foreach (var path in actualDirs.Except(baselineDirs))
        {
            report.Extra.Add(path + "/");
        }

        foreach (var path in baselineFiles.Keys.Except(actualFiles.Keys))
        {
            report.Missing.Add(path);
        }

        foreach (var path in actualFiles.Keys.Except(baselineFiles.Keys))
        {
            report.Extra.Add(path);
        }

        // 4) 内容与标记比对
        foreach (var (path, entry) in baselineFiles)
        {
            if (!actualFiles.TryGetValue(path, out var produced))
            {
                continue;
            }

            if (entry.IsPlaceholder != produced.IsPlaceholder)
            {
                report.FlagDiff.Add($"{path}：isPlaceholder {entry.IsPlaceholder} → {produced.IsPlaceholder}");
            }

            if (entry.Ignored != produced.Ignored)
            {
                report.FlagDiff.Add($"{path}：ignored {entry.Ignored} → {produced.Ignored}");
            }

            var baselineBytes = File.ReadAllBytes(
                Path.Combine(caseDirectory, "files", path.Replace('/', Path.DirectorySeparatorChar)));
            var actualBytes = produced.Bytes;

            if (baselineBytes.AsSpan().SequenceEqual(actualBytes))
            {
                continue;
            }

            if (NormalizeEol(baselineBytes).AsSpan().SequenceEqual(NormalizeEol(actualBytes)))
            {
                report.EolOnlyFiles.Add(path);
                continue;
            }

            report.ContentDiff.Add($"{path}｜{DescribeDifference(baselineBytes, actualBytes)}");
        }

        return report;
    }

    private static List<ActualEntry> CollectActual(
        string root,
        IReadOnlyList<string> ignoredPrefixes,
        ComparisonReport report)
    {
        var entries = new List<ActualEntry>();
        var metadataSeen = false;

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            var relative = ToRelative(root, directory);
            entries.Add(new ActualEntry(NormalizePath(relative), "dir", Array.Empty<byte>(), false, IsIgnored(relative, ignoredPrefixes)));
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = ToRelative(root, file);
            if (relative == MetadataFileName)
            {
                metadataSeen = true;
                VerifyMetadata(file, report);
                continue;
            }

            entries.Add(new ActualEntry(
                NormalizePath(relative),
                "file",
                File.ReadAllBytes(file),
                Path.GetFileName(file) == TreeBuilder.PlaceholderFileName,
                IsIgnored(Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? string.Empty, ignoredPrefixes)));
        }

        if (!metadataSeen)
        {
            report.Missing.Add($"{MetadataFileName}（G10 元数据未生成）");
        }

        return entries;
    }

    private static void VerifyMetadata(string path, ComparisonReport report)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var required in new[] { "engine", "engineVersion", "template", "templateHash", "projectName", "generatedAt" })
            {
                if (!document.RootElement.TryGetProperty(required, out _))
                {
                    report.ContentDiff.Add($"{MetadataFileName} 缺少字段 {required}");
                }
            }
        }
        catch (JsonException ex)
        {
            report.ContentDiff.Add($"{MetadataFileName} 不是合法 JSON：{ex.Message}");
        }
    }

    private static bool IsIgnored(string relativeDirectory, IReadOnlyList<string> prefixes)
        => relativeDirectory.Length > 0 && TreeBuilder.MatchesIgnoredDirectory(relativeDirectory, prefixes);

    private static string ToRelative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string NormalizePath(string relative) => TimestampedBackup.Replace(relative, ".TIMESTAMP.bak");

    /// <summary>CRLF / 单独 CR 一律归一为 LF。</summary>
    private static byte[] NormalizeEol(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Replace('\r', '\n');
        return Encoding.UTF8.GetBytes(text);
    }

    private static string DescribeDifference(byte[] baseline, byte[] actual)
    {
        var left = Encoding.UTF8.GetString(NormalizeEol(baseline)).Split('\n');
        var right = Encoding.UTF8.GetString(NormalizeEol(actual)).Split('\n');

        var limit = Math.Min(left.Length, right.Length);
        for (var index = 0; index < limit; index++)
        {
            if (left[index] != right[index])
            {
                return $"首处差异在第 {index + 1} 行｜基线「{Truncate(left[index])}」｜生成「{Truncate(right[index])}」";
            }
        }

        return $"行数不同｜基线 {left.Length} 行｜生成 {right.Length} 行";
    }

    private static string Truncate(string value) => value.Length <= 60 ? value : value[..60] + "…";

    private sealed record ActualEntry(string Path, string Type, byte[] Bytes, bool IsPlaceholder, bool Ignored);
}

/// <summary>比对结果。</summary>
public sealed class ComparisonReport
{
    public List<string> Missing { get; } = new();

    public List<string> Extra { get; } = new();

    public List<string> ContentDiff { get; } = new();

    public List<string> FlagDiff { get; } = new();

    /// <summary>仅行尾不同（CRLF vs LF）的文件——唯一有意差异，按通过处理但计数留痕。</summary>
    public List<string> EolOnlyFiles { get; } = new();

    public bool Passed => Missing.Count == 0 && Extra.Count == 0 && ContentDiff.Count == 0 && FlagDiff.Count == 0;

    public string Describe()
    {
        var lines = new List<string>();
        if (Missing.Count > 0)
        {
            lines.Add($"  缺失({Missing.Count})：{string.Join(", ", Missing.Take(8))}");
        }

        if (Extra.Count > 0)
        {
            lines.Add($"  多余({Extra.Count})：{string.Join(", ", Extra.Take(8))}");
        }

        if (ContentDiff.Count > 0)
        {
            lines.Add($"  内容不一致({ContentDiff.Count})：{string.Join(" ; ", ContentDiff.Take(4))}");
        }

        if (FlagDiff.Count > 0)
        {
            lines.Add($"  标记不一致({FlagDiff.Count})：{string.Join(" ; ", FlagDiff.Take(4))}");
        }

        return lines.Count == 0 ? "  无差异" : string.Join(Environment.NewLine, lines);
    }
}
