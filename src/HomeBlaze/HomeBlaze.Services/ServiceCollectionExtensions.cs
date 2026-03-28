using HomeBlaze.Services.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Extensions;
using Namotion.Interceptor.Mcp.Implementations;

namespace HomeBlaze.Services;

/// <summary>
/// Extension methods for registering HomeBlaze.Services in dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds HomeBlaze backend services to the service collection.
    /// This includes root management, serialization, type registry, and context factory.
    /// </summary>
    public static IServiceCollection AddHomeBlazeServices(this IServiceCollection services)
    {
        var typeProvider = new TypeProvider();
        var typeRegistry = new SubjectTypeRegistry(typeProvider);
        var context = SubjectContextFactory.Create(services);

        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton(context);

        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        services.AddSingleton<RootManager>();
        services.AddSingleton<SubjectPathResolver>();
        services.AddSingleton<DeveloperModeService>();
        services.AddHostedService(sp => sp.GetRequiredService<RootManager>());

        return services;
    }

    /// <summary>
    /// Adds the MCP subject server configured with HomeBlaze extensions.
    /// The root subject is resolved lazily from <see cref="RootManager"/> at tool invocation time,
    /// since it is loaded asynchronously during startup.
    /// Must be called after <see cref="AddHomeBlazeServices"/> so that <see cref="RootManager"/>
    /// and <see cref="SubjectTypeRegistry"/> singletons are already registered.
    /// </summary>
    public static IServiceCollection AddHomeBlazeMcpServer(this IServiceCollection services)
    {
        var pathProvider = new StateAttributePathProvider();

        // RootManager loads the root subject asynchronously during startup.
        // We capture a lazy resolver: the Func<IInterceptorSubject> is only
        // invoked at MCP tool call time (after the app has started), so
        // RootManager.Root will be available by then.
        RootManager? resolvedRootManager = null;
        SubjectTypeRegistry? resolvedTypeRegistry = null;

        IInterceptorSubject ResolveRoot()
        {
            return resolvedRootManager?.Root
                ?? throw new InvalidOperationException(
                    "Root subject is not yet available. Ensure InitializeHomeBlazeMcpServer " +
                    "has been called after building the application.");
        }

        var configuration = new McpServerConfiguration
        {
            PathProvider = pathProvider,
            SubjectEnrichers = { new HomeBlazeMcpSubjectEnricher() },
            TypeProviders =
            {
                new SubjectAbstractionsAssemblyTypeProvider(),
                new LazySubjectTypeRegistryTypeProvider(() => resolvedTypeRegistry
                    ?? throw new InvalidOperationException(
                        "SubjectTypeRegistry is not yet available. Ensure InitializeHomeBlazeMcpServer " +
                        "has been called after building the application."))
            },
            ToolProviders =
            {
                new HomeBlazeMcpToolProvider(ResolveRoot, pathProvider, isReadOnly: false)
            },
            IsReadOnly = false
        };

        services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithSubjectServerTools(ResolveRoot, configuration);

        // Store the initialization action as a singleton so InitializeHomeBlazeMcpServer can run it.
        services.AddSingleton(new McpInitializationAction(serviceProvider =>
        {
            resolvedRootManager = serviceProvider.GetRequiredService<RootManager>();
            resolvedTypeRegistry = serviceProvider.GetRequiredService<SubjectTypeRegistry>();
        }));

        return services;
    }

    /// <summary>
    /// Initializes the HomeBlaze MCP server by wiring up the lazy root subject resolver
    /// and type registry. Must be called after the application is built.
    /// </summary>
    public static void InitializeHomeBlazeMcpServer(this IServiceProvider serviceProvider)
    {
        var initAction = serviceProvider.GetRequiredService<McpInitializationAction>();
        initAction.Initialize(serviceProvider);
    }
}

/// <summary>
/// Holds the initialization action for the HomeBlaze MCP server, allowing deferred
/// wiring of services that are only available after the DI container is built.
/// </summary>
internal sealed class McpInitializationAction
{
    private readonly Action<IServiceProvider> _initialize;

    public McpInitializationAction(Action<IServiceProvider> initialize)
    {
        _initialize = initialize;
    }

    public void Initialize(IServiceProvider serviceProvider) => _initialize(serviceProvider);
}

/// <summary>
/// A type provider that lazily resolves the <see cref="SubjectTypeRegistry"/>
/// to support deferred DI resolution.
/// </summary>
internal sealed class LazySubjectTypeRegistryTypeProvider : IMcpTypeProvider
{
    private readonly Func<SubjectTypeRegistry> _typeRegistryProvider;

    public LazySubjectTypeRegistryTypeProvider(Func<SubjectTypeRegistry> typeRegistryProvider)
    {
        _typeRegistryProvider = typeRegistryProvider;
    }

    public IEnumerable<McpTypeInfo> GetTypes()
    {
        foreach (var type in _typeRegistryProvider().RegisteredTypes)
        {
            yield return new McpTypeInfo(type.FullName!, null, IsInterface: false);
        }
    }
}
