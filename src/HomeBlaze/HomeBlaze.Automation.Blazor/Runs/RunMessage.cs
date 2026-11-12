using System.Text.RegularExpressions;

namespace HomeBlaze.Automation.Blazor.Runs;

/// <summary>
/// An action of a run as its message describes it: the operation or property name, the absolute path of its subject, and
/// the arguments, the written value or the target state. A part that matches no description, or whose path is relative to
/// the rule, has only <see cref="Detail"/>.
/// </summary>
internal sealed record RunMessageAction(string? Name, string? SubjectPath, string Detail);

/// <summary>
/// Splits the message of a successful run or of a dry run into its actions; any other message, such as the failures of
/// a run, is one text action.
/// </summary>
internal static partial class RunMessage
{
    private const string RanPrefix = "Ran ";
    private const string DryRunPrefix = "Would run ";

    // The end of a message that the runtime cut to AutomationRule.MaxRunMessageLength.
    private const string Ellipsis = "...";

    public static IReadOnlyList<RunMessageAction> Parse(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return [];
        }

        int prefixLength;
        if (message.StartsWith(RanPrefix, StringComparison.Ordinal))
        {
            prefixLength = RanPrefix.Length;
        }
        else if (message.StartsWith(DryRunPrefix, StringComparison.Ordinal))
        {
            prefixLength = DryRunPrefix.Length;
        }
        else
        {
            return [new RunMessageAction(null, null, message)];
        }

        var descriptions = message[prefixLength..];
        if (string.IsNullOrWhiteSpace(descriptions))
        {
            return [];
        }

        // An argument or value can hold "; " itself; the parts it is split into here show as text or as actions of their own.
        var parts = descriptions.Split("; ");
        var actions = new RunMessageAction[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            actions[index] = ParseAction(parts[index]);
        }

        // A shortened part can end anywhere, such as inside an argument that then reads as a property write.
        if (message.Length >= AutomationRule.MaxRunMessageLength && message.EndsWith(Ellipsis, StringComparison.Ordinal))
        {
            actions[^1] = new RunMessageAction(null, null, parts[^1]);
        }

        return actions;
    }

    private static RunMessageAction ParseAction(string part)
    {
        if (ChangeStatePattern().Match(part) is { Success: true } changeState)
        {
            return new RunMessageAction("ChangeState", null, changeState.Groups["state"].Value);
        }

        if (OperationPattern().Match(part) is { Success: true } operation && !NamesAnError(operation))
        {
            return new RunMessageAction(operation.Groups["name"].Value, GetSubjectPath(operation), operation.Groups["arguments"].Value);
        }

        if (WritePattern().Match(part) is { Success: true } write && !NamesAnError(write))
        {
            return new RunMessageAction(write.Groups["name"].Value, GetSubjectPath(write), "= " + write.Groups["value"].Value);
        }

        return new RunMessageAction(null, null, part);
    }

    // A dry run describes an action it cannot prepare by its path, ": " and the error, and the error can quote a path and
    // a value that read as a description of their own. A path holds ": " only inside the brackets of a key.
    private static bool NamesAnError(Match match)
    {
        var path = match.Groups["path"].ValueSpan;
        var depth = 0;
        for (var index = 0; index < path.Length; index++)
        {
            switch (path[index])
            {
                case '[':
                    depth++;
                    break;
                case ']':
                    depth = Math.Max(0, depth - 1);
                    break;
                case ':' when depth == 0 && index + 1 < path.Length && path[index + 1] == ' ':
                    return true;
            }
        }

        return false;
    }

    private static string GetSubjectPath(Match match) => match.Groups["path"] is { Success: true } path ? path.Value : "/";

    [GeneratedRegex(@"^ChangeState\((?<state>.*)\)\z", RegexOptions.Singleline)]
    private static partial Regex ChangeStatePattern();

    // The path is the shortest one that the name and the arguments can follow, the root first, with keys in brackets kept
    // whole, so that a key or an argument may hold slashes and parentheses; an absent path is the root. An argument of a
    // record member is named "parameter.Member".
    [GeneratedRegex(@"^(?<path>/(?:[^\[]|\[[^\]]*\])*?)??/(?<name>[^/\s(\[\]]+)\((?<arguments>(?:[\w.]+: .*)?)\)\z", RegexOptions.Singleline)]
    private static partial Regex OperationPattern();

    [GeneratedRegex(@"^(?<path>/(?:[^\[]|\[[^\]]*\])*?)??/(?<name>[^/\s=\[\]]+) = (?<value>.*)\z", RegexOptions.Singleline)]
    private static partial Regex WritePattern();
}
