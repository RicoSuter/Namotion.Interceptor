using System.Runtime.CompilerServices;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Authorization.Tests;

/// <summary>
/// Waits for a status, warning or <see cref="Role.UsedByAgents"/> count that <see cref="AuthorizationService"/> writes. The
/// service writes them on the thread that built the snapshot, which can be a background rebuild started by an earlier change,
/// so a policy call can return before they are written, and a pass that started earlier can still write an older value.
/// </summary>
internal static class StatusAssert
{
    public static Task EqualAsync(
        string? expected,
        Func<string?> actual,
        [CallerArgumentExpression(nameof(actual))] string expression = "") =>
        WaitAsync(value => value == expected, $"be {Format(expected)}", actual, expression);

    public static Task EqualAsync(
        int expected,
        Func<int> actual,
        [CallerArgumentExpression(nameof(actual))] string expression = "") =>
        AsyncTestHelpers.WaitUntilAsync(
            () => actual() == expected,
            () => $"'{expression}' should be {expected}, but is {actual()}.");

    public static Task ContainsAsync(
        string expectedSubstring,
        Func<string?> actual,
        [CallerArgumentExpression(nameof(actual))] string expression = "") =>
        WaitAsync(
            value => value?.Contains(expectedSubstring, StringComparison.Ordinal) == true,
            $"contain {Format(expectedSubstring)}",
            actual,
            expression);

    public static Task NotNullAsync(
        Func<string?> actual,
        [CallerArgumentExpression(nameof(actual))] string expression = "") =>
        WaitAsync(value => value is not null, "be set", actual, expression);

    private static Task WaitAsync(Func<string?, bool> condition, string expectation, Func<string?> actual, string expression) =>
        AsyncTestHelpers.WaitUntilAsync(
            () => condition(actual()),
            () => $"'{expression}' should {expectation}, but is {Format(actual())}.");

    private static string Format(string? value) => value is null ? "null" : $"'{value}'";
}
