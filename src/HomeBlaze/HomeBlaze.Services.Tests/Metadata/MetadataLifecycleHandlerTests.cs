using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services.Lifecycle;
using HomeBlaze.Services.Metadata;
using HomeBlaze.Storage.Abstractions.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Tests.Metadata;

public class MetadataLifecycleHandlerTests
{
    private static LifecycleTestStore CreateStore()
        => new(InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithService<ILifecycleHandler>(() => new MetadataLifecycleHandler(), handler => handler is MetadataLifecycleHandler));

    [Fact]
    public void WhenASubjectAttachesBelowAStore_ThenTheStoreAppliesItOnce()
    {
        // Arrange
        var store = CreateStore();
        var child = new LifecycleTestChild();

        // Act
        store.Child = child;

        // Assert
        var applied = Assert.Single(store.Applied);
        Assert.Equal((child, null), applied);
    }

    [Fact]
    public void WhenASubjectWithDynamicPropertiesAttachesAgain_ThenTheStoreAppliesItOnceAndAPropertyAddedAfterItself()
    {
        // Arrange
        var store = CreateStore();
        var child = new LifecycleTestChild();
        store.Child = child;
        child.TryGetRegisteredSubject()!.AddProperty("Extra", typeof(int), _ => 1, null);
        store.Child = null;
        store.Applied.Clear();

        // Act
        store.Child = child;
        child.TryGetRegisteredSubject()!.AddProperty("Later", typeof(int), _ => 2, null);

        // Assert
        Assert.Equal([(child, null), (child, "Later")], store.Applied);
    }

    [Fact]
    public void WhenAPropertyIsAddedToAnAttachedSubject_ThenTheStoreAppliesItButNotMetadataOrAnAttribute()
    {
        // Arrange
        var store = CreateStore();
        var child = new LifecycleTestChild();
        store.Child = child;
        store.Applied.Clear();
        var registered = child.TryGetRegisteredSubject()!;
        var values = ImmutableDictionary<string, JsonElement>.Empty;

        // Act
        registered.AddProperty(KnownAttributes.Metadata, values.GetType(), _ => values, null);
        registered.TryGetProperty(nameof(LifecycleTestChild.Name))!.AddAttribute("Unit", typeof(string), _ => "none", null);
        registered.AddProperty("Extra", typeof(int), _ => 1, null);

        // Assert
        var applied = Assert.Single(store.Applied);
        Assert.Equal((child, "Extra"), applied);
    }

    [Fact]
    public void WhenAMethodPropertyIsAddedToAnAttachedSubject_ThenTheStoreDoesNotApplyIt()
    {
        // Arrange
        var store = CreateStore();
        var child = new LifecycleTestChild();
        store.Child = child;
        store.Applied.Clear();

        // Act
        child.TryGetRegisteredSubject()!.AddProperty("Run", typeof(MethodMetadata), _ => null, null);

        // Assert
        Assert.Empty(store.Applied);
    }

    [Fact]
    public void WhenRegisteredBeforeTheMethodPropertyInitializer_ThenItRunsAfterIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithService<ILifecycleHandler>(() => new MetadataLifecycleHandler(), handler => handler is MetadataLifecycleHandler)
            .WithService<ILifecycleHandler>(() => new MethodPropertyInitializer(), handler => handler is MethodPropertyInitializer);

        // Act
        var handlers = context.GetServices<ILifecycleHandler>();

        // Assert
        Assert.IsType<MethodPropertyInitializer>(handlers[0]);
        Assert.IsType<MetadataLifecycleHandler>(handlers[1]);
    }
}

[InterceptorSubject]
public partial class LifecycleTestStore : IMetadataStore
{
    public partial LifecycleTestChild? Child { get; set; }

    public List<(IInterceptorSubject Subject, string? PropertyName)> Applied { get; } = [];

    public bool CanStoreMetadata(IInterceptorSubject subject) => true;

    public void ApplyStoredMetadata(IInterceptorSubject subject, string? propertyName)
        => Applied.Add((subject, propertyName));

    public Task SetMetadataAsync(IInterceptorSubject subject, string? propertyName, JsonElement values, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

[InterceptorSubject]
public partial class LifecycleTestChild
{
    public partial string? Name { get; set; }
}
