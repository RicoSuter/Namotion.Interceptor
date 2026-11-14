using Microsoft.AspNetCore.Components;

namespace HomeBlaze.Components;

/// <summary>
/// A widget that renders more while its page is edited and the selection is on it or on a widget inside it, such as a list
/// whose rows become selectable. It renders again when that changes. A widget that overrides
/// <see cref="OnParametersSet"/> calls the base first.
/// </summary>
public abstract class SelectionContainingWidgetBase : ComponentBase, IDisposable
{
    private WidgetSelection? _subscribedSelection;

    [CascadingParameter(Name = "IsEditing")]
    public bool IsEditing { get; set; }

    [CascadingParameter]
    public WidgetSelection? Selection { get; set; }

    [CascadingParameter(Name = "ParentSubjectComponent")]
    public SubjectComponent? Owner { get; set; }

    /// <summary>Gets whether the page is edited and its selected widget is this widget or a widget inside it.</summary>
    protected bool ContainsSelection { get; private set; }

    protected override void OnParametersSet()
    {
        // The flag follows the selection, which changes without new parameters.
        var selection = IsEditing ? Selection : null;
        if (!ReferenceEquals(selection, _subscribedSelection))
        {
            Unsubscribe();
            _subscribedSelection = selection;
            if (selection is not null)
            {
                selection.Changed += OnSelectionChanged;
            }
        }

        ContainsSelection = selection is not null && Owner?.ContainsSelection == true;
    }

    private void OnSelectionChanged()
    {
        // Every such widget of the page is notified, and only one that gains or loses the selection renders again.
        var containsSelection = Owner?.ContainsSelection == true;
        if (containsSelection != ContainsSelection)
        {
            ContainsSelection = containsSelection;
            _ = InvokeAsync(StateHasChanged);
        }
    }

    private void Unsubscribe()
    {
        if (_subscribedSelection is not null)
        {
            _subscribedSelection.Changed -= OnSelectionChanged;
            _subscribedSelection = null;
        }
    }

    public void Dispose() => Unsubscribe();
}
