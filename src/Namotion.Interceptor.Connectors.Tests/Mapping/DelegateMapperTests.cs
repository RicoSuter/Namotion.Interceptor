using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Connectors.Tests.Mapping;

public class DelegateMapperTests
{
    private sealed record TestMapping(string Value);

    [Fact]
    public void WhenSelectorReturnsValue_ThenTryGetMappingReturnsTrue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context) { FirstName = "x" };
        var nameProperty = person.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.FirstName))!;

        var mapper = new DelegateMapper<TestMapping>(p => new TestMapping(p.Name));

        // Act
        var found = mapper.TryGetMapping(nameProperty, out var mapping);

        // Assert
        Assert.True(found);
        Assert.Equal("FirstName", mapping!.Value);
    }

    [Fact]
    public void WhenSelectorReturnsNull_ThenTryGetMappingReturnsFalse()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        var nameProperty = person.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.FirstName))!;

        var mapper = new DelegateMapper<TestMapping>(_ => null);

        // Act
        var found = mapper.TryGetMapping(nameProperty, out var mapping);

        // Assert
        Assert.False(found);
        Assert.Null(mapping);
    }
}
