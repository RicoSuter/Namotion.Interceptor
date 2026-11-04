using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Services.Tests;

/// <summary>Gives a subject metadata in the property where a store puts it.</summary>
public static class TestMetadata
{
    public static ImmutableDictionary<string, JsonElement> Parse(string json)
        => JsonElement.Parse(json).EnumerateObject()
            .ToImmutableDictionary(property => property.Name, property => property.Value);

    public static void Set(IInterceptorSubject subject, string json)
    {
        var values = Parse(json);
        subject.TryGetRegisteredSubject()!.AddProperty(KnownAttributes.Metadata, typeof(ImmutableDictionary<string, JsonElement>), _ => values, null);
    }
}
