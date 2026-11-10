using HomeBlaze.Authorization.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

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
    /// Detaches the subject again that <see cref="BeforeSave"/> attached, called when persisting it is refused, so that no
    /// subject stays attached without being saved. Set it together with <see cref="BeforeSave"/>.
    /// </summary>
    public Action<IInterceptorSubject>? RevertBeforeSave { get; init; }

    /// <summary>
    /// How the dialog asks for the name, or null when the created subject has no name of its own.
    /// </summary>
    public SubjectNameOptions? NameField { get; init; }

    /// <summary>
    /// The type to create, which skips the picker and has no way back to it, or null to pick the type in the dialog.
    /// </summary>
    public Type? Type { get; init; }

    /// <summary>
    /// The member of the method that creates the subject where the caller puts it, such as the <c>Create</c> operation of a
    /// folder: Create requires <see cref="IUserPermissions.CanInvoke"/> on it.
    /// </summary>
    /// <remarks>
    /// Create is refused unless at least one of <see cref="InvokeMember"/> and <see cref="AdministerMember"/> is set, and every
    /// member that is set must allow it. Checked when Create is clicked, before anything is applied, and again right before
    /// <see cref="BeforeSave"/>.
    /// </remarks>
    public RegisteredSubjectProperty? InvokeMember { get; init; }

    /// <summary>
    /// The member that will hold the subject, such as the content of a page or the nodes of a layout: Create requires
    /// <see cref="IUserPermissions.CanAdminister"/> on it. See <see cref="InvokeMember"/> for how the two combine.
    /// </summary>
    public RegisteredSubjectProperty? AdministerMember { get; init; }
}
