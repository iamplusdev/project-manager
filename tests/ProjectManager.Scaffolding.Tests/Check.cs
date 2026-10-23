namespace ProjectManager.Scaffolding.Tests;

/// <summary>极简断言器：无 NuGet 依赖，失败即记录并继续跑完（一次看到全部问题）。</summary>
public static class Check
{
    private static readonly List<string> Failures = new();

    public static int Passed { get; private set; }

    public static IReadOnlyList<string> AllFailures => Failures;

    public static bool Ok(bool condition, string message)
    {
        if (condition)
        {
            Passed++;
            return true;
        }

        Failures.Add(message);
        return false;
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

    public static void SequenceEqual(IEnumerable<string> expected, IEnumerable<string> actual, string message)
    {
        var left = expected.ToList();
        var right = actual.ToList();
        if (left.Count == right.Count && left.Zip(right).All(pair => pair.First == pair.Second))
        {
            Passed++;
            return;
        }

        var missing = left.Except(right).Take(5).ToList();
        var extra = right.Except(left).Take(5).ToList();
        Failures.Add(
            $"{message}｜缺失：{string.Join(", ", missing)}｜多余：{string.Join(", ", extra)}｜计数 {left.Count} vs {right.Count}");
    }

    public static void Throws<TException>(Action action, string message, Func<TException, bool>? predicate = null)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            if (predicate is null || predicate(ex))
            {
                Passed++;
                return;
            }

            Failures.Add($"{message}｜异常类型符合但内容不符：{ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            Failures.Add($"{message}｜期望 {typeof(TException).Name}，实际 {ex.GetType().Name}：{ex.Message}");
            return;
        }

        Failures.Add($"{message}｜期望抛出 {typeof(TException).Name}，但未抛出");
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
