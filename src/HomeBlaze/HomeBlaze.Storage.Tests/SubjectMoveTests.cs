using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Storage.Tests;

public class SubjectMoveTests : IDisposable
{
    private readonly DirectoryInfo _storageDirectory = Directory.CreateTempSubdirectory("homeblaze-storage-");
    private readonly List<FluentStorageContainer> _storages = [];

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task WhenDeviceMovesToParentFolderAndItsOldFolderIsDeleted_ThenItIsStillReachableThroughTheGraph(
        bool isOnePass, bool hasContextConstructor)
    {
        // Arrange: the device is the only child of the kitchen folder. Without a constructor that takes the context it
        // has its context from the folder that holds it only.
        WriteFile("House/OG/Kitchen/Dishwasher.json", hasContextConstructor
            ? InjectedDevice.Serialize("Dishwasher")
            : UnanchoredInjectedDevice.Serialize("Dishwasher"));
        WriteFile("House/OG/Freezer.json", InjectedDevice.Serialize("Freezer"));
        var (storage, context, resolver) = await ConnectAsync();
        var house = Assert.IsType<VirtualFolder>(storage.Children["House"]);
        var upperFloor = Assert.IsType<VirtualFolder>(house.Children["OG"]);
        var kitchen = Assert.IsType<VirtualFolder>(upperFloor.Children["Kitchen"]);
        var freezer = upperFloor.Children["Freezer"];
        var device = kitchen.Children["Dishwasher"];
        Assert.IsType(hasContextConstructor ? typeof(InjectedDevice) : typeof(UnanchoredInjectedDevice), device);

        var powerChanges = new ConcurrentQueue<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                if (ReferenceEquals(change.Property.Subject, device) && change.Property.Name == "Power")
                {
                    powerChanges.Enqueue(change);
                }
            });

        // Act: the folder assignments a reconciling storage makes when the file moves and the empty kitchen folder
        // is deleted. Everything is added before anything is removed, so the device is held by both folders for a
        // moment. In one pass the kitchen folder is released while it still holds the device.
        upperFloor.Children = new Dictionary<string, IInterceptorSubject>
        {
            ["Kitchen"] = kitchen,
            ["Freezer"] = freezer,
            ["Dishwasher"] = device
        };
        if (!isOnePass)
        {
            kitchen.Children = new Dictionary<string, IInterceptorSubject>();
        }

        upperFloor.Children = new Dictionary<string, IInterceptorSubject>
        {
            ["Freezer"] = freezer,
            ["Dishwasher"] = device
        };
        if (device is InjectedDevice anchoredDevice)
        {
            anchoredDevice.Power = 42;
        }
        else
        {
            ((UnanchoredInjectedDevice)device).Power = 42;
        }

        // Assert: placed in the upper floor folder only, the kitchen folder is gone.
        Assert.Equal(["Dishwasher", "Freezer"], upperFloor.Children.Keys.Order());
        Assert.Same(device, upperFloor.Children["Dishwasher"]);
        Assert.Null(kitchen.TryGetRegisteredSubject());

        // Assert: registered and resolvable through its canonical path.
        var registeredDevice = device.TryGetRegisteredSubject();
        Assert.NotNull(registeredDevice);
        var parent = Assert.Single(registeredDevice.Parents);
        Assert.Same(upperFloor, parent.Property.Subject);
        Assert.Same(device, resolver.ResolveSubject("/House/OG/Dishwasher", PathStyle.Canonical));
        Assert.Equal(42d, resolver.ResolveValue("/House/OG/Dishwasher/Power", PathStyle.Canonical));

        // Assert: it has the services of the graph: registry, lifecycle and change tracking.
        Assert.Same(context, device.TryGetContext());
        Assert.Same(context.TryGetService<ISubjectRegistry>(), device.TryGetContext()?.TryGetService<ISubjectRegistry>());
        Assert.Same(upperFloor, Assert.Single(device.GetParents()).Property.Subject);
        Assert.Equal(1, device.GetReferenceCount());
        var powerChange = Assert.Single(powerChanges);
        Assert.Equal(42d, powerChange.GetNewValue<double?>());
    }

    public void Dispose()
    {
        foreach (var storage in _storages)
        {
            storage.Dispose();
        }

        _storageDirectory.Delete(recursive: true);
    }

    private async Task<(FluentStorageContainer Storage, IInterceptorSubjectContext Context, SubjectPathResolver Resolver)> ConnectAsync()
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(FluentStorageContainer).Assembly);
        typeProvider.AddAssembly(typeof(InjectedDevice).Assembly);
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        // The application's context, so that the Children setters run change tracking, registry and lifecycle code.
        var services = new ServiceCollection();
        var context = SubjectContextFactory.Create(services);
        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton(context);
        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        var serviceProvider = services.BuildServiceProvider();

        var storage = new FluentStorageContainer(
            typeRegistry,
            serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(),
            serviceProvider)
        {
            ConnectionString = _storageDirectory.FullName,
            EnableFileWatching = false
        };
        _storages.Add(storage);

        // As the root manager of the application does.
        var resolver = new SubjectPathResolver(() => storage);
        context.AddService<ISubjectPathResolver>(resolver);
        storage.AttachToContext(context);

        await storage.ConnectAsync(CancellationToken.None);
        return (storage, context, resolver);
    }

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_storageDirectory.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
