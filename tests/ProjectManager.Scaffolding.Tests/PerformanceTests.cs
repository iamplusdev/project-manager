using System.Diagnostics;
using ProjectManager.Scaffolding.Models;
using ProjectManager.Scaffolding.Scaffolding;

namespace ProjectManager.Scaffolding.Tests;

/// <summary>
/// 性能预算自测（方案 4.6）。出口门禁 G1 要求"达标或记录偏差"。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不直接断言"落盘 500 目录 &lt; 800 ms"</b>：本机实测同一负载（500 目录 + 500 个
/// <c>.gitkeep</c>）用<b>裸循环</b>（不经过引擎）也要 870–999 ms——预算是按理想 SSD 估的，
/// 在本机物理不可达。把不可达的数字当硬断言，只会让测试变成噪音，进而被人为调大阈值。
/// </para>
/// <para>
/// 因此采用<b>双重判定</b>：
/// <list type="number">
/// <item><b>诊断</b>：把引擎耗时与预算、与「同规模裸 IO 下限」三者并列打印，偏差写清楚归因；</item>
/// <item><b>硬断言</b>：引擎耗时不显著高于裸 IO 下限（≤ 2.5×）。这才是我们真正能控制的东西——
/// 算法没有额外开销；分母是环境，不是代码。</item>
/// </list>
/// </para>
/// </remarks>
public static class PerformanceTests
{
    private const int TreeBudgetMs = 20;
    private const int WriteBudgetMs = 800;
    private const double OverheadFactorCeiling = 2.5;
    private const int Samples = 3;

    public static void Run()
    {
        Check.Section("性能预算（4.6）：树展开 1000 节点 / 落盘 500 目录");

        var treeTemplate = BuildLargeTemplate(rows: 40, columns: 24);
        Check.Equal(1000, TreeBuilder.FlattenRelative(treeTemplate.Structure).Count, "合成模板应为 1000 个节点");

        TreeBuilder.FlattenRelative(TreeBuilder.Build(treeTemplate, Array.Empty<string>())); // 预热
        var expandSamples = new List<long>();
        for (var index = 0; index < Samples; index++)
        {
            expandSamples.Add(
                Measure(() => TreeBuilder.FlattenRelative(TreeBuilder.Build(treeTemplate, Array.Empty<string>()))));
        }

        var expandBest = expandSamples.Min();
        Check.That(
            expandBest < TreeBudgetMs,
            $"树展开最优 {expandBest} ms 超预算 {TreeBudgetMs} ms（样本：{Format(expandSamples)}）");
        Console.WriteLine(
            $"  树展开 1000 节点：最优 {expandBest} ms（预算 <{TreeBudgetMs} ms，样本 {Format(expandSamples)}）");

        var writeTemplate = BuildLargeTemplate(rows: 20, columns: 24);
        Check.Equal(500, TreeBuilder.FlattenRelative(writeTemplate.Structure).Count, "落盘用例应为 500 个目录");

        var target = FixtureLocator.CreateTempDirectory("perf");
        try
        {
            var generator = new ScaffoldGenerator();
            var request = new GenerationRequest
            {
                ProjectName = "PerfProject",
                Description = "性能预算自测",
                TargetDirectory = target,
            };

            // 预热：首次含 JIT、程序集加载与首建目录的额外开销
            generator.Generate(request, writeTemplate);

            var writeSamples = new List<long>();
            var directories = 0;
            for (var index = 0; index < Samples; index++)
            {
                FixtureLocator.TryDelete(Path.Combine(target, "PerfProject"));
                var stopwatch = Stopwatch.StartNew();
                var result = generator.Generate(request, writeTemplate);
                stopwatch.Stop();
                directories = result.DirectoriesCreated.Count(item => item != ".");
                writeSamples.Add(stopwatch.ElapsedMilliseconds);
            }

            var writeBest = writeSamples.Min();
            var ioFloor = MeasureIoFloor(target, rows: 20, columns: 24);

            Console.WriteLine(
                $"  落盘 {directories} 个目录 + {directories} 个占位文件：最优 {writeBest} ms"
                + $"（样本 {Format(writeSamples)}；同规模裸 IO 下限 {ioFloor} ms；预算 <{WriteBudgetMs} ms）");

            if (ioFloor > WriteBudgetMs)
            {
                Console.WriteLine(
                    $"  ⚠ 预算偏差：同规模裸 IO 已需 {ioFloor} ms > 预算 {WriteBudgetMs} ms，"
                    + "该项在本机物理不可达（环境文件系统/杀毒扫描成本），非引擎开销。");
            }
            else if (writeBest > WriteBudgetMs)
            {
                Console.WriteLine($"  ⚠ 预算偏差 +{writeBest - WriteBudgetMs} ms，裸 IO 下限 {ioFloor} ms。");
            }

            Check.That(
                writeBest <= ioFloor * OverheadFactorCeiling,
                $"引擎落盘 {writeBest} ms 超过裸 IO 下限 {ioFloor} ms 的 {OverheadFactorCeiling} 倍，"
                + $"存在额外开销（样本：{Format(writeSamples)}）");
        }
        finally
        {
            FixtureLocator.TryDelete(target);
        }
    }

    /// <summary>
    /// 同规模「裸 IO 下限」：只用文件系统抽象做 500 次建目录 + 500 次写空文件，不经过任何引擎逻辑。
    /// 拿来当分母——引擎再快也不可能低于它，因此比较才有意义。
    /// </summary>
    private static long MeasureIoFloor(string target, int rows, int columns)
    {
        var fs = ProjectManager.Scaffolding.IO.PhysicalFileSystem.Instance;
        var floorRoot = Path.Combine(target, "__io_floor__");

        long Run()
        {
            FixtureLocator.TryDelete(floorRoot);
            var stopwatch = Stopwatch.StartNew();
            for (var row = 0; row < rows; row++)
            {
                var rowPath = Path.Combine(floorRoot, $"layer-{row}");
                fs.CreateDirectory(rowPath);
                for (var column = 0; column < columns; column++)
                {
                    var leaf = Path.Combine(rowPath, $"pkg-{column}");
                    fs.CreateDirectory(leaf);
                    fs.WriteAllText(Path.Combine(leaf, ".gitkeep"), string.Empty);
                }
            }

            stopwatch.Stop();
            return stopwatch.ElapsedMilliseconds;
        }

        Run(); // 预热
        var best = Math.Min(Run(), Run());
        FixtureLocator.TryDelete(floorRoot);
        return best;
    }

    private static string Format(IReadOnlyList<long> samples) => string.Join("/", samples) + " ms";

    private static long Measure(Action action)
    {
        var stopwatch = Stopwatch.StartNew();
        action();
        stopwatch.Stop();
        return stopwatch.ElapsedMilliseconds;
    }

    /// <summary>合成 rows×(columns+1) 个节点的模板：每行一个父目录 + columns 个叶子目录。</summary>
    private static Template BuildLargeTemplate(int rows, int columns)
    {
        var root = new DirNode(string.Empty);
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var row = 0; row < rows; row++)
        {
            var rowName = $"layer-{row}";
            var rowNode = root.FindOrAdd(rowName);
            descriptions[rowName] = $"第 {row} 层";

            for (var column = 0; column < columns; column++)
            {
                var leafName = $"pkg-{column}";
                rowNode.FindOrAdd(leafName);
                descriptions[$"{rowName}/{leafName}"] = $"第 {row} 层的第 {column} 个包";
            }
        }

        return new Template(
            "性能模板",
            root,
            Array.Empty<ModuleOption>(),
            descriptions,
            "1. 编译。",
            new[] { "bin/", "obj/" });
    }
}
