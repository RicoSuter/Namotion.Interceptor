namespace Namotion.Interceptor.Authorization.Internal;

/// <summary>A rule with its actions as a bitmask and its selectors parsed; a null selector matches anything.</summary>
internal sealed class CompiledRule
{
    public required AuthorizationRole Role { get; init; }

    public required PermissionRule Rule { get; init; }

    public required ulong Actions { get; init; }

    public PathPattern[]? Paths { get; init; }

    public string[]? Types { get; init; }

    public string[]? Members { get; init; }

    public string[]? Tags { get; init; }
}
