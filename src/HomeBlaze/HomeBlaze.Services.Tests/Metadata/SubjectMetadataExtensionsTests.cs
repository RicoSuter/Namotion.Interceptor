using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Services.Metadata;
using HomeBlaze.Services.Tests.Models;
using HomeBlaze.Storage.Abstractions.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Services.Tests.Metadata;

public class SubjectMetadataExtensionsTests
{
    [Fact]
    public void WhenSubjectIsNotRegistered_ThenItHasNoMetadata()
    {
        // Arrange
        var subject = new MetadataTestSubject();

        // Act
        var metadata = subject.GetMetadata();

        // Assert
        Assert.Null(metadata);
    }

    [Fact]
    public void WhenSubjectAndPropertyHaveMetadata_ThenTheirValuesAreRead()
    {
        // Arrange
        var subject = new MetadataTestSubject(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry());
        TestMetadata.Set(subject, """{ "navTitle": "Pump" }""");
        var name = subject.TryGetRegisteredSubject()!.TryGetProperty(nameof(MetadataTestSubject.Name))!;
        var values = TestMetadata.Parse("""{ "comment": "set by hand" }""");
        name.AddAttribute(KnownAttributes.Metadata, values.GetType(), _ => values, null);

        // Act
        var subjectMetadata = subject.GetMetadata();
        var propertyMetadata = name.GetMetadata();

        // Assert
        Assert.Equal("Pump", subjectMetadata.GetString(MetadataKeys.NavigationTitle));
        Assert.Equal("set by hand", propertyMetadata.GetString("comment"));
    }

    [Fact]
    public void WhenPropertiesAreChecked_ThenOnlyTheMetadataOfTheSubjectAndOfAPropertyIsMetadata()
    {
        // Arrange
        var subject = new MetadataTestSubject(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry());
        TestMetadata.Set(subject, """{ "navTitle": "Pump" }""");
        var registered = subject.TryGetRegisteredSubject()!;
        var name = registered.TryGetProperty(nameof(MetadataTestSubject.Name))!;
        var attribute = name.AddAttribute(KnownAttributes.Metadata, typeof(object), _ => null, null);

        // Act & Assert
        Assert.True(registered.TryGetProperty(KnownAttributes.Metadata)!.IsMetadata());
        Assert.True(attribute.IsMetadata());
        Assert.False(name.IsMetadata());
    }

    [Fact]
    public void WhenTheStoreOfASubjectIsFound_ThenItIsTheSubjectItselfWhenItIsOneElseTheNearestOneUpItsParents()
    {
        // Arrange
        var child = new MetadataTestSubject();
        var inner = new MetadataTestStore { Child = child };
        var outer = new MetadataTestStore(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry()) { Inner = inner };

        // Act
        var storeOfChild = child.FindMetadataStore();
        var storeOfInner = inner.FindMetadataStore();
        var storeOfOuter = outer.FindMetadataStore();

        // Assert
        Assert.Same(inner, storeOfChild);
        Assert.Same(inner, storeOfInner);
        Assert.Same(outer, storeOfOuter);
    }

    [Fact]
    public void WhenTheStoreIsFarUpTheParents_ThenItIsFound()
    {
        // Arrange
        var leaf = new TestContainer();
        var top = leaf;
        for (var depth = 0; depth < 300; depth++)
        {
            top = new TestContainer { Child = top };
        }

        var store = new MetadataTestStore(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry()) { Container = top };

        // Act
        var found = leaf.FindMetadataStore();

        // Assert
        Assert.Same(store, found);
    }

    [Fact]
    public void WhenTheFirstParentsFormACycleWithoutAStore_ThenNoStoreIsFound()
    {
        // Arrange: subjects that only reference each other, held in the graph by an anchor on one of them, have
        // each other as their only parents, so their first parents form a cycle without a store on it.
        var first = new TestContainer();
        var second = new TestContainer { Child = first };
        first.Child = second;
        ((IInterceptorSubject)first).AttachToContext(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry());

        // Act
        var found = first.FindMetadataStore();

        // Assert
        Assert.NotNull(first.TryGetRegisteredSubject());
        Assert.Null(found);
    }
}

[InterceptorSubject]
public partial class MetadataTestSubject
{
    public partial string? Name { get; set; }
}

[InterceptorSubject]
public partial class MetadataTestStore : IMetadataStore
{
    public partial MetadataTestStore? Inner { get; set; }

    public partial MetadataTestSubject? Child { get; set; }

    public partial TestContainer? Container { get; set; }

    public bool CanStoreMetadata(IInterceptorSubject subject) => true;

    public void ApplyStoredMetadata(IInterceptorSubject subject, string? propertyName)
    {
    }

    public Task SetMetadataAsync(IInterceptorSubject subject, string? propertyName, JsonElement values, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
