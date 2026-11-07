using System.Text.Json.Serialization;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Automation;

/// <summary>
/// Something that requests a run of a rule. Serialized with a stable <c>$type</c> discriminator; an unknown
/// discriminator is rejected.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(PropertyTrigger), "PropertyTrigger")]
[JsonDerivedType(typeof(SubjectEventTrigger), "SubjectEventTrigger")]
public abstract record AutomationTrigger;

/// <summary>
/// Fires when its expression changes from not true to true and stayed true for <see cref="For"/>. It fires once and
/// then waits until the expression is false, or until <see cref="Rearm"/> is true when that is set; a rearm while the
/// expression is still true fires only on the next change to true.
/// </summary>
public sealed record PropertyTrigger : AutomationTrigger
{
    /// <summary>The condition, such as <c>/Weather/WS90/WindGust > 8</c>.</summary>
    [Configuration]
    public string Expression { get; init; } = string.Empty;

    /// <summary>
    /// How long the expression must stay true before the trigger fires; null or zero fires at once. Written as a
    /// duration such as <c>10m</c>.
    /// </summary>
    [Configuration]
    [JsonConverter(typeof(DurationJsonConverter))]
    public TimeSpan? For { get; init; }

    /// <summary>The condition that rearms the trigger after it fired, in place of the expression being false.</summary>
    [Configuration]
    public string? Rearm { get; init; }

    /// <summary>What the trigger does with the current value when it becomes active.</summary>
    [Configuration]
    public TriggerActivation OnActivation { get; init; }
}

/// <summary>Fires on every event of a type that a subject publishes and that passes the filter.</summary>
public sealed record SubjectEventTrigger : AutomationTrigger
{
    /// <summary>The path of the subject that publishes the events, absolute or relative to the rule.</summary>
    [Configuration]
    public string SubjectPath { get; init; } = string.Empty;

    /// <summary>The name or full name of the event type, such as <c>ButtonEvent</c>.</summary>
    [Configuration]
    public string EventType { get; init; } = string.Empty;

    /// <summary>A condition over the event, such as <c>event/ButtonState == Release</c>; null passes every event.</summary>
    [Configuration]
    public string? Filter { get; init; }
}
