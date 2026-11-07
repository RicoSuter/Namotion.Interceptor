using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Namotion.Interceptor.Expressions;

/// <summary>
/// The names of the variables that a host of an expression binds, unless it declares its own when it parses the
/// expression. They are the same for every such host, so that an expression written once, such as a condition shared by
/// several hosts, means the same in each.
/// </summary>
public static class ExpressionVariables
{
    /// <summary>The value the host evaluates the expression for, such as the value of a property it shows.</summary>
    public const string Value = "value";

    /// <summary>The subject of that value: the owner of its property, or the subject that a path to a subject leads to.</summary>
    public const string Subject = "subject";

    internal static readonly ImmutableArray<string> Default = [Value, Subject];

    /// <summary>
    /// Gets whether the first segment of the path is <see cref="Value"/> or <see cref="Subject"/>, and which one. For a path
    /// of an expression parsed with variables the host declares, use <see cref="ParsedExpression.TryGetVariable"/>.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="pathSeparator">The character between the segments of the path.</param>
    /// <param name="variable">The variable, or null.</param>
    public static bool TryGetVariable(string path, char pathSeparator, [NotNullWhen(true)] out string? variable) =>
        TryGetVariable(path, pathSeparator, Default, out variable, out _);

    /// <summary>
    /// Gets whether the first segment of the path is <see cref="Value"/> or <see cref="Subject"/>, and which one, with the
    /// rest of the path. For a path of an expression parsed with variables the host declares, use
    /// <see cref="ParsedExpression.TryGetVariable"/>.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="pathSeparator">The character between the segments of the path.</param>
    /// <param name="variable">The variable, or null.</param>
    /// <param name="memberPath">The path after the variable and its separator, or null for the variable alone or a path without one.</param>
    public static bool TryGetVariable(
        string path,
        char pathSeparator,
        [NotNullWhen(true)] out string? variable,
        out string? memberPath) =>
        TryGetVariable(path, pathSeparator, Default, out variable, out memberPath);

    internal static bool TryGetVariable(
        string path,
        char pathSeparator,
        ImmutableArray<string> variables,
        [NotNullWhen(true)] out string? variable,
        out string? memberPath)
    {
        var end = path.AsSpan().IndexOfAny(pathSeparator, '[');
        var first = end < 0 ? path.AsSpan() : path.AsSpan(0, end);
        foreach (var name in variables)
        {
            if (first.SequenceEqual(name))
            {
                variable = name;
                if (end < 0)
                {
                    memberPath = null;
                }
                else
                {
                    // An index directly after the variable stays part of the member path.
                    memberPath = path[end] == pathSeparator ? path[(end + 1)..] : path[end..];
                }

                return true;
            }
        }

        variable = null;
        memberPath = null;
        return false;
    }

    // A variable is a word that the parser does not read as a keyword, a number or a path with more than one segment.
    internal static void Validate(ImmutableArray<string> variables, char pathSeparator)
    {
        if (variables.IsDefault)
        {
            throw new ArgumentException("The variables are not initialized.", nameof(variables));
        }

        for (var index = 0; index < variables.Length; index++)
        {
            var name = variables[index];
            if (!IsWord(name) || name.Contains(pathSeparator) || ExpressionParser.IsKeyword(name))
            {
                throw new ArgumentException($"The variable '{name}' is not a word that an expression can hold.", nameof(variables));
            }

            if (variables.AsSpan()[(index + 1)..].Contains(name))
            {
                throw new ArgumentException($"The variable '{name}' is declared twice.", nameof(variables));
            }
        }
    }

    private static bool IsWord(string name) =>
        !string.IsNullOrEmpty(name) &&
        char.IsLetter(name[0]) &&
        name.All(character => char.IsLetterOrDigit(character) || character == '_');
}
