using System.Linq.Expressions;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Connectors.Tests.Mapping;

public class FluentPropertyMapperBaseTests
{
    private sealed record TestMapping(string? Value);

    private sealed class TestFluent<TSubject> : FluentPropertyMapperBase<TSubject, TestMapping>
    {
        public TestFluent<TSubject> Set<TValue>(Expression<Func<TSubject, TValue>> selector, TestMapping mapping)
        {
            SetMapping(selector, mapping);
            return this;
        }
    }

    [Fact]
    public void WhenPropertyConfigured_ThenTryGetMappingReturnsConfiguredValue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context) { Father = new Person(context) { FirstName = "Dad" } };
        var fatherProperty = person.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.Father))!;
        var fatherSubject = fatherProperty.Children.Single().Subject!.TryGetRegisteredSubject()!;
        var firstNameProperty = fatherSubject.TryGetProperty(nameof(Person.FirstName))!;

        var fluent = new TestFluent<Person>().Set(p => p.Father!.FirstName!, new TestMapping("CONFIGURED"));

        // Act
        var found = fluent.TryGetMapping(firstNameProperty, out var mapping);

        // Assert
        Assert.True(found);
        Assert.Equal("CONFIGURED", mapping!.Value);
    }

    [Fact]
    public void WhenPropertyNotConfigured_ThenTryGetMappingReturnsFalse()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context) { Father = new Person(context) };
        var fatherProperty = person.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.Father))!;
        var fatherSubject = fatherProperty.Children.Single().Subject!.TryGetRegisteredSubject()!;
        var firstNameProperty = fatherSubject.TryGetProperty(nameof(Person.FirstName))!;

        // Act
        var found = new TestFluent<Person>().TryGetMapping(firstNameProperty, out var mapping);

        // Assert
        Assert.False(found);
        Assert.Null(mapping);
    }
}
