using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Mcp.Abstractions;

/// <summary>
/// Changes the value of a property or attribute that the built-in tools return or compare, for example to leave out the
/// parts of a value that name what the access policy hides, see <see cref="McpServerConfiguration.ValueFilters"/>.
/// </summary>
public interface IMcpValueFilter
{
    /// <summary>
    /// Gets the value the tools use for the member instead of <paramref name="value"/>. Called only for a member the
    /// tools may read. May be called concurrently, so it must be thread-safe.
    /// </summary>
    /// <remarks>
    /// A tool of the application may match the value before the filters first, to skip the members that cannot match,
    /// and then the filtered value. Such a tool does not find a value that matches only after a filter, such as metadata
    /// to which a filter added an entry, so a filter for such a tool must only leave out parts of a value.
    /// </remarks>
    /// <param name="member">The property or attribute.</param>
    /// <param name="value">The value of the member, as the filters before this one left it.</param>
    object? Filter(RegisteredSubjectProperty member, object? value);
}
