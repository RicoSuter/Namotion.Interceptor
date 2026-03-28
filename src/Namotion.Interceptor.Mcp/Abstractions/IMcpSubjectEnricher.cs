using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Mcp.Abstractions;

/// <summary>
/// Adds subject-level metadata fields (prefixed with $) to query responses.
/// </summary>
public interface IMcpSubjectEnricher
{
    /// <summary>
    /// Enriches the subject with additional metadata fields.
    /// </summary>
    /// <param name="subject">The registered subject to enrich.</param>
    /// <param name="metadata">The metadata dictionary to add fields to.</param>
    void EnrichSubject(RegisteredSubject subject, IDictionary<string, object?> metadata);
}
