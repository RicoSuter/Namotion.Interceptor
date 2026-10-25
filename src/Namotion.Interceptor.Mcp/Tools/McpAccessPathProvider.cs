using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// The path provider of a configuration with an access policy: it includes only the properties the inner provider
/// includes and the policy allows to read, and an attribute only when its property is included too. Everything else
/// is the inner provider's.
/// </summary>
internal sealed class McpAccessPathProvider : PathProviderBase
{
    private readonly PathProviderBase _inner;
    private readonly IMcpAccessPolicy _policy;

    private McpAccessPathProvider(PathProviderBase inner, IMcpAccessPolicy policy)
    {
        _inner = inner;
        _policy = policy;
    }

    /// <summary>
    /// The provider the tools of the configuration use: <paramref name="pathProvider"/> itself without a policy.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="pathProvider"/> does not extend <see cref="PathProviderBase"/>.</exception>
    public static PathProviderBase Create(IPathProvider pathProvider, IMcpAccessPolicy? policy)
    {
        var basePathProvider = pathProvider as PathProviderBase
            ?? throw new InvalidOperationException("PathProvider must extend PathProviderBase.");

        return policy is null ? basePathProvider : new McpAccessPathProvider(basePathProvider, policy);
    }

    public override char PathSeparator => _inner.PathSeparator;

    public override char IndexOpen => _inner.IndexOpen;

    public override char IndexClose => _inner.IndexClose;

    public override bool IsPropertyIncluded(RegisteredSubjectProperty property) =>
        _inner.IsPropertyIncluded(property) &&
        _policy.IsAllowed(property, McpAccessActions.Read) &&
        (!property.IsAttribute ||
         property.Parent.TryGetProperty(property.AttributeMetadata.PropertyName) is { } attributedProperty &&
         IsPropertyIncluded(attributedProperty));

    public override string? TryGetPropertySegment(RegisteredSubjectProperty property) =>
        _inner.TryGetPropertySegment(property);

    public override RegisteredSubjectProperty? TryGetPropertyFromSegment(RegisteredSubject subject, string segment) =>
        _inner.TryGetPropertyFromSegment(subject, segment);
}
