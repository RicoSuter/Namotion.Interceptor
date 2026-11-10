namespace HomeBlaze.Abstractions;

/// <summary>
/// The result of an operation that creates a secret which exists only now, such as a setup code or a key. The user
/// interface shows it once in a dialog with a button that copies it, and keeps it nowhere else.
/// </summary>
/// <remarks>
/// <see cref="ToString"/> leaves the secret out, so that a log or a generic display of the result never shows it.
/// </remarks>
/// <param name="Title">The title of the dialog, such as whose setup code it is.</param>
/// <param name="Message">What the secret is for, who gets it and how long it is valid.</param>
/// <param name="Secret">The secret.</param>
/// <param name="LinkPath">The page the secret is used on, relative to the base address of the application, or null.</param>
public sealed record OneTimeSecret(string Title, string Message, string Secret, string? LinkPath = null)
{
    /// <summary>Gets the title, without the secret.</summary>
    public override string ToString() => Title;
}
