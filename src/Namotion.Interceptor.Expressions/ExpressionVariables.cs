using System.Diagnostics.CodeAnalysis;

namespace Namotion.Interceptor.Expressions;

/// <summary>
/// The names of the variables that a host of an expression binds. They are the same for every host, so that an expression written once, such as a condition shared by several hosts, means the same in each.
/// </summary>
public static class ExpressionVariables
{
    /// <summary>The value the host evaluates the expression for, such as the value of a property it shows.</summary>
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
