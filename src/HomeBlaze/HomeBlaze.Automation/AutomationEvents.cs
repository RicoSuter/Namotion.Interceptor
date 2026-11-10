using System.Collections.Immutable;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Services.Messaging;
using Namotion.Interceptor;

namespace HomeBlaze.Automation;

/// <summary>
/// The events that rules can trigger on: the event types a subject publishes through <see cref="IObservable{T}"/> and the
/// fields of their payloads.
/// </summary>
public static class AutomationEvents
{
    /// <summary>The path of the automation seen from one of its rules, whose events are its state changes.</summary>
    public const string OwnAutomationPath = "..";

    public static ImmutableArray<Type> GetEventTypes(Type subjectType) => SubjectEvents.GetEventTypes(subjectType);

    /// <summary>The names of the fields of a payload type, ordered by name.</summary>
    public static ImmutableArray<string> GetPayloadFields(Type eventType) =>
        [.. EventPayloadReader.GetMembers(eventType).Values.Select(member => member.Name).Order(StringComparer.Ordinal)];

    /// <summary>
    /// Resolves the subject that publishes the events of a trigger, from the saved rule with the key. For a rule that is
    /// not saved yet, <see cref="OwnAutomationPath"/> is the automation, an absolute path resolves from the root, and any
    /// other relative path leads nowhere.
    /// </summary>
    public static IInterceptorSubject? ResolveSource(Automation automation, string? ruleKey, string? subjectPath)
    {
        if (string.IsNullOrWhiteSpace(subjectPath))
        {
            return null;
        }

        var isSaved = ruleKey is not null && automation.Rules.ContainsKey(ruleKey);
        if (!isSaved && subjectPath == OwnAutomationPath)
        {
            return automation;
        }

        return automation.PathResolver.ResolveSubject(subjectPath, AutomationPreview.GetAnchor(automation, ruleKey));
    }
}
