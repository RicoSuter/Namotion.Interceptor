using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.AI.Agents;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// Builds the HomeBlaze MCP tool configuration shared by the MCP server and the built-in agents.
/// </summary>
internal static class HomeBlazeMcpConfigurationFactory
{
    /// <summary>
    /// Creates a configuration with the HomeBlaze path provider, enrichers, type providers, method tools, Markdown tools, the
    /// Markdown page hints of browse and search and every registered <see cref="McpToolProviderRegistration"/>.
    /// </summary>
    /// <param name="serviceProvider">Services; must contain <see cref="SubjectTypeRegistry"/> and <see cref="ILoggerFactory"/>.</param>
    /// <param name="rootSubjectProvider">Resolves the root subject when a tool runs.</param>
    /// <param name="isReadOnly">Whether set_property and operations are blocked.</param>
    /// <param name="additionalToolProviders">Providers passed directly by the caller, added after the registered ones.</param>
    public static McpServerConfiguration Create(
        IServiceProvider serviceProvider,
        Func<IInterceptorSubject> rootSubjectProvider,
        bool isReadOnly,
        IEnumerable<Func<McpToolProviderContext, IMcpToolProvider>>? additionalToolProviders = null)
    {
        return Create(serviceProvider, rootSubjectProvider, isReadOnly, additionalToolProviders, propertyWriteFilter: null, operationFilter: null);
    }

    /// <summary>
    /// Creates the configuration of the built-in agents' tools: the same as <see cref="Create(IServiceProvider, Func{IInterceptorSubject}, bool, IEnumerable{Func{McpToolProviderContext, IMcpToolProvider}}?)"/>,
    /// except that set_property refuses every property marked with <see cref="ConfigurationAttribute"/> and
    /// invoke_operation and the Markdown tools refuse every operation of an agent (<see cref="LlmAgentBase"/>).
    /// </summary>
    /// <param name="serviceProvider">Services; must contain <see cref="SubjectTypeRegistry"/> and <see cref="ILoggerFactory"/>.</param>
    /// <param name="rootSubjectProvider">Resolves the root subject when a tool runs.</param>
    /// <param name="isReadOnly">Whether set_property and operations are blocked.</param>
    public static McpServerConfiguration CreateForAgents(
        IServiceProvider serviceProvider,
        Func<IInterceptorSubject> rootSubjectProvider,
        bool isReadOnly)
    {
        return Create(serviceProvider, rootSubjectProvider, isReadOnly, additionalToolProviders: null, RefuseConfigurationWrite, RefuseAgentOperation);
    }

    private static string? RefuseAgentOperation(RegisteredSubject subject, MethodMetadata method) =>
        subject.Subject is LlmAgentBase ? "Agents cannot run operations of agents." : null;

    private static string? RefuseConfigurationWrite(RegisteredSubjectProperty property, string path) =>
        property.TryGetAttribute(KnownAttributes.Configuration) is not null
            ? $"'{path}' is a configuration property; agents cannot change configuration."
            : null;

    private static McpServerConfiguration Create(
        IServiceProvider serviceProvider,
        Func<IInterceptorSubject> rootSubjectProvider,
        bool isReadOnly,
        IEnumerable<Func<McpToolProviderContext, IMcpToolProvider>>? additionalToolProviders,
        Func<RegisteredSubjectProperty, string, string?>? propertyWriteFilter,
        Func<RegisteredSubject, MethodMetadata, string?>? operationFilter)
    {
        var typeRegistry = serviceProvider.GetRequiredService<SubjectTypeRegistry>();
        var pathProvider = new StateAttributePathProvider();
        var typeProviders = new IMcpTypeProvider[]
        {
            new SubjectAbstractionTypeProvider(),
            new SubjectTypeRegistryTypeProvider(typeRegistry)
        };

        var excludeTypes = typeRegistry.RegisteredTypes
            .Where(type => type.GetCustomAttributes(typeof(ExcludeFromBrowsingAttribute), true).Length > 0)
            .ToArray();

        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var configuration = new McpServerConfiguration
        {
            PathProvider = pathProvider,
            PathPrefix = "/",
            ExcludeTypes = excludeTypes,
            SubjectEnrichers = { new HomeBlazeMcpSubjectEnricher(typeProviders, isReadOnly) },
            TypeProviders = typeProviders,
            ToolProviders =
            {
                new HomeBlazeMcpToolProvider(
                    rootSubjectProvider,
                    pathProvider, serviceProvider,
                    loggerFactory.CreateLogger<HomeBlazeMcpToolProvider>(),
                    isReadOnly,
                    operationFilter),
                new HomeBlazeMarkdownToolProvider(
                    rootSubjectProvider,
                    pathProvider, serviceProvider,
                    loggerFactory.CreateLogger<HomeBlazeMarkdownToolProvider>(),
                    isReadOnly,
                    operationFilter)
            },
            ToolDescriptionAdditions =
            {
                ["browse"] = HomeBlazeMcpInstructions.MarkdownSubjectsToolHint,
                ["search"] = HomeBlazeMcpInstructions.MarkdownSubjectsToolHint,
                ["set_property"] = HomeBlazeMcpInstructions.ChangeCaution
            },
            PropertyWriteFilter = propertyWriteFilter,
            IsReadOnly = isReadOnly
        };

        var context = new McpToolProviderContext(serviceProvider, rootSubjectProvider, pathProvider, isReadOnly);
        var creators = serviceProvider.GetServices<McpToolProviderRegistration>()
            .Select(registration => registration.Create)
            .Concat(additionalToolProviders ?? []);
        foreach (var create in creators)
        {
            configuration.ToolProviders.Add(create(context));
        }

        return configuration;
    }

    /// <summary>
    /// Resolves the root subject. The root is loaded by a background service, so a request arriving before it
    /// exists gets a diagnosable error rather than a null reference, and a failed load surfaces its own exception.
    /// </summary>
    public static IInterceptorSubject GetLoadedRoot(IServiceProvider serviceProvider)
    {
        var rootLoaded = serviceProvider.GetRequiredService<RootManager>().RootLoaded;
        if (!rootLoaded.IsCompleted)
        {
            throw new InvalidOperationException("The root subject is still loading, try again shortly.");
        }

        return rootLoaded.GetAwaiter().GetResult();
    }
}
