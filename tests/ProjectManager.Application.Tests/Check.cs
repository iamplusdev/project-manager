namespace ProjectManager.Application.Tests;

/// <summary>极简断言器：无 NuGet 依赖，失败即记录并继续跑完（一次看到全部问题）。</summary>
public static class Check
{
    private static readonly List<string> Failures = new();

    public static int Passed { get; private set; }

    public static void Ok(bool condition, string message)
    {
        if (condition)
        {
            Passed++;
            return;
        }

        Failures.Add(message);
    }

    public static void That(bool condition, string message) => Ok(condition, message);

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual))
        {
            Passed++;
            return;
        }

        Failures.Add($"{message}｜期望：{Format(expected)}｜实际：{Format(actual)}");
    }

    public static void NotNull(object? value, string message)
    {
        if (value is not null)
        {
            Passed++;
            return;
        }

        Failures.Add($"{message}｜期望非空，实际为 null");
    }

    public static void Section(string title) => Console.WriteLine($"\n── {title}");

    public static int Report()
    {
        Console.WriteLine();
        if (Failures.Count == 0)
        {
            Console.WriteLine($"全部通过：{Passed} 项断言");
            return 0;
        }

        Console.WriteLine($"失败 {Failures.Count} 项（通过 {Passed} 项）：");
        foreach (var failure in Failures)
        {
            Console.WriteLine($"  ✗ {failure}");
        }

        return 1;
    }

    private static string Format<T>(T value) => value switch
    {
        null => "<null>",
        string text => $"\"{text}\"",
        _ => value.ToString() ?? "<null>",
    };
}
