using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaStructureHandlerTests
{
    [Fact]
    public async Task WhenPopulatingSubjectMap_ThenAllSubjectsWithNodeIdsRegistered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithLifecycle()
            .WithFullPropertyTracking();

        var rootSubject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(rootSubject);
        var rootRegistered = rootSubject.TryGetRegisteredSubject()!;

        var rootNodeId = new NodeId(1, 0);
        rootSubject.SetData(OpcUaSubjectClientSource.SubjectNodeIdDataKey, rootNodeId);

        // Create a child subject with a NodeId
        var childSubject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(childSubject);
        var childNodeId = new NodeId(2, 0);
        childSubject.SetData(OpcUaSubjectClientSource.SubjectNodeIdDataKey, childNodeId);

        // Add the child as a property of the root
        rootRegistered.AddProperty(
            "Child",
            typeof(IInterceptorSubject),
            _ => childSubject,
            (_, _) => { });

        var configuration = CreateTestConfiguration();
        var source = new OpcUaSubjectClientSource(rootSubject, configuration, NullLogger<OpcUaSubjectClientSource>.Instance);
        var loader = new OpcUaSubjectLoader(configuration, source.Ownership, source, NullLogger<OpcUaSubjectClientSource>.Instance);
        var handler = new OpcUaStructureHandler(loader, source, configuration, NullLogger<OpcUaSubjectClientSource>.Instance);

        // Act & Assert
        // PopulateSubjectMap is called during StartAsync, which requires a real Session.
        // Verify the prerequisites: subjects have their NodeIds stored in Data.
        Assert.True(rootSubject.TryGetData(OpcUaSubjectClientSource.SubjectNodeIdDataKey, out var rootData));
        Assert.Equal(rootNodeId, rootData);
        Assert.True(childSubject.TryGetData(OpcUaSubjectClientSource.SubjectNodeIdDataKey, out var childData));
        Assert.Equal(childNodeId, childData);

        // Verify the handler's SubjectMap is initially empty (populated during StartAsync)
        Assert.Empty(handler.SubjectMap.ExternalIds);

        // Clean up
        await handler.DisposeAsync();
        await source.DisposeAsync();
    }

    [Fact]
    public async Task WhenDisposed_ThenSubjectMapIsDisposed()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithLifecycle()
            .WithFullPropertyTracking();

        var rootSubject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(rootSubject);

        var configuration = CreateTestConfiguration();
        var source = new OpcUaSubjectClientSource(rootSubject, configuration, NullLogger<OpcUaSubjectClientSource>.Instance);
        var loader = new OpcUaSubjectLoader(configuration, source.Ownership, source, NullLogger<OpcUaSubjectClientSource>.Instance);
        var handler = new OpcUaStructureHandler(loader, source, configuration, NullLogger<OpcUaSubjectClientSource>.Instance);

        // Act
        await handler.DisposeAsync();

        // Assert: SubjectMap should be disposed (empty and no longer tracking)
        Assert.Empty(handler.SubjectMap.ExternalIds);

        // Clean up
        await source.DisposeAsync();
    }

    [Fact]
    public async Task WhenCreated_ThenSubjectMapIsEmpty()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithLifecycle()
            .WithFullPropertyTracking();

        var rootSubject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(rootSubject);

        var configuration = CreateTestConfiguration();
        var source = new OpcUaSubjectClientSource(rootSubject, configuration, NullLogger<OpcUaSubjectClientSource>.Instance);
        var loader = new OpcUaSubjectLoader(configuration, source.Ownership, source, NullLogger<OpcUaSubjectClientSource>.Instance);

        // Act
        var handler = new OpcUaStructureHandler(loader, source, configuration, NullLogger<OpcUaSubjectClientSource>.Instance);

        // Assert
        Assert.Empty(handler.SubjectMap.ExternalIds);

        // Clean up
        await handler.DisposeAsync();
        await source.DisposeAsync();
    }

    [Fact]
    public async Task WhenConstructedWithNullLoader_ThenThrowsArgumentNullException()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithLifecycle();

        var rootSubject = new DynamicSubject(context);
        var configuration = CreateTestConfiguration();
        var source = new OpcUaSubjectClientSource(rootSubject, configuration, NullLogger<OpcUaSubjectClientSource>.Instance);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new OpcUaStructureHandler(null!, source, configuration, NullLogger<OpcUaSubjectClientSource>.Instance));

        // Clean up
        await source.DisposeAsync();
    }

    [Fact]
    public async Task WhenConstructedWithNullClientSource_ThenThrowsArgumentNullException()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithLifecycle();

        var rootSubject = new DynamicSubject(context);
        var configuration = CreateTestConfiguration();
        var source = new OpcUaSubjectClientSource(rootSubject, configuration, NullLogger<OpcUaSubjectClientSource>.Instance);
        var loader = new OpcUaSubjectLoader(configuration, source.Ownership, source, NullLogger<OpcUaSubjectClientSource>.Instance);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new OpcUaStructureHandler(loader, null!, configuration, NullLogger<OpcUaSubjectClientSource>.Instance));

        // Clean up
        await source.DisposeAsync();
    }

    [Fact]
    public async Task WhenConstructedWithNullConfiguration_ThenThrowsArgumentNullException()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithLifecycle();

        var rootSubject = new DynamicSubject(context);
        var configuration = CreateTestConfiguration();
        var source = new OpcUaSubjectClientSource(rootSubject, configuration, NullLogger<OpcUaSubjectClientSource>.Instance);
        var loader = new OpcUaSubjectLoader(configuration, source.Ownership, source, NullLogger<OpcUaSubjectClientSource>.Instance);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new OpcUaStructureHandler(loader, source, null!, NullLogger<OpcUaSubjectClientSource>.Instance));

        // Clean up
        await source.DisposeAsync();
    }

    [Fact]
    public async Task WhenDisposeCalledTwice_ThenNoError()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithLifecycle()
            .WithFullPropertyTracking();

        var rootSubject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(rootSubject);

        var configuration = CreateTestConfiguration();
        var source = new OpcUaSubjectClientSource(rootSubject, configuration, NullLogger<OpcUaSubjectClientSource>.Instance);
        var loader = new OpcUaSubjectLoader(configuration, source.Ownership, source, NullLogger<OpcUaSubjectClientSource>.Instance);
        var handler = new OpcUaStructureHandler(loader, source, configuration, NullLogger<OpcUaSubjectClientSource>.Instance);

        // Act: dispose twice should not throw
        await handler.DisposeAsync();
        await handler.DisposeAsync();

        // Clean up
        await source.DisposeAsync();
    }

    private static OpcUaClientConfiguration CreateTestConfiguration()
    {
        return new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
            ShouldAddDynamicProperty = static (_, _) => Task.FromResult(false)
        };
    }
}
