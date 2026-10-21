namespace HomeBlaze.Components;

/// <summary>
/// The widget of a page in edit mode whose actions are shown. At most one widget of a page is selected.
/// </summary>
/// <remarks>
/// Not thread-safe: use it on the dispatcher of the renderer, like the components that it notifies.
/// </remarks>
public sealed class WidgetSelection
{
    /// <summary>
    /// Gets the selected widget, or null when none is selected.
    /// </summary>
    public SubjectComponent? Selected { get; private set; }

    /// <summary>
    /// Raised after the selection changed, for components that depend on where the selection is, such as a widget
    /// that shows more while the selection is inside it.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Selects the widget, or clears the selection when it is null.
    /// </summary>
    public void Select(SubjectComponent? component)
    {
        var previous = Selected;
        if (ReferenceEquals(previous, component))
        {
            return;
        }

        Selected = component;
        previous?.OnSelectionChanged();
        component?.OnSelectionChanged();
        Changed?.Invoke();
    }

    /// <summary>
    /// Clears the selection.
    /// </summary>
    public void Clear() => Select(null);

    internal void Remove(SubjectComponent component)
    {
        if (ReferenceEquals(Selected, component))
        {
            Selected = null;
            Changed?.Invoke();
        }
    }
}
