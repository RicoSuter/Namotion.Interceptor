using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Tests.Mapping;

public class CompositeMapperTests
{
    private sealed record TestMapping(string? A, int? B) : IPropertyMapping<TestMapping>
    {
        public static TestMapping Merge(TestMapping primary, TestMapping fallback) =>
            new(primary.A ?? fallback.A, primary.B ?? fallback.B);
    }

    private static (RegisteredSubjectProperty Property, IInterceptorSubject Subject) NameProperty()
    {
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context) { FirstName = "x" };
        return (person.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.FirstName))!, person);
    }

    [Fact]
    public void WhenAllInnerMappersReturnNull_ThenCompositeReturnsFalse()
    {
        // Arrange
        var mapper = new CompositeMapper<TestMapping>(
            new DelegateMapper<TestMapping>((_, _) => null),
            new DelegateMapper<TestMapping>((_, _) => null));

        // Act
        var (property, subject) = NameProperty();
        var found = mapper.TryGetMapping(property, subject, out var mapping);

        // Assert
        Assert.False(found);
        Assert.Null(mapping);
    }

    [Fact]
    public void WhenLaterMapperHasFields_ThenItOverridesEarlierMapperFields()
    {
        // Arrange
        var mapper = new CompositeMapper<TestMapping>(
            new DelegateMapper<TestMapping>((_, _) => new TestMapping(A: "first", B: 1)),
            new DelegateMapper<TestMapping>((_, _) => new TestMapping(A: "second", B: null)));

        // Act
        var (property, subject) = NameProperty();
        mapper.TryGetMapping(property, subject, out var mapping);

        // Assert
        Assert.NotNull(mapping);
        Assert.Equal("second", mapping.A);
        Assert.Equal(1, mapping.B);
    }

    [Fact]
    public void WhenAnInnerMapperIsNull_ThenConstructorThrows()
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            new CompositeMapper<TestMapping>(
                new DelegateMapper<TestMapping>((_, _) => null),
                null!));

        Assert.Equal("mappers", exception.ParamName);
    }

    [Fact]
    public void WhenExplicitMergerProvided_ThenItOverridesDefaultMerge()
    {
        // Arrange
        var mapper = new CompositeMapper<TestMapping>(
            (primary, fallback) => new TestMapping(A: fallback.A, B: primary.B ?? fallback.B),
            new DelegateMapper<TestMapping>((_, _) => new TestMapping(A: "first", B: 1)),
            new DelegateMapper<TestMapping>((_, _) => new TestMapping(A: "second", B: 2)));

        // Act
        var (property, subject) = NameProperty();
        mapper.TryGetMapping(property, subject, out var mapping);

        // Assert
        Assert.NotNull(mapping);
        Assert.Equal("first", mapping.A);
        Assert.Equal(2, mapping.B);
    }
}
