using System.Text.Json;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.AI.Agents;
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

    /// <summary>The refusal of an operation of an agent.</summary>
    internal const string AgentOperationMessage = "Operations of agents cannot be run.";

    /// <summary>
    /// Creates a configuration with the HomeBlaze path provider, enrichers, type providers, method tools, Markdown tools, the Markdown
    /// search tool, the Markdown page hints of browse and search, the Percent hint of search and every registered
    /// <see cref="McpToolProviderRegistration"/>. invoke_operation and the Markdown tools refuse every operation of an agent
    /// (<see cref="LlmAgentBase"/>), and SetMetadata of a storage with arguments of another shape, for an agent, a subject
    /// that an agent holds through any parent, and a subject or property that the tools do not show, which is refused as a
    /// missing one. SearchMetadata of a storage and search_markdown leave out the metadata and notes of agents and of the
    /// subjects and properties that the tools do not show.
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

        var invoker = new McpMethodInvoker(
            rootSubjectProvider,
            pathProvider,
            serviceProvider,
            serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<McpMethodInvoker>(),
            isReadOnly,
            (subject, method, arguments) => GetOperationError(subject, method, arguments, pathProvider, excludeTypes),
            (subject, method, result) => AgentMetadataAccess.FilterSearchMetadataResult(subject, method, result, pathProvider, excludeTypes));
        var configuration = new McpServerConfiguration
        {
            PathProvider = pathProvider,
            PathPrefix = PathPrefix,
            ExcludeTypes = excludeTypes,
            SubjectEnrichers = { new HomeBlazeMcpSubjectEnricher(typeProviders, isReadOnly) },
            TypeProviders = typeProviders,
            ToolProviders =
            {
                new HomeBlazeMcpToolProvider(invoker, isReadOnly),
                new HomeBlazeMarkdownToolProvider(invoker, isReadOnly),
                new HomeBlazeMarkdownSearchToolProvider(rootSubjectProvider, pathProvider, PathPrefix, excludeTypes)
            },
            ToolDescriptionAdditions =
            {
                ["browse"] = HomeBlazeMcpInstructions.MarkdownSubjectsToolHint,
                ["search"] = HomeBlazeMcpInstructions.MarkdownSubjectsToolHint + " " + HomeBlazeMcpInstructions.PercentFractionsSearchHint,
                ["set_property"] = HomeBlazeMcpInstructions.ChangeCaution + " " + HomeBlazeMcpInstructions.ConfigurationWriteHint
            },
            AccessPolicy = accessPolicy,
            IsReadOnly = isReadOnly
        };

        var context = new McpToolProviderContext(serviceProvider, rootSubjectProvider, pathProvider, isReadOnly, configuration);
        foreach (var registration in serviceProvider.GetServices<McpToolProviderRegistration>())
        {
            configuration.ToolProviders.Add(registration.Create(context));
        }

        return configuration;
    }

    private static string? GetOperationError(
        RegisteredSubject subject, MethodMetadata method, object?[] arguments, PathProviderBase pathProvider, Type[] excludeTypes)
    {
        // Stands in for the system rule on agents until the method tools ask the access policy.
        if (subject.Subject is LlmAgentBase)
        {
            return AgentOperationMessage;
        }

        if (method.PropertyName != FluentStorageContainer.SetMetadataMethodName || subject.Subject is not IMetadataStore)
        {
            return null;
        }

        // The input parameters in declaration order, empty when they could not be read. Any other shape is refused
        // without looking at the path, so that a subject the tools do not show reads as a missing one.
        return arguments is [string path, string or null, JsonElement { ValueKind: JsonValueKind.Object }, ..]
            ? AgentMetadataAccess.GetError(subject, path, arguments[1] as string, pathProvider, excludeTypes)
            : SetMetadataArgumentsMessage;
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
