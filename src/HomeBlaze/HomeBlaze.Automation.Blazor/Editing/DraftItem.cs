namespace HomeBlaze.Automation.Blazor.Editing;

/// <summary>
/// An entry of a draft list that keeps its identity while its immutable value is replaced, so that the editor of the
/// entry keeps its own state, such as text that does not parse yet, when the list changes around it.
/// </summary>
public sealed class DraftItem<T>(T value)
    where T : class
{
    public T Value { get; set; } = value;
}
