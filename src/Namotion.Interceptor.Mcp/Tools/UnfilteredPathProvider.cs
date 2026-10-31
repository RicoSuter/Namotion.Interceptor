using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// The characters, segments and segment lookup of another provider, including every property.
/// </summary>
internal sealed class UnfilteredPathProvider(PathProviderBase inner) : PathProviderBase
{
    public override char PathSeparator => inner.PathSeparator;

    public override char IndexOpen => inner.IndexOpen;

    public override char IndexClose => inner.IndexClose;

    public override bool IsPropertyIncluded(RegisteredSubjectProperty property) => true;

    public override string? TryGetPropertySegment(RegisteredSubjectProperty property)
        => inner.TryGetPropertySegment(property);

    public override RegisteredSubjectProperty? TryGetPropertyFromSegment(RegisteredSubject subject, string segment)
        => inner.TryGetPropertyFromSegment(subject, segment);
}
