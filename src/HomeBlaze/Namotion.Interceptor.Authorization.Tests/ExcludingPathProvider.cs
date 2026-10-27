using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Authorization.Tests;

/// <summary>Leaves every property of the given name out of paths.</summary>
internal sealed class ExcludingPathProvider(string excludedPropertyName, char pathSeparator = '.') : PathProviderBase
{
    public override char PathSeparator => pathSeparator;

    public override bool IsPropertyIncluded(RegisteredSubjectProperty property) => property.Name != excludedPropertyName;
}
