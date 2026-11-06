using System.Diagnostics.CodeAnalysis;

namespace Namotion.Interceptor.Expressions;

/// <summary>
/// The names of the variables that a host of an expression binds. They are the same for every host, so that an expression written once, such as a condition shared by several hosts, means the same in each. A host binds the variables it has a meaning for and refuses an expression that uses another one.
/// </summary>
public static class ExpressionVariables
{
    /// <summary>The value the host evaluates the expression for, such as the value of a property it shows.</summary>
    public const string Value = "value";

    /// <summary>The subject of that value: the owner of its property, the subject that a path to a subject leads to, or the publisher of an event.</summary>
    public const string Subject = "subject";

    /// <summary>The current instant, a <see cref="DateTimeOffset"/> in UTC, for a host with a clock.</summary>
    public const string Now = "now";

    /// <summary>The local time of day, a <see cref="TimeSpan"/> in the time zone of the host, for a host with a clock.</summary>
    public const string Time = "time";

    /// <summary>The event the host evaluates the expression for, a record whose members follow the variable, such as <c>event/State</c>.</summary>
    public const string Event = "event";

    /// <summary>Gets whether the first segment of the path is a variable, and which one.</summary>
    /// <param name="path">The path.</param>
    /// <param name="pathSeparator">The character between the segments of the path.</param>
    /// <param name="variable">The variable, or null.</param>
    public static bool TryGetVariable(string path, char pathSeparator, [NotNullWhen(true)] out string? variable)
    {
        var end = path.AsSpan().IndexOfAny(pathSeparator, '[');
        var first = end < 0 ? path.AsSpan() : path.AsSpan(0, end);
        variable = first switch
        {
            Value => Value,
            Subject => Subject,
            Now => Now,
            Time => Time,
            Event => Event,
            _ => null
        };

        return variable is not null;
    }

    /// <summary>Gets whether the first segment of the path is a variable, and which one, with the rest of the path.</summary>
    /// <param name="path">The path.</param>
    /// <param name="pathSeparator">The character between the segments of the path.</param>
    /// <param name="variable">The variable, or null.</param>
    /// <param name="memberPath">The path after the variable and its separator, or null for the variable alone or a path without one.</param>
    public static bool TryGetVariable(
        string path,
        char pathSeparator,
        [NotNullWhen(true)] out string? variable,
        out string? memberPath)
    {
        if (!TryGetVariable(path, pathSeparator, out variable))
        {
            memberPath = null;
            return false;
        }

        if (path.Length == variable.Length)
        {
            memberPath = null;
        }
        else
        {
            // An index directly after the variable stays part of the member path.
            memberPath = path[variable.Length] == pathSeparator ? path[(variable.Length + 1)..] : path[variable.Length..];
        }

        return true;
    }
}
