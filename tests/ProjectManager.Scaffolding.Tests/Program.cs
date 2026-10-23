namespace ProjectManager.Scaffolding.Tests;

/// <summary>
/// 引擎测试入口（WP1.10）。退出码：0 全绿 / 1 有断言失败 / 2 意外异常。
/// </summary>
/// <remarks>
/// 刻意不引入 xunit / nunit：本仓库要求在<b>离线</b>环境可构建可运行，
/// 而测试框架需要 NuGet 还原。控制台断言器虽朴素，但能覆盖全部不变量与等价性比对，
/// 且"退出码即结果"可直接接 CI。
/// </remarks>
public static class Program
{
    public static int Main()
    {
        Console.WriteLine("ProjectManager.Scaffolding 引擎测试");
        Console.WriteLine($"仓库根：{FixtureLocator.RepositoryRoot}");

        try
        {
            var templates = BaselineEquivalence.Run();
            TemplateValidationTests.Run();
            InvariantTests.Run(templates);
            BoundaryTests.Run(templates);
            PerformanceTests.Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n意外异常：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 2;
        }

        return Check.Report();
    }
}
