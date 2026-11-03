using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tests;

/// <summary>
/// Path provider whose segment is the browse name, which excludes the property with the given name from paths.
/// </summary>
internal sealed class ExcludingPathProvider(string excludedPropertyName) : PathProviderBase
{
    public override bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
    {
        segment = property.Name != excludedPropertyName ? property.BrowseName : null;
        return segment is not null;
    }
}
