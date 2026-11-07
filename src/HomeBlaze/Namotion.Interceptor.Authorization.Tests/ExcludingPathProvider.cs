using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Authorization.Tests;

/// <summary>Leaves every property of the given name out of paths.</summary>
internal sealed class ExcludingPathProvider(string excludedPropertyName, char pathSeparator = '.') : PathProviderBase
{
    public override char PathSeparator => pathSeparator;

    public override bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
    {
        segment = property.Name != excludedPropertyName ? property.BrowseName : null;
        return segment is not null;
    }
}
