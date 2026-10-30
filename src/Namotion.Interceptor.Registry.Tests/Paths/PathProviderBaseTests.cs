using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests.Paths;

public class PathProviderBaseTests
{
    [Theory]
    [InlineData(nameof(Person.FirstName), false, false, false)]
    [InlineData(nameof(Person.FirstName_MaxLength), true, false, false)]
    [InlineData(nameof(Person.FirstName_MaxLength_Unit), true, true, false)]
    [InlineData(nameof(Person.LastName), true, true, true)]
    public void WhenAPropertyIsExcluded_ThenPathsPassNeitherItNorTheAttributesBelongingToIt(
        string excludedPropertyName, bool firstNameIncluded, bool maxLengthIncluded, bool unitIncluded)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Ann" };
        var pathProvider = new ExcludingPathProvider(excludedPropertyName);
        var firstName = person.TryGetRegisteredProperty(p => p.FirstName)!;
        var maxLength = firstName.TryGetAttribute("MaxLength")!;
        var unit = maxLength.TryGetAttribute("Unit")!;

        // Act
        var firstNameResult = pathProvider.IsPropertyIncludedInPaths(firstName);
        var maxLengthResult = pathProvider.IsPropertyIncludedInPaths(maxLength);
        var unitResult = pathProvider.IsPropertyIncludedInPaths(unit);

        // Assert
        Assert.Equal(firstNameIncluded, firstNameResult);
        Assert.Equal(maxLengthIncluded, maxLengthResult);
        Assert.Equal(unitIncluded, unitResult);
        Assert.Equal(unitIncluded, unit.TryGetPath(pathProvider, person) is not null);
    }

    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();
    }

    private sealed class ExcludingPathProvider(string excludedPropertyName) : PathProviderBase
    {
        public override bool IsPropertyIncluded(RegisteredSubjectProperty property) => property.Name != excludedPropertyName;
    }
}
