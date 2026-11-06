namespace HomeBlaze.Automation;

/// <summary>A field of an event payload, which filters and arguments read as <c>event/Name</c>.</summary>
public sealed record AutomationEventField(string Name, Type Type);
