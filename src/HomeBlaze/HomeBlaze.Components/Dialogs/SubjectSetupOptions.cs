using Namotion.Interceptor;

namespace HomeBlaze.Components.Dialogs;

/// <summary>
/// Options of the create subject dialog.
/// </summary>
public sealed record SubjectSetupOptions
{
    /// <summary>
    /// Optional callback that establishes the parent chain, invoked after the edits are applied and before they are persisted.
    /// </summary>
    public Action<IInterceptorSubject>? BeforeSave { get; init; }

    /// <summary>
    /// How the dialog asks for the name, or null when the created subject has no name of its own.
    /// </summary>
    public SubjectNameOptions? NameField { get; init; }
}
