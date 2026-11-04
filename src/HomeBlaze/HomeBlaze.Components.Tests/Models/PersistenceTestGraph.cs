using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace HomeBlaze.Components.Tests.Models;

/// <summary>
/// A device held by a recording configuration store, in a context with parent tracking, and a root manager without root.
/// </summary>
public sealed class PersistenceTestGraph : IDisposable
{
    private readonly ServiceProvider _serviceProvider = new ServiceCollection().BuildServiceProvider();

    public PersistenceTestGraph()
    {
        Context = SubjectContextFactory.Create(new ServiceCollection());

        var typeProvider = new TypeProvider();
        RootManager = new RootManager(
            new SubjectTypeRegistry(typeProvider),
            new ConfigurableSubjectSerializer(typeProvider, _serviceProvider),
            Context,
            new SubjectPathResolver(() => null));

        Device = new PersistedDevice(Context);
        Store = new RecordingConfigurationStore(Context) { Device = Device };
    }

    public IInterceptorSubjectContext Context { get; }

    public RootManager RootManager { get; }

    public RecordingConfigurationStore Store { get; }

    public PersistedDevice Device { get; }

    public void Dispose()
    {
        RootManager.Dispose();
        _serviceProvider.Dispose();
    }
}
