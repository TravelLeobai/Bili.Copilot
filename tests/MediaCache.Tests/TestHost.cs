// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;

namespace MediaCache.Tests;

/// <summary>
/// 极简断言与测试登记（不引 xunit，保持零依赖）.
/// </summary>
internal static class TestHost
{
    private static readonly List<(string Name, Func<Task> Body)> Tests = [];

    public static int Failures { get; private set; }

    public static void Add(string name, Func<Task> body) => Tests.Add((name, body));

    public static void Add(string name, Action body) => Tests.Add((name, () =>
    {
        body();
        return Task.CompletedTask;
    }));

    public static async Task<int> RunAsync(string? filter)
    {
        var passed = 0;
        var failed = 0;
        foreach (var (name, body) in Tests)
        {
            if (!string.IsNullOrEmpty(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                await body();
                passed++;
                Console.WriteLine($"  PASS  {name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  FAIL  {name}");
                Console.WriteLine("        " + ex.Message.Replace("\n", "\n        ", StringComparison.Ordinal));
                if (ex is not AssertionException)
                {
                    Console.WriteLine("        " + ex.GetType().Name);
                }
            }
        }

        Failures = failed;
        Console.WriteLine();
        Console.WriteLine($"合计：{passed} 通过 / {failed} 失败 / {passed + failed} 项");
        return failed == 0 ? 0 : 1;
    }
}

internal sealed class AssertionException(string message) : Exception(message);

internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new AssertionException(message);
        }
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException(string.Create(CultureInfo.InvariantCulture, $"{message}：期望 [{expected}]，实际 [{actual}]"));
        }
    }

    public static void NotNull(object? value, string message)
    {
        if (value is null)
        {
            throw new AssertionException(message + "：值为 null");
        }
    }

    public static void Sequence(IReadOnlyList<long> expected, IReadOnlyList<long> actual, string message)
    {
        if (expected.Count != actual.Count || expected.Where((t, i) => t != actual[i]).Any())
        {
            throw new AssertionException(
                string.Create(CultureInfo.InvariantCulture, $"{message}：期望 [{string.Join(",", expected)}]，实际 [{string.Join(",", actual)}]"));
        }
    }
}
