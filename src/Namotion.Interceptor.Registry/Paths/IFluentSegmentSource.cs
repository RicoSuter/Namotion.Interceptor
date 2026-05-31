namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Supplies the per-(type, member) path segment for code-based (fluent) mapping. Implemented by the
/// connector-agnostic fluent registry so FluentPathProvider can resolve segments without
/// depending on connector metadata types.
/// </summary>
public interface IFluentSegmentSource
{
    /// <summary>
    /// Returns true when a type-level registration exists for the given holder type and member.
    /// <paramref name="segment"/> is the registered segment override, or null to mean "use the
    /// member's BrowseName".
    /// </summary>
    bool TryGetSegment(Type subjectType, string member, out string? segment);
}
