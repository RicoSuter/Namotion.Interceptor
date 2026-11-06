namespace HomeBlaze.Automation.Blazor.Editing;

/// <summary>
/// An entry of a draft list that keeps its identity while its immutable value is replaced, so that the editor of the
/// entry keeps its own state, such as text that does not parse yet, when the list changes around it.
/// </summary>
public sealed class DraftItem<T>(T value)
    where T : class
{
    /// <summary>A key of the entry that does not change when the list is reordered; the key of its fields in <see cref="PendingRows"/>.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public T Value { get; set; } = value;

    /// <summary>Whether the operation of the action was changed while editing, so that the arguments of the previous operation may be stale.</summary>
    public bool IsOperationChanged { get; set; }
}
