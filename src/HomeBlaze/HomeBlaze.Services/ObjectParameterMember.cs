namespace HomeBlaze.Services;

/// <summary>
/// A public settable member of an object parameter, as entered in a form.
/// </summary>
/// <param name="Name">The CLR member name, which keys its input.</param>
/// <param name="JsonName">The name the member is read under from JSON.</param>
/// <param name="Type">The member type.</param>
/// <param name="IsRequired">Whether the member must be given, from the C# <c>required</c> modifier.</param>
/// <param name="IsNullable">Whether the member may be null.</param>
public sealed record ObjectParameterMember(string Name, string JsonName, Type Type, bool IsRequired, bool IsNullable);
