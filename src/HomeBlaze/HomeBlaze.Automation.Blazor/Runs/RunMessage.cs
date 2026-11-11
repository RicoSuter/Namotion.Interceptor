using System.Text.RegularExpressions;

namespace HomeBlaze.Automation.Blazor.Runs;

/// <summary>
/// An action of a run as its message describes it: the operation or property name, the absolute path of its subject, and
/// the arguments, the written value or the target state. A part that matches no description, or whose path is relative to
/// the rule, has only <see cref="Detail"/>.
/// </summary>
internal sealed record RunMessageAction(string? Name, string? SubjectPath, string Detail);

/// <summary>Splits the message of a successful run into its actions; any other message is one text action.</summary>
internal static partial class RunMessage
{
    private const string RanPrefix = "Ran ";

    public static IReadOnlyList<RunMessageAction> Parse(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return [];
        }

        if (!message.StartsWith(RanPrefix, StringComparison.Ordinal))
        {
            return [new RunMessageAction(null, null, message)];
        }

        var descriptions = message[RanPrefix.Length..];
        if (string.IsNullOrWhiteSpace(descriptions))
        {
            return [];
        }

        // Arguments and values can hold "; " themselves, which splits them here and shows those parts as text.
        return descriptions.Split("; ").Select(ParseAction).ToArray();
    }

    private static RunMessageAction ParseAction(string part)
    {
        if (ChangeStatePattern().Match(part) is { Success: true } changeState)
        {
            return new RunMessageAction("ChangeState", null, changeState.Groups["state"].Value);
        }

        if (OperationPattern().Match(part) is { Success: true } operation)
        {
            return new RunMessageAction(operation.Groups["name"].Value, GetSubjectPath(operation), operation.Groups["arguments"].Value);
        }

        if (WritePattern().Match(part) is { Success: true } write)
        {
            return new RunMessageAction(write.Groups["name"].Value, GetSubjectPath(write), "= " + write.Groups["value"].Value);
        }

        return new RunMessageAction(null, null, part);
    }

    private static string GetSubjectPath(Match match) => match.Groups["path"] is { Success: true } path ? path.Value : "/";

    [GeneratedRegex(@"^ChangeState\((?<state>.*)\)$")]
    private static partial Regex ChangeStatePattern();

    // The path is the first one that the name and the arguments can follow, with keys in brackets kept whole, so that a
    // key or an argument may hold slashes and parentheses; an absent path is the root.
    [GeneratedRegex(@"^(?<path>/(?:[^\[]|\[[^\]]*\])*?)?/(?<name>[^/\s(\[\]]+)\((?<arguments>(?:\w+: .*)?)\)$")]
    private static partial Regex OperationPattern();

    [GeneratedRegex(@"^(?<path>/(?:[^\[]|\[[^\]]*\])*?)?/(?<name>[^/\s=\[\]]+) = (?<value>.*)$")]
    private static partial Regex WritePattern();
}
