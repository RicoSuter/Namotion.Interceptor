using System.Collections.Immutable;
using HomeBlaze.Services.Expressions;

namespace HomeBlaze.Services.Tests.Expressions;

/// <summary>Resolves a path to the value the dictionary has for it; the pipe "upper" writes the text in upper case.</summary>
internal readonly struct TestExpressionContext(Dictionary<string, object?> values) : IExpressionContext
{
    public ExpressionValue ResolvePath(string path) =>
        values.TryGetValue(path, out var value) ? ExpressionValue.FromValue(value) : ExpressionValue.Missing;

    public ExpressionValue ApplyPipe(string name, ImmutableArray<string> arguments, ExpressionValue input) =>
        ExpressionValue.FromText($"{input.Value}".ToUpperInvariant() + string.Concat(arguments));
}
