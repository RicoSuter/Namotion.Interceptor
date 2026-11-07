using System.Text;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Storage.Blazor.Tests;

/// <summary>A door on a page, for the expressions of the page to read.</summary>
[InterceptorSubject]
public partial class TestDoor : IConfigurable
{
    [Configuration]
    [State]
    public partial bool IsOpen { get; set; }

    [Configuration]
    [State]
    public partial bool IsLocked { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// An in-memory storage that holds the page <c>page.md</c> with a <see cref="TestDoor"/> named <c>door</c> and value maps,
/// whose expressions the tests render.
/// </summary>
public sealed class PageTestGraph
{
    private readonly FluentStorageContainer _storage;
    private readonly IServiceProvider _services;

    private PageTestGraph(FluentStorageContainer storage, IServiceProvider services)
    {
        _storage = storage;
        _services = services;
    }

    public MarkdownFile Page { get; private set; } = null!;

    /// <summary>The services of the subjects of the graph.</summary>
    public IServiceProvider Services => _services;

    /// <summary>The storage that holds the files of the graph.</summary>
    public FluentStorageContainer Storage => _storage;

    public TestDoor Door => Page.Children.Values.OfType<TestDoor>().Single();

    public RegisteredSubjectProperty DoorIsOpen => GetProperty(nameof(TestDoor.IsOpen));

    public RegisteredSubjectProperty DoorIsLocked => GetProperty(nameof(TestDoor.IsLocked));

    /// <summary>Creates the graph with a page whose text after the door and the maps is <paramref name="text"/>.</summary>
    public static async Task<PageTestGraph> CreateAsync(string text)
    {
        var typeProvider = new TypeProvider();
        typeProvider
            .AddAssembly(typeof(FluentStorageContainer).Assembly)
            .AddAssembly(typeof(ValueMap).Assembly)
            .AddAssembly(typeof(PageTestGraph).Assembly);
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

        // A stopped storage loads no page of its own for the file that the graph writes.
        await storage.StopAsync(CancellationToken.None);

        var graph = new PageTestGraph(storage, serviceProvider);
        graph.Page = await graph.AddPageAsync("page.md", $$$"""
            :: {{{typeof(TestDoor).FullName}}}#door IsOpen=true IsLocked=true
            ::

            :: HomeBlaze.Components.ValueMap#state
            ::: Case Value=true Text="open"
            ::: Case Text="unknown"
            ::

            :: HomeBlaze.Components.ValueMap#lock
            ::: Case When="subject/IsLocked" Text="locked"
            ::

            :: HomeBlaze.Components.ValueMap#wronglock
            ::: Case When="subject/Nothing" Text="locked"
            ::

            {{{text}}}
            """);
        return graph;
    }

    /// <summary>Adds a page that holds only <paramref name="text"/>.</summary>
    public async Task<MarkdownFile> AddPageAsync(string name, string text)
    {
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"))))
        {
            await _storage.WriteBlobAsync(name, stream, CancellationToken.None);
        }

        var page = new MarkdownFile(
            _storage,
            name,
            _services.GetRequiredService<MarkdownContentParser>(),
            _services.GetRequiredService<ConfigurableSubjectSerializer>());
        _storage.Children = new Dictionary<string, IInterceptorSubject>(_storage.Children) { [name] = page };
        await page.OnFileChangedAsync(CancellationToken.None);
        return page;
    }

    /// <summary>Adds a JSON file that holds <paramref name="text"/>.</summary>
    public async Task<JsonFile> AddJsonFileAsync(string name, string text)
    {
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
        {
            await _storage.WriteBlobAsync(name, stream, CancellationToken.None);
        }

        var file = new JsonFile(_storage, name);
        _storage.Children = new Dictionary<string, IInterceptorSubject>(_storage.Children) { [name] = file };
        return file;
    }

    /// <summary>Reads the text of a file of the storage.</summary>
    public async Task<string> ReadTextAsync(string name)
    {
        await using var stream = await _storage.ReadBlobAsync(name, CancellationToken.None);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    /// <summary>Gets the expression of a page; there must be exactly one.</summary>
    public static RenderExpression GetExpression(MarkdownFile page) =>
        page.Children.Values.OfType<RenderExpression>().Single();

    private RegisteredSubjectProperty GetProperty(string name) =>
        Door.TryGetRegisteredSubject()!.TryGetProperty(name)!;
}
