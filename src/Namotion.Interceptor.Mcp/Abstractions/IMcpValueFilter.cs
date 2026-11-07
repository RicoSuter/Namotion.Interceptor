using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Mcp.Abstractions;

/// <summary>
/// Changes the value of a property or attribute that the tools return or compare, for example to leave out the parts of
/// a value that name what the access policy hides.
/// </summary>
public interface IMcpValueFilter
{
    /// <summary>
    /// Gets the value the tools use for the member instead of <paramref name="value"/>. Called only for a member the
    /// tools may read. May be called concurrently, so it must be thread-safe.
    /// </summary>
    /// <param name="member">The property or attribute.</param>
    /// <param name="value">The value of the member, as the filters before this one left it.</param>
    object? Filter(RegisteredSubjectProperty member, object? value);
}
