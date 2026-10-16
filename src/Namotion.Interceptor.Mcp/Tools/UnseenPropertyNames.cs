using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// The requested property names that no observed subject has as an exposed scalar property yet, so a tool can warn
/// about a name that resolves on no subject in scope instead of silently matching or returning nothing.
/// </summary>
internal sealed class UnseenPropertyNames
{
    private readonly List<string> _names;

    private UnseenPropertyNames(List<string> names)
    {
        _names = names;
    }

    /// <summary>
    /// Creates a tracker for the distinct names of both sources, or returns null when there are none.
    /// </summary>
    public static UnseenPropertyNames? TryCreate(IEnumerable<string>? first, IEnumerable<string>? second)
    {
        List<string>? names = null;
        foreach (var name in (first ?? []).Concat(second ?? []))
        {
            names ??= [];
            if (!names.Contains(name))
            {
                names.Add(name);
            }
        }

        return names is not null ? new UnseenPropertyNames(names) : null;
    }

    /// <summary>
    /// Marks every name that resolves to a scalar property of <paramref name="subject"/> as seen.
    /// </summary>
    public void Observe(RegisteredSubject subject, PathProviderBase pathProvider)
    {
        for (var index = _names.Count - 1; index >= 0; index--)
        {
            if (McpToolHelper.TryGetScalarProperty(subject, pathProvider, _names[index]) is not null)
            {
                _names.RemoveAt(index);
            }
        }
    }

    /// <summary>
    /// One warning per name no observed subject has, or null when every name was seen.
    /// </summary>
    public IReadOnlyList<string>? CreateWarnings() =>
        _names.Count > 0
            ? _names.Select(name => $"No subject in scope has a property named '{name}'.").ToArray()
            : null;
}
