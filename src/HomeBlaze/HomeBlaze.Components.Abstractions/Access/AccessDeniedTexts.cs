namespace HomeBlaze.Components.Abstractions.Access;

/// <summary>
/// The texts the UI shows when the roles of the viewer do not allow an action.
/// </summary>
public static class AccessDeniedTexts
{
    /// <summary>The title of the message that refuses an action.</summary>
    public const string Title = "Not allowed";

    /// <summary>The tooltip of a control or button that is disabled because the viewer may not use it.</summary>
    public const string Tooltip = "Your roles do not allow this.";

    /// <summary>The reason a write is refused.</summary>
    public const string Write = "Your roles do not allow changing this.";

    /// <summary>The reason an operation or query does not run.</summary>
    public const string Invoke = "Your roles do not allow running this.";

    /// <summary>
    /// The reason an administration change is refused, such as configuring, creating or deleting a subject or editing a page
    /// or file.
    /// </summary>
    public const string Administer = "Your roles do not allow this change.";
}
