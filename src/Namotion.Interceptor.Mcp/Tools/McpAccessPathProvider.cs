using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// The path provider of a configuration with an access policy. A listing provider includes what the inner provider
/// includes and the policy allows to read; a resolution provider includes what the policy allows to read. Both look up
/// a segment as the inner provider does, but treat a property the policy does not allow to read as absent. Separators
/// and segments are the inner provider's.
/// </summary>
internal sealed class McpAccessPathProvider : PathProviderBase
{
    private readonly PathProviderBase _inner;
    private readonly IMcpAccessPolicy _policy;
    private readonly bool _appliesInnerInclusion;

    private McpAccessPathProvider(PathProviderBase inner, IMcpAccessPolicy policy, bool appliesInnerInclusion)
    {
        _inner = inner;
        _policy = policy;
        _appliesInnerInclusion = appliesInnerInclusion;
    }

    /// <summary>
    /// <paramref name="pathProvider"/> restricted to what <paramref name="policy"/> allows to read, or
    /// <paramref name="pathProvider"/> itself without a policy. <paramref name="appliesInnerInclusion"/> keeps the
    /// inclusion of <paramref name="pathProvider"/> in addition to the policy.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="pathProvider"/> does not extend <see cref="PathProviderBase"/>.</exception>
    public static PathProviderBase Create(IPathProvider pathProvider, IMcpAccessPolicy? policy, bool appliesInnerInclusion)
    {
        var basePathProvider = pathProvider as PathProviderBase
            ?? throw new InvalidOperationException("PathProvider must extend PathProviderBase.");

        return policy is null ? basePathProvider : new McpAccessPathProvider(basePathProvider, policy, appliesInnerInclusion);
    }

    /// <summary>
    /// Whether the policy allows to read the member, and for an attribute also its property.
    /// </summary>
    public static bool IsReadable(IMcpAccessPolicy policy, RegisteredSubjectProperty member) =>
        policy.IsAllowed(member, McpAccessActions.Read) &&
        (!member.IsAttribute ||
         member.Parent.TryGetProperty(member.AttributeMetadata.PropertyName) is { } attributedProperty &&
         IsReadable(policy, attributedProperty));

    public override char PathSeparator => _inner.PathSeparator;

    public override char IndexOpen => _inner.IndexOpen;

    public override char IndexClose => _inner.IndexClose;

    public override bool IsPropertyIncluded(RegisteredSubjectProperty property) =>
        (!_appliesInnerInclusion || _inner.IsPropertyIncluded(property)) && IsReadable(_policy, property);

    public override string? TryGetPropertySegment(RegisteredSubjectProperty property) =>
        _inner.TryGetPropertySegment(property);

    public override RegisteredSubjectProperty? TryGetPropertyFromSegment(RegisteredSubject subject, string segment)
    {
        var property = _inner.TryGetPropertyFromSegment(subject, segment);
        if (property is null || IsReadable(_policy, property))
        {
            return property;
        }

        // As for a name that does not exist, the segment is a key of the [InlinePaths] dictionary, so a client cannot
        // tell the hidden property from a missing one and the registry writes the key without the explicit form.
        var inlinePathsPropertyName = InlinePathsAttribute.GetInlinePathsPropertyName(subject.Subject.GetType());
        return inlinePathsPropertyName is not null ? subject.TryGetProperty(inlinePathsPropertyName) : null;
    }
}
