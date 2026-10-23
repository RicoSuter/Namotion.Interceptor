using System.Collections.Immutable;
using HomeBlaze.Services.Expressions;

namespace HomeBlaze.Services.Tests.Expressions;

/// <summary>
/// Resolves a path, a variable included, to the value the dictionary has for its text; the pipe "upper" writes the text in
/// upper case, and a result is written with its plain text.
/// </summary>
internal readonly struct TestExpressionContext(Dictionary<string, object?> values) : IExpressionContext
{
    public ExpressionValue ResolvePath(string path) =>
        values.TryGetValue(path, out var value) ? ExpressionValue.FromValue(value) : ExpressionValue.Missing;

    public ExpressionValue ResolveVariable(string variable, string? memberPath) =>
        ResolvePath(memberPath is null ? variable : $"{variable}/{memberPath}");

    public ExpressionValue ApplyPipe(string name, ImmutableArray<string> arguments, ExpressionValue input) =>
        ExpressionValue.FromText($"{input.Value}".ToUpperInvariant() + string.Concat(arguments));

    public string ToText(ExpressionValue result) => result.IsText ? (string)result.Value! : $"{result.Value}";
}
