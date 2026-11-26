using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Authorization.Tests;

/// <summary>
/// Registers metadata the way the storage does: a dynamic property of the subject, or an attribute of a property.
/// </summary>
internal static class TestMetadata
{
    public static RegisteredSubjectProperty AddSubjectMetadata(RegisteredSubject subject)
    {
        var values = ImmutableDictionary<string, JsonElement>.Empty;
        return subject.AddProperty(KnownAttributes.Metadata, values.GetType(), _ => values, null);
    }

    public static RegisteredSubjectProperty AddPropertyMetadata(RegisteredSubject subject, string propertyName)
    {
        var values = ImmutableDictionary<string, JsonElement>.Empty;
        return subject.TryGetProperty(propertyName)!.AddAttribute(KnownAttributes.Metadata, values.GetType(), _ => values, null);
    }
}
