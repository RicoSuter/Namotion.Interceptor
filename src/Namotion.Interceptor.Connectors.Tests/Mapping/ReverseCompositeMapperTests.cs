using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Tests.Mapping;

public class ReverseCompositeMapperTests
{
    // Two nullable fields so a same-field conflict makes "last wins" observable, and a
    // complementary merge (each mapper fills a different field) is also testable.
    private sealed record TestMapping(string? Topic, int? Qos) : IPropertyMapping<TestMapping>
    {
        public static TestMapping Merge(TestMapping primary, TestMapping fallback) =>
            new(primary.Topic ?? fallback.Topic, primary.Qos ?? fallback.Qos);
    }

    private sealed class FakeMapper(
        TestMapping? forward = null,
        string? reverseKey = null,
        RegisteredSubjectProperty? reverseResult = null)
        : IReversePropertyMapper<TestMapping, string>
    {
        public bool TryGetMapping(
            RegisteredSubjectProperty property,
            IInterceptorSubject rootSubject,
            [NotNullWhen(true)] out TestMapping? mapping)
        {
            mapping = forward;
            return mapping is not null;
        }

        public ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
            string key, RegisteredSubject subject, CancellationToken cancellationToken)
            => new(key == reverseKey ? reverseResult : null);
    }

    // Concrete subclass to instantiate the protected-constructor composite from tests.
    private sealed class TestComposite(params IReversePropertyMapper<TestMapping, string>[] mappers)
        : ReverseCompositeMapper<TestMapping, string>(mappers);

    private static (RegisteredSubject Subject, IInterceptorSubject Root) CreateSubject()
    {
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context) { FirstName = "Test", LastName = "Person" };
        return (person.TryGetRegisteredSubject()!, person);
    }

    [Fact]
    public void WhenMappersIsEmpty_ThenForwardReturnsFalse()
    {
        // Arrange
        var (subject, root) = CreateSubject();
        var property = subject.TryGetProperty("FirstName")!;
        var composite = new TestComposite();

        // Act
        var found = composite.TryGetMapping(property, root, out var mapping);

        // Assert
        Assert.False(found);
        Assert.Null(mapping);
    }

    [Fact]
    public void WhenMapperElementIsNull_ThenConstructorThrows()
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            new TestComposite(new FakeMapper(), null!));
        Assert.Equal("mappers", exception.ParamName);
    }

    [Fact]
    public void WhenMappersArrayIsNull_ThenConstructorThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new TestComposite((IReversePropertyMapper<TestMapping, string>[])null!));
    }

    [Fact]
    public void WhenTwoMappersConflictOnSameField_ThenLaterMapperWins()
    {
        // Arrange - both set Topic; only the earlier sets Qos
        var (subject, root) = CreateSubject();
        var property = subject.TryGetProperty("FirstName")!;
        var earlier = new FakeMapper(new TestMapping(Topic: "early", Qos: 1));
        var later = new FakeMapper(new TestMapping(Topic: "late", Qos: null));
        var composite = new TestComposite(earlier, later);

        // Act
        var found = composite.TryGetMapping(property, root, out var mapping);

        // Assert - later wins on the conflicting field, the non-conflicting field falls through
        Assert.True(found);
        Assert.Equal("late", mapping!.Topic);
        Assert.Equal(1, mapping.Qos);
    }

    [Fact]
    public void WhenMappersAreComplementary_ThenFieldsMerge()
    {
        // Arrange - one mapper provides Topic, the other Qos
        var (subject, root) = CreateSubject();
        var property = subject.TryGetProperty("FirstName")!;
        var topicMapper = new FakeMapper(new TestMapping(Topic: "t", Qos: null));
        var qosMapper = new FakeMapper(new TestMapping(Topic: null, Qos: 5));
        var composite = new TestComposite(topicMapper, qosMapper);

        // Act
        var found = composite.TryGetMapping(property, root, out var mapping);

        // Assert
        Assert.True(found);
        Assert.Equal("t", mapping!.Topic);
        Assert.Equal(5, mapping.Qos);
    }

    [Fact]
    public void WhenThreeMappersChainMerges_ThenLastWinsAndUnsetFieldsFallThrough()
    {
        // Arrange - three mappers in order. Only the first sets Qos; all three set Topic.
        // This exercises the chained accumulation (Merge over an already-merged value), which a
        // two-mapper test does not: the second Merge receives the first Merge's result as fallback.
        var (subject, root) = CreateSubject();
        var property = subject.TryGetProperty("FirstName")!;
        var first = new FakeMapper(new TestMapping(Topic: "a", Qos: 1));
        var middle = new FakeMapper(new TestMapping(Topic: "b", Qos: null));
        var last = new FakeMapper(new TestMapping(Topic: "c", Qos: null));
        var composite = new TestComposite(first, middle, last);

        // Act
        var found = composite.TryGetMapping(property, root, out var mapping);

        // Assert - the last mapper's Topic wins across the whole chain, while the first mapper's
        // Qos survives both merges because neither later mapper overrides it.
        Assert.True(found);
        Assert.Equal("c", mapping!.Topic);
        Assert.Equal(1, mapping.Qos);
    }

    [Fact]
    public async Task WhenTwoMappersResolveSameReverseKey_ThenLaterMapperWins()
    {
        // Arrange - both resolve "k", to different properties; later mapper should win
        var (subject, _) = CreateSubject();
        var firstName = subject.TryGetProperty("FirstName")!;
        var lastName = subject.TryGetProperty("LastName")!;
        var earlier = new FakeMapper(reverseKey: "k", reverseResult: firstName);
        var later = new FakeMapper(reverseKey: "k", reverseResult: lastName);
        var composite = new TestComposite(earlier, later);

        // Act
        var result = await composite.TryGetPropertyAsync("k", subject, CancellationToken.None);

        // Assert - reverse-order iteration returns the later mapper's match
        Assert.Same(lastName, result);
    }

    [Fact]
    public async Task WhenOnlyEarlierMapperResolvesReverseKey_ThenReturnsThatProperty()
    {
        // Arrange
        var (subject, _) = CreateSubject();
        var firstName = subject.TryGetProperty("FirstName")!;
        var earlier = new FakeMapper(reverseKey: "k", reverseResult: firstName);
        var later = new FakeMapper(reverseKey: "other", reverseResult: subject.TryGetProperty("LastName"));
        var composite = new TestComposite(earlier, later);

        // Act
        var result = await composite.TryGetPropertyAsync("k", subject, CancellationToken.None);

        // Assert
        Assert.Same(firstName, result);
    }

    [Fact]
    public async Task WhenNoMapperResolvesReverseKey_ThenReturnsNull()
    {
        // Arrange
        var (subject, _) = CreateSubject();
        var composite = new TestComposite(
            new FakeMapper(reverseKey: "a", reverseResult: subject.TryGetProperty("FirstName")),
            new FakeMapper(reverseKey: "b", reverseResult: subject.TryGetProperty("LastName")));

        // Act
        var result = await composite.TryGetPropertyAsync("missing", subject, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }
}
