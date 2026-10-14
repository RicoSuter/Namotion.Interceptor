using HomeBlaze.Abstractions;
using HomeBlaze.Components;
using HomeBlaze.Samples;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests.Pages;

/// <summary>
/// A folder of the documentation with its subfolders, loaded from a copy of its pages and JSON files by a storage
/// as the application loads it, with the widget types that ship and the sample subjects. A path from a page to a
/// file next to it, such as <c>../Pump</c>, resolves as it does in the application.
/// </summary>
/// <remarks>
/// The storage follows nothing after it has loaded the files, and the hosted subjects, such as the sample motors,
/// are not started.
/// </remarks>
public sealed class DocumentationFolderFixture : IDisposable
{
    /// <summary>The documentation root, relative to the repository root.</summary>
    public const string DocumentationFolder = "src/HomeBlaze/HomeBlaze/Data/Files/Docs";

    private readonly ServiceProvider _services;
    private readonly FluentStorageContainer _storage;
    private readonly DirectoryInfo _directory;

    private DocumentationFolderFixture(ServiceProvider services, FluentStorageContainer storage, DirectoryInfo directory)
    {
        _services = services;
        _storage = storage;
        _directory = directory;
        Pages = FindPages(storage).OrderBy(page => page.FullPath, StringComparer.Ordinal).ToList();
    }

    public IServiceProvider Services => _services;

    /// <summary>The pages of the folder and of its subfolders, ordered by their path in the storage.</summary>
    public IReadOnlyList<MarkdownFile> Pages { get; }

    /// <param name="relativeFolder">The folder relative to <see cref="DocumentationFolder"/>, for example <c>administration</c>.</param>
    public static async Task<DocumentationFolderFixture> LoadAsync(string relativeFolder)
    {
        var directory = Directory.CreateTempSubdirectory("homeblaze-docs-");
        CopyPagesAndJsonFiles(relativeFolder, directory);

        var typeProvider = new TypeProvider();
        typeProvider
            .AddAssembly(typeof(FluentStorageContainer).Assembly)
            .AddAssembly(typeof(DocumentationFolderFixture).Assembly)
            .AddAssembly(typeof(Card).Assembly)
            .AddAssembly(typeof(Motor).Assembly);
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
            ConnectionString = directory.FullName,
            EnableFileWatching = false,
            ReconcileIntervalSeconds = 0
        };
        ((IInterceptorSubject)storage).Context.AddFallbackContext(context);

        // Completes when the first pass has created the subject of every file.
        await storage.ConnectAsync(CancellationToken.None);
        return new DocumentationFolderFixture(serviceProvider, storage, directory);
    }

    /// <summary>Gets the subject at an absolute path of the storage, for example <c>/administration/widgets/Pump</c>.</summary>
    public IInterceptorSubject FindSubject(string absolutePath) =>
        _services.GetRequiredService<SubjectPathResolver>().ResolveSubject(absolutePath, PathStyle.Canonical)
        ?? throw new InvalidOperationException($"Nothing exists at '{absolutePath}'.");

    /// <summary>Gets the page whose path in the storage is <paramref name="fullPath"/>, for example <c>/administration/page-widgets.md</c>.</summary>
    public MarkdownFile FindPage(string fullPath) => Pages.Single(page => page.FullPath == fullPath);

    /// <summary>Gets the subjects of the page at every depth in the order of the page.</summary>
    public static List<IInterceptorSubject> FindSubjects(MarkdownFile page) =>
        PageErrorBoxes.Flatten(page.Children.Values).ToList();

    public void Dispose()
    {
        _storage.Dispose();
        _services.Dispose();
        _directory.Delete(recursive: true);
    }

    private static void CopyPagesAndJsonFiles(string relativeFolder, DirectoryInfo directory)
    {
        var source = Path.Combine(RepositoryPagesTests.FindRepositoryRoot(), DocumentationFolder, relativeFolder);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (Path.GetExtension(file) is not (".md" or ".json"))
            {
                continue;
            }

            var target = Path.Combine(directory.FullName, relativeFolder, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static IEnumerable<MarkdownFile> FindPages(IInterceptorSubject subject)
    {
        IEnumerable<IInterceptorSubject> children = subject switch
        {
            FluentStorageContainer storage => storage.Children.Values,
            VirtualFolder folder => folder.Children.Values,
            _ => []
        };

        foreach (var child in children)
        {
            if (child is MarkdownFile page)
            {
                yield return page;
            }

            foreach (var nested in FindPages(child))
            {
                yield return nested;
            }
        }
    }
}
