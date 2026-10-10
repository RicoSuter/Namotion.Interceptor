using System.Text;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace HomeBlaze.Storage.Tests.Pages;

public sealed class MarkdownPageFixture
{
    /// <summary>
    /// The xunit collection of every test class that writes to the in-memory blob storage.
    /// The storage hashes each write with one shared hash instance that is not thread-safe,
    /// so these classes must not run in parallel.
    /// </summary>
    public const string StorageCollection = "In-memory blob storage";

    private MarkdownPageFixture(FluentStorageContainer storage, IServiceProvider services)
    {
        Storage = storage;
        Services = services;
    }

    public FluentStorageContainer Storage { get; }

    public IServiceProvider Services { get; }

    /// <summary>The hosted subjects of this fixture that run. Filled once <see cref="StartHostedServicesAsync"/> ran.</summary>
    public TestHostedLog HostedLog { get; } = new();

    /// <param name="configureServices">Adds or replaces services before the service provider is built.</param>
    public static async Task<MarkdownPageFixture> CreateAsync(Action<IServiceCollection>? configureServices = null)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(FluentStorageContainer).Assembly);
        typeProvider.AddAssembly(typeof(MarkdownPageFixture).Assembly);
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        var services = new ServiceCollection();
        var context = SubjectContextFactory.Create(services);
        FluentStorageContainer? storage = null;

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton(context);
        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        services.AddSingleton(_ => new SubjectPathResolver(() => storage));
        services.AddSingleton<RootManager>();
        services.AddSingleton<MarkdownContentParser>();
        configureServices?.Invoke(services);
        var serviceProvider = services.BuildServiceProvider();

        // RootManager registers itself and the path resolver in the context from its constructor.
        serviceProvider.GetRequiredService<RootManager>();

        storage = new FluentStorageContainer(
            typeRegistry, serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(), serviceProvider)
        {
            StorageType = "inmemory"
        };
        ((IInterceptorSubject)storage).Context.AddFallbackContext(context);
        await storage.ConnectAsync(CancellationToken.None);

        return new MarkdownPageFixture(storage, serviceProvider);
    }

    /// <summary>
    /// Starts the hosted services of the context, so that the hosted subjects on a page are started and stopped.
    /// Without it they never start. Call <see cref="StopHostedServicesAsync"/> at the end of the test.
    /// </summary>
    public async Task StartHostedServicesAsync()
    {
        Services.GetRequiredService<IInterceptorSubjectContext>().AddService(HostedLog);
        foreach (var hostedService in Services.GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }

        // The storage is a hosted subject as well, and its start connects again, which replaces the in-memory
        // blobs with an empty store. That must be over before a test writes its first page.
        await SettleHostedServicesAsync();
        if (Storage.ExecuteTask is { } connect)
        {
            await connect;
        }
    }

    public async Task StopHostedServicesAsync()
    {
        foreach (var hostedService in Services.GetServices<IHostedService>())
        {
            await hostedService.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Completes when every start and stop that was requested before this call has run.
    /// Starts and stops run one after the other in the order they were requested, so a service
    /// that is attached now starts after all of them.
    /// </summary>
    public async Task SettleHostedServicesAsync()
    {
        var marker = new MarkerHostedService();
        await Storage.AttachHostedServiceAsync(marker, CancellationToken.None);
        await Storage.DetachHostedServiceAsync(marker, CancellationToken.None);
    }

    public async Task<MarkdownFile> AddPageAsync(string name, string content)
    {
        await WriteAsync(name, content);
        var file = CreatePage(name);

        Storage.Children = new Dictionary<string, IInterceptorSubject>(Storage.Children) { [name] = file };
        await file.OnFileChangedAsync(CancellationToken.None);
        return file;
    }

    /// <summary>
    /// Adds a page the way the storage adds a new file: the page is parsed first and put into the graph afterwards.
    /// </summary>
    public async Task<MarkdownFile> AddPageParsedBeforeAttachAsync(string name, string content)
    {
        await WriteAsync(name, content);
        var file = CreatePage(name);

        await file.OnFileChangedAsync(CancellationToken.None);
        Storage.Children = new Dictionary<string, IInterceptorSubject>(Storage.Children) { [name] = file };
        return file;
    }

    private MarkdownFile CreatePage(string name) =>
        new(
            Storage,
            name,
            Services.GetRequiredService<MarkdownContentParser>(),
            Services.GetRequiredService<ConfigurableSubjectSerializer>());

    public async Task UpdatePageAsync(MarkdownFile file, string content, CancellationToken cancellationToken = default)
    {
        await WriteAsync(file.Name, content);
        await file.OnFileChangedAsync(cancellationToken);
    }

    public async Task<string> ReadAsync(string name)
    {
        await using var stream = await Storage.ReadBlobAsync(name, CancellationToken.None);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    public static string Html(IEnumerable<KeyValuePair<string, IInterceptorSubject>> children) =>
        string.Concat(children.Select(child => child.Value is HtmlSegment html ? html.Html : string.Empty));

    /// <summary>Writes the file without telling the page, like a change that the file watcher has not reported yet.</summary>
    public async Task WriteAsync(string name, string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content.Replace("\r\n", "\n")));
        await Storage.WriteBlobAsync(name, stream, CancellationToken.None);
    }

    private sealed class MarkerHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
