using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Authorization;

/// <summary>
/// An action a rule allows or denies, identified by a name compared ignoring case. The library defines
/// <see cref="Read"/> and <see cref="Write"/>; an application defines the others it needs and passes all of
/// them as the known actions of <see cref="AuthorizationPolicy.Compile"/>.
/// </summary>
[JsonConverter(typeof(PermissionActionJsonConverter))]
public readonly record struct PermissionAction
{
    /// <summary>Reading a member's value.</summary>
    public static readonly PermissionAction Read = new("read");

    /// <summary>Writing a member's value.</summary>
    public static readonly PermissionAction Write = new("write");

    private readonly string? _name;

    /// <param name="name">The name; must not be empty or whitespace.</param>
    public PermissionAction(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
    }

    /// <summary>Gets the name; empty for <see langword="default"/>, which is no action and is known to no policy.</summary>
    public string Name => _name ?? string.Empty;

    public bool Equals(PermissionAction other) => string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Name);

    public override string ToString() => Name;
}
