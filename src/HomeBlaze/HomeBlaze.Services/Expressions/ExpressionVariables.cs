using System.Diagnostics.CodeAnalysis;

namespace HomeBlaze.Services.Expressions;

/// <summary>
/// The names of the variables that a host of an expression binds. They are the same for every host, so that one map
/// serves every widget that applies it.
/// </summary>
public static class ExpressionVariables
{
    /// <summary>The value the host shows, such as the value of the property at the path of a widget.</summary>
    public const string Value = "value";

    /// <summary>The subject of that value: the owner of its property, or the subject that a path to a subject leads to.</summary>
    public const string Subject = "subject";

    /// <summary>Gets whether the first segment of the path is a variable, and which one.</summary>
    public static bool TryGetVariable(string path, [NotNullWhen(true)] out string? variable)
    {
        var end = path.AsSpan().IndexOfAny('/', '[');
        var first = end < 0 ? path.AsSpan() : path.AsSpan(0, end);
        variable = first is Value ? Value : first is Subject ? Subject : null;
        return variable is not null;
    }
}
