using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Automation.Expressions;

/// <summary>
/// Reads the members of an event payload: the public properties of its record, found once per type. A member that holds
/// a subject continues the path into that subject, as in <c>event/Button/Name</c>.
/// </summary>
internal static class EventPayloadReader
{
    // Weak, so that the event types of an unloaded plugin are not kept alive.
    private static readonly ConditionalWeakTable<Type, FrozenDictionary<string, PropertyInfo>> MembersByType = new();

    public static IReadOnlyDictionary<string, PropertyInfo> GetMembers(Type eventType) =>
        MembersByType.GetValue(eventType, static type => type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .DistinctBy(property => property.Name)
            .ToFrozenDictionary(property => property.Name, StringComparer.Ordinal));

    /// <summary>Reads the member path from the payload; an unknown member or a path into a value that is no subject is missing.</summary>
    public static ExpressionValue Read(IEvent payload, string memberPath, SubjectPathResolver resolver)
    {
        var separator = memberPath.IndexOf(AutomationPaths.Separator);
        var name = separator < 0 ? memberPath : memberPath[..separator];
        if (!GetMembers(payload.GetType()).TryGetValue(name, out var member))
        {
            return ExpressionValue.Missing;
        }

        var value = member.GetValue(payload);
        if (separator < 0)
        {
            return ExpressionValue.FromValue(value);
        }

        return value is IInterceptorSubject subject
            ? AutomationPaths.ReadFrom(resolver, memberPath[(separator + 1)..], subject)
            : ExpressionValue.Missing;
    }
}
