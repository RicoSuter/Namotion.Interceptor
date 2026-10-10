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
    public SubjectNameOptions? Name { get; init; }
}

/// <summary>
/// How the create subject dialog asks for the name of the new subject.
/// </summary>
/// <param name="Label">The label of the name field.</param>
/// <param name="Suffix">A fixed suffix shown after the name, such as ".json", or an empty string.</param>
/// <param name="ExistingNames">The names that are already used, with or without the suffix. The dialog rejects them.</param>
public sealed record SubjectNameOptions(string Label, string Suffix, IReadOnlyCollection<string> ExistingNames);
