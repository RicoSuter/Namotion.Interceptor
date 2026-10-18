namespace HomeBlaze.Services;

/// <summary>
/// A member of an object parameter, as entered in a form.
/// </summary>
/// <param name="Name">The name the member is read under from JSON, which also keys its input.</param>
/// <param name="Type">The member type.</param>
/// <param name="IsRequired">Whether the member must be given, from the C# <c>required</c> modifier or <c>[JsonRequired]</c>.</param>
public sealed record ObjectParameterMember(string Name, Type Type, bool IsRequired);
