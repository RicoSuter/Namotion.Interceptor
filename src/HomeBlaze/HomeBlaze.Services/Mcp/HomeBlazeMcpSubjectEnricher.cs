using HomeBlaze.Abstractions;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Services.Mcp;

/// <summary>
/// Enriches MCP query responses with $title, $icon, and $type from HomeBlaze interfaces.
/// </summary>
public class HomeBlazeMcpSubjectEnricher : IMcpSubjectEnricher
{
    public void EnrichSubject(RegisteredSubject subject, IDictionary<string, object?> metadata)
    {
        if (subject.Subject is ITitleProvider titleProvider)
        {
            metadata["$title"] = titleProvider.Title;
        }

        if (subject.Subject is IIconProvider iconProvider)
        {
            metadata["$icon"] = iconProvider.IconName;
        }

        metadata["$type"] = subject.Subject.GetType().Name;
    }
}
