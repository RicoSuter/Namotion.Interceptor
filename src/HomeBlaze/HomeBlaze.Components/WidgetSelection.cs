using Namotion.Interceptor;

namespace HomeBlaze.Components;

/// <summary>
/// The widget of a page in edit mode whose actions are shown. At most one widget of a page is selected.
/// </summary>
/// <remarks>
/// Not thread-safe: use it on the dispatcher of the renderer, like the components that it notifies.
/// </remarks>
public sealed class WidgetSelection
{
    // The subject whose widget is selected when it renders, see SelectSubject.
    private IInterceptorSubject? _pendingSubject;

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
        _pendingSubject = null;
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
    /// Selects the selectable widget of <paramref name="subject"/> among <paramref name="root"/> and the widgets inside it.
    /// When there is none yet, such as for a block that a reload of the page has just created, the selection stays until
    /// the first such widget renders and is selected.
    /// </summary>
    public void SelectSubject(IInterceptorSubject subject, SubjectComponent root)
    {
        if (root.FindSelectable(subject) is { } component)
        {
            Select(component);
            return;
        }

        // Kept rather than cleared: text in the content of a widget, such as the footer of a card, is selectable only while
        // that widget or a block in it is selected, so it would never become selectable to take the selection.
        _pendingSubject = subject;
    }

    /// <summary>
    /// Clears the selection.
    /// </summary>
    public void Clear() => Select(null);

    internal void OnRendered(SubjectComponent component)
    {
        if (_pendingSubject is { } subject && ReferenceEquals(component.Subject, subject) && component.IsSelectable)
        {
            Select(component);
        }
    }

    internal void Remove(SubjectComponent component)
    {
        if (ReferenceEquals(Selected, component))
        {
            Selected = null;
            Changed?.Invoke();
        }
    }
}
