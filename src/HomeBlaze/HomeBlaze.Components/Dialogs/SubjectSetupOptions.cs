using HomeBlaze.Storage.Abstractions;
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
    /// Optional callback that stores each created subject while the dialog stays open, invoked after the edits and the
    /// configuration are applied. A subject created from a found device on the Discover page then returns the dialog
    /// to the Discover page, so that the next device can be created; a subject created from the type list closes it.
    /// </summary>
    /// <remarks>
    /// With this callback the dialog returns no result. When the callback throws, the dialog reports the error and
    /// stays on the configure page; a subject that has no parent after the callback threw is discarded later.
    /// It requires <see cref="NameField"/> and must not be set together with <see cref="BeforeSave"/>.
    /// </remarks>
    public Func<CreateSubjectResult, CancellationToken, Task>? OnCreatedAsync { get; init; }

    /// <summary>
    /// How the dialog asks for the name, or null when the created subject has no name of its own.
    /// </summary>
    public SubjectNameOptions? NameField { get; init; }

    /// <summary>
    /// The type to create, which skips the picker and has no way back to it, or null to pick the type in the dialog.
    /// </summary>
    public Type? Type { get; init; }
}
