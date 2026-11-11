namespace HomeBlaze.Authorization;

/// <summary>
/// The tags <see cref="HomeBlazeMemberTagProvider"/> gives members, which rules select on.
/// </summary>
public static class MemberTags
{
    /// <summary>A property marked with <c>[State]</c>.</summary>
    public const string State = "state";

    /// <summary>A property marked with <c>[Configuration]</c>.</summary>
    public const string Configuration = "configuration";

    /// <summary>A configuration property marked as secret.</summary>
    public const string Secret = "secret";

    /// <summary>A method marked with <c>[Query]</c>.</summary>
    public const string Query = "query";

    /// <summary>A method marked with <c>[Operation]</c>.</summary>
    public const string Operation = "operation";

    /// <summary>An operation that requires the confirmation of a person.</summary>
    public const string RequiresConfirmation = "requires-confirmation";

    /// <summary>A property that can hold subjects.</summary>
    public const string Subjects = "subjects";

    /// <summary>An attribute of a property.</summary>
    public const string Attribute = "attribute";

    /// <summary>The metadata of a subject or, as an attribute, of a property.</summary>
    public const string Metadata = "metadata";
}
