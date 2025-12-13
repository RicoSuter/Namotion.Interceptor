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
    /// Raised when IsValid changes.
    /// </summary>
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
    /// Gets the preferred dialog size for this edit component.
    /// </summary>
    string PreferredDialogSize => "Small";
}
