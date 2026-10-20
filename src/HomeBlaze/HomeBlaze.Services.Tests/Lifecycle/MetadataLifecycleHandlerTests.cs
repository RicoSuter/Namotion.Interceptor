using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Tests.Lifecycle;

public class MetadataLifecycleHandlerTests
{
    private static LifecycleTestStore CreateStore()
        => new(InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithService<ILifecycleHandler>(() => new MetadataLifecycleHandler(), handler => handler is MetadataLifecycleHandler)
            .WithService<IPropertyLifecycleHandler>(() => new MetadataLifecycleHandler(), handler => handler is MetadataLifecycleHandler));

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
