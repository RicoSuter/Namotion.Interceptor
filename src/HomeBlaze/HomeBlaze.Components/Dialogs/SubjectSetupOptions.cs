using Namotion.Interceptor;

namespace HomeBlaze.Components.Dialogs;

/// <summary>
/// Options of the create subject dialog.
/// </summary>
public sealed record SubjectSetupOptions
{
    /// <summary>
    /// Optional callback that establishes the parent chain, invoked after the edits are applied and before they are persisted.
    /// Without it nothing is persisted, and the caller stores the subject of the result.
    /// </summary>
    /// <remarks>
    /// The callback must attach the subject to a parent. A subject that has no parent after the callback returned
    /// is discarded when the dialog closes, although it is still returned in the result of the dialog.
    /// </remarks>
    public Action<IInterceptorSubject>? BeforeSave { get; init; }

    /// <summary>
    /// How the dialog asks for the name, or null when the created subject has no name of its own.
    /// </summary>
    public SubjectNameOptions? NameField { get; init; }
}
