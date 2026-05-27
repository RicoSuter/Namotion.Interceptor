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

    private static RegisteredSubjectProperty NameProperty()
    {
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context) { FirstName = "x" };
        return person.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.FirstName))!;
    }

    [Fact]
    public void WhenAllInnerMappersReturnNull_ThenCompositeReturnsFalse()
    {
        // Arrange
        var mapper = new CompositeMapper<TestMapping>(
            new DelegateMapper<TestMapping>(_ => null),
            new DelegateMapper<TestMapping>(_ => null));

        // Act
        var found = mapper.TryGetMapping(NameProperty(), out var mapping);

        // Assert
        Assert.False(found);
        Assert.Null(mapping);
    }

    [Fact]
    public void WhenLaterMapperHasFields_ThenItOverridesEarlierMapperFields()
    {
        // Arrange
        var mapper = new CompositeMapper<TestMapping>(
            new DelegateMapper<TestMapping>(_ => new TestMapping(A: "first", B: 1)),
            new DelegateMapper<TestMapping>(_ => new TestMapping(A: "second", B: null)));

        // Act
        mapper.TryGetMapping(NameProperty(), out var mapping);

        // Assert
        Assert.NotNull(mapping);
        Assert.Equal("second", mapping.A);
        Assert.Equal(1, mapping.B);
    }

    [Fact]
    public void WhenExplicitMergerProvided_ThenItOverridesDefaultMerge()
    {
        // Arrange
        var mapper = new CompositeMapper<TestMapping>(
            (primary, fallback) => new TestMapping(A: fallback.A, B: primary.B ?? fallback.B),
            new DelegateMapper<TestMapping>(_ => new TestMapping(A: "first", B: 1)),
            new DelegateMapper<TestMapping>(_ => new TestMapping(A: "second", B: 2)));

        // Act
        mapper.TryGetMapping(NameProperty(), out var mapping);

        // Assert
        Assert.NotNull(mapping);
        Assert.Equal("first", mapping.A);
        Assert.Equal(2, mapping.B);
    }
}
