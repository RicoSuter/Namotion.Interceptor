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

    // Whether the widget that SelectSubject selected takes the focus once it rendered as selected, see TakesFocus.
    private bool _takesFocus;

    /// <summary>
    /// Gets the selected widget, or null when none is selected.
    /// </summary>
    public SubjectComponent? Selected { get; private set; }

    /// <summary>
    /// Raised after the selection or the pending subject changed, for components that depend on where the selection is, such
    /// as a widget that shows more while the selection is inside it.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Gets the subject that <see cref="SelectSubject"/> found no widget for yet, or null. A widget that shows the subject or
    /// hides it, such as a tab layout with the subject as one of its tabs, can show it.
    /// </summary>
    internal IInterceptorSubject? PendingSubject => _pendingSubject;

    /// <summary>
    /// Selects the widget, or clears the selection when it is null.
    /// </summary>
    public void Select(SubjectComponent? component)
    {
        _pendingSubject = null;
        _takesFocus = false;
        SetSelected(component);
    }

    /// <summary>
    /// Selects the selectable widget of <paramref name="subject"/> among <paramref name="root"/> and the widgets inside it,
    /// and gives it the focus once rendered when an edit left the focus nowhere. When there is none yet, such as for a block
    /// that a reload of the page has just created, the current selection stays, and the first widget of the subject that
    /// renders selectable takes it, unless another widget is selected before.
    /// </summary>
    public void SelectSubject(IInterceptorSubject subject, SubjectComponent root)
    {
        if (root.FindSelectable(subject) is { } component)
        {
            Select(component);
            _takesFocus = true;

            // A widget that was selected already, such as one that a move kept, renders all the same to take the focus.
            component.OnSelectionChanged();
            return;
        }

        // Kept rather than cleared: text in the content of a widget, such as the footer of a card, is selectable only while
        // that widget or a block in it is selected, so it would never become selectable to take the selection.
        _pendingSubject = subject;
        _takesFocus = true;
        Changed?.Invoke();
    }

    /// <summary>
    /// Clears the selection.
    /// </summary>
    public void Clear() => Select(null);

    internal void OnRendered(SubjectComponent component)
    {
        if (_pendingSubject is { } subject && ReferenceEquals(component.Subject, subject) && component.IsSelectable)
        {
            _pendingSubject = null;
            SetSelected(component);
        }
    }

    // True once for the widget that SelectSubject selected, after a render of it as selected.
    internal bool TakesFocus(SubjectComponent component)
    {
        if (!_takesFocus || _pendingSubject is not null || !ReferenceEquals(Selected, component))
        {
            return false;
        }

        _takesFocus = false;
        return true;
    }

    // The widget around the removed one takes the selection rather than none, so that the content that holds the
    // replacement of the removed widget, such as edited text in a card, stays at the selection and the replacement can take
    // it. A pending subject stays pending. The removed widget renders no more.
    internal void Remove(SubjectComponent component)
    {
        if (ReferenceEquals(Selected, component))
        {
            var ancestor = component.SelectableAncestor;
            Selected = ancestor;
            ancestor?.OnSelectionChanged();
            Changed?.Invoke();
        }
    }

    private void SetSelected(SubjectComponent? component)
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
}
