using System.Text.Json;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services;
using HomeBlaze.Abstractions;
using HomeBlaze.Services.Metadata;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Abstractions.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// Builds the HomeBlaze MCP tool configuration shared by the MCP server and the built-in agents.
/// </summary>
internal static class HomeBlazeMcpConfigurationFactory
{
    private const string PathPrefix = "/";

    /// <summary>The refusal of a call of SetMetadata whose arguments do not have the shape of the method.</summary>
    internal const string SetMetadataArgumentsMessage =
        "SetMetadata takes path, a string, an optional property and values, a JSON object, for example {\"path\": \"Devices/Pump\", \"values\": {\"navTitle\": \"Pump\"}}.";

    /// <summary>
    /// Creates a configuration with the HomeBlaze path provider, enrichers, type providers, method tools, Markdown tools, the Markdown
    /// search tool, the Markdown page hints of browse and search, the Percent hint of search and every registered
    /// <see cref="McpToolProviderRegistration"/>. The method tools and the Markdown tools find only the subjects and methods
    /// <paramref name="accessPolicy"/> allows to read and invoke only the methods it allows to execute. invoke_operation and
    /// the Markdown tools refuse SetMetadata of a storage with arguments of another shape, for a subject or property that
    /// the tools do not show, which is refused as a missing one, and for metadata that the policy does not allow to write.
    /// SearchMetadata of a storage and search_markdown leave out the metadata and notes of the subjects and properties that
    /// the tools do not show and those that the policy does not allow to read. GetOrder of a folder names only the children
    /// that the tools show, SetOrder needs write access to the metadata of the folder, and an order that names a child the
    /// tools do not show is refused as one naming a child that does not exist.
    /// </summary>
    /// <param name="serviceProvider">Services; must contain <see cref="SubjectTypeRegistry"/> and <see cref="ILoggerFactory"/>.</param>
    /// <param name="rootSubjectProvider">Resolves the root subject when a tool runs.</param>
    /// <param name="isReadOnly">Whether set_property and operations are blocked.</param>
    /// <param name="accessPolicy">Decides per member and action what the tools show and change, <see cref="McpServerConfiguration.AccessPolicy"/>.</param>
    public static McpServerConfiguration Create(
        IServiceProvider serviceProvider,
        Func<IInterceptorSubject> rootSubjectProvider,
        bool isReadOnly,
        IMcpAccessPolicy accessPolicy)
    {
        ArgumentNullException.ThrowIfNull(accessPolicy);

        var typeRegistry = serviceProvider.GetRequiredService<SubjectTypeRegistry>();
        var pathProvider = new StateAttributePathProvider();
        var typeProviders = new IMcpTypeProvider[]
        {
            new SubjectAbstractionTypeProvider(),
            new SubjectTypeRegistryTypeProvider(typeRegistry)
        };

        var excludeTypes = typeRegistry.RegisteredTypes
            .Where(type => ExcludeFromAttribute.IsExcluded(type, ExclusionTarget.Agents))
            .ToArray();

        var configuration = new McpServerConfiguration
        {
            PathProvider = pathProvider,
            PathPrefix = PathPrefix,
            ExcludeTypes = excludeTypes,
            SubjectEnrichers = { new HomeBlazeMcpSubjectEnricher(typeProviders, isReadOnly, accessPolicy) },
            TypeProviders = typeProviders,
            ToolDescriptionAdditions =
            {
                ["browse"] = HomeBlazeMcpInstructions.MarkdownSubjectsToolHint,
                ["search"] = HomeBlazeMcpInstructions.MarkdownSubjectsToolHint + " " + HomeBlazeMcpInstructions.PercentFractionsSearchHint,
                ["set_property"] = HomeBlazeMcpInstructions.ChangeCaution + " " + HomeBlazeMcpInstructions.ConfigurationWriteHint
            },
            AccessPolicy = accessPolicy,
            IsReadOnly = isReadOnly
        };

        var invoker = new McpMethodInvoker(
            rootSubjectProvider,
            configuration,
            serviceProvider,
            serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<McpMethodInvoker>(),
            isReadOnly,
            (subject, method, arguments) => AgentMetadataAccess.GetOperationError(subject, method, arguments, configuration, pathProvider, rootSubjectProvider()),
            (subject, method, result) => AgentOrderAccess.FilterGetOrderResult(
                subject, method, AgentMetadataAccess.FilterSearchMetadataResult(subject, method, result, configuration, pathProvider),
                configuration, pathProvider, rootSubjectProvider()));
        configuration.ToolProviders.Add(new HomeBlazeMcpToolProvider(invoker, isReadOnly));
        configuration.ToolProviders.Add(new HomeBlazeMarkdownToolProvider(invoker, isReadOnly));
        configuration.ToolProviders.Add(new HomeBlazeMarkdownSearchToolProvider(rootSubjectProvider, pathProvider, PathPrefix, excludeTypes, configuration));

        var context = new McpToolProviderContext(serviceProvider, rootSubjectProvider, pathProvider, isReadOnly, configuration);
        foreach (var registration in serviceProvider.GetServices<McpToolProviderRegistration>())
        {
            configuration.ToolProviders.Add(registration.Create(context));
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
