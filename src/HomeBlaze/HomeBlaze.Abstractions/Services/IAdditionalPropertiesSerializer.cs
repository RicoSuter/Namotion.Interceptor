using System.Text.Json;
using Namotion.Interceptor;

namespace HomeBlaze.Abstractions.Services;

/// <summary>
/// Serializes/deserializes additional "$" properties with subjects.
/// Implementations are called by ConfigurableSubjectSerializer.
/// </summary>
public interface IAdditionalPropertiesSerializer
{
    /// <summary>
    /// Returns additional properties to serialize with the subject.
    /// Keys are plain names (e.g., "authorization") - serializer adds "$" prefix.
    /// Return null if no data to add.
    /// </summary>
    Dictionary<string, object>? GetAdditionalProperties(IInterceptorSubject subject);

    /// <summary>
    /// Receives additional "$" properties from deserialized JSON.
    /// Keys have "$" prefix stripped (e.g., "$authorization" -> "authorization").
    /// Implementation picks out the ones it handles and ignores the rest.
    /// May receive null if no additional properties were present in the JSON.
    /// </summary>
    void SetAdditionalProperties(IInterceptorSubject subject, Dictionary<string, JsonElement>? properties);
}
