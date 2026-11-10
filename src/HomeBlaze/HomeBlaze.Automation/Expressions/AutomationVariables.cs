using System.Collections.Immutable;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Automation.Expressions;

/// <summary>The variables of automation expressions, which <see cref="AutomationExpressionCheck"/> parses with.</summary>
internal static class AutomationVariables
{
    /// <summary>The current instant, a <see cref="DateTimeOffset"/> in UTC.</summary>
    public const string Now = "now";

    /// <summary>The local time of day, a <see cref="TimeSpan"/> in the time zone of the automation.</summary>
    public const string Time = "time";

    /// <summary>The payload of the captured event, a record whose members follow the variable, such as <c>event/State</c>.</summary>
    public const string Event = "event";

    /// <summary>The subject that published the captured event.</summary>
    public const string Subject = ExpressionVariables.Subject;

    /// <summary>Declared only so that it is refused as a variable rather than read as a path.</summary>
    public const string Value = ExpressionVariables.Value;

    /// <summary>The names the expressions of automations are parsed with.</summary>
    public static readonly ImmutableArray<string> All = [Now, Time, Event, Subject, Value];
}
