using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Mcp.Abstractions;

/// <summary>
/// Adds subject-level metadata fields (prefixed with $) to query responses.
/// </summary>
public interface IMcpSubjectEnricher
{
    /// <summary>
    /// Gets enrichment metadata for the subject.
    /// </summary>
    /// <param name="subject">The registered subject to enrich.</param>
    /// <returns>A dictionary of metadata fields to add to the subject node.</returns>
    IDictionary<string, object?> GetSubjectEnrichments(RegisteredSubject subject);
}
