namespace HomeBlaze.Components.Abstractions;

/// <summary>
/// Interface for edit components that provide validation state and save functionality.
/// Components implementing this interface can be used as edit views in the subject browser.
/// </summary>
public interface ISubjectEditComponent : ISubjectComponent
{
    /// <summary>
    /// Gets whether the form is valid and ready to be saved.
    /// </summary>
    bool IsValid { get; }

    /// <summary>
    /// Gets whether the form has unsaved changes.
    /// </summary>
    bool IsDirty { get; }

    /// <summary>
    /// Raised with the new value whenever <see cref="IsValid"/> changes.
    /// </summary>
    /// <remarks>
    /// The create dialog, the edit dialog and the page editor enable their confirm button from
    /// <see cref="IsValid"/> and follow this event. A component that changes its validity without raising it
    /// is only picked up at the next interaction in its form.
    /// </remarks>
    event Action<bool>? IsValidChanged;

    /// <summary>
    /// Raised when IsDirty changes.
    /// </summary>
    event Action<bool>? IsDirtyChanged;

    /// <summary>
    /// Saves the changes to the subject.
    /// </summary>
    Task SaveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets whether <see cref="SaveAsync"/> stores the changes of an existing subject itself, such as through a store of
    /// its own, so that the host does not persist the configuration that contains the subject afterwards. False by
    /// default. The host still checks that the viewer may administer the configuration of the subject before it saves.
    /// </summary>
    bool PersistsItself => false;

    /// <summary>
    /// Gets the preferred dialog size for this edit component.
    /// </summary>
    string PreferredDialogSize => "Small";

    /// <summary>
    /// Gets or sets whether the component is in creation mode (new subject) vs edit mode.
    /// </summary>
    bool IsCreating { get; set; }
}
