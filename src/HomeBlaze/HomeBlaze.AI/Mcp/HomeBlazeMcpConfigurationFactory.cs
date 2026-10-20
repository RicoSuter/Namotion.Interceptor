using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.AI.Agents;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// Builds the HomeBlaze MCP tool configuration shared by the MCP server and the built-in agents.
/// </summary>
internal static class HomeBlazeMcpConfigurationFactory
{
    private const string PathPrefix = "/";

    private static readonly Func<RegisteredSubject, MethodMetadata, object?[], string?> RefuseAgentOperation = GetAgentOperationError;

    private static readonly Func<RegisteredSubjectProperty, string?> RefuseConfigurationWrite =
        property => property.IsConfigurationProperty() ? "It is a configuration property; set_property cannot change configuration, an operation of the subject may." : null;

    /// <summary>
    /// Creates a configuration with the HomeBlaze path provider, enrichers, type providers, method tools, Markdown tools, the Markdown
    /// search tool, the Markdown page hints of browse and search, the Percent hint of search and every registered
    /// <see cref="McpToolProviderRegistration"/>. set_property refuses every property marked with <see cref="ConfigurationAttribute"/>,
    /// regardless of <paramref name="forAgents"/>.
    /// </summary>
    /// <param name="serviceProvider">Services; must contain <see cref="SubjectTypeRegistry"/> and <see cref="ILoggerFactory"/>.</param>
    /// <param name="rootSubjectProvider">Resolves the root subject when a tool runs.</param>
    /// <param name="isReadOnly">Whether set_property and operations are blocked.</param>
    /// <param name="forAgents">
    /// Whether the tools serve the built-in agents; invoke_operation and the Markdown tools then refuse every
    /// operation of an agent (<see cref="LlmAgentBase"/>), SetMetadata of a storage for an agent or a subject inside one,
    /// and SetOrder of a folder inside an agent.
    /// </param>
    public static McpServerConfiguration Create(
        IServiceProvider serviceProvider,
        Func<IInterceptorSubject> rootSubjectProvider,
        bool isReadOnly,
        bool forAgents)
    {
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
            forAgents ? RefuseAgentOperation : null);
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
                new HomeBlazeMarkdownSearchToolProvider(rootSubjectProvider, pathProvider, PathPrefix)
            },
            ToolDescriptionAdditions =
            {
                ["browse"] = HomeBlazeMcpInstructions.MarkdownSubjectsToolHint,
                ["search"] = HomeBlazeMcpInstructions.MarkdownSubjectsToolHint + " " + HomeBlazeMcpInstructions.PercentFractionsSearchHint,
                ["set_property"] = HomeBlazeMcpInstructions.ChangeCaution
            },
            PropertyWriteFilter = RefuseConfigurationWrite,
            IsReadOnly = isReadOnly
        };

        var context = new McpToolProviderContext(serviceProvider, rootSubjectProvider, pathProvider, isReadOnly);
        foreach (var registration in serviceProvider.GetServices<McpToolProviderRegistration>())
        {
            configuration.ToolProviders.Add(registration.Create(context));
        }

        return configuration;
    }

    private static string? GetAgentOperationError(RegisteredSubject subject, MethodMetadata method, object?[] arguments)
    {
        if (subject.Subject is LlmAgentBase)
        {
            return "Agents cannot run operations of agents.";
        }

        // SetMetadata of a storage whose path leads to an agent or to a subject inside one, and SetOrder of a folder inside one.
        var target = method.PropertyName switch
        {
            "SetMetadata" when subject.Subject is FluentStorageContainer storage && arguments is [string path, ..]
                => MetadataPaths.TryResolveSubject(storage, path),
            "SetOrder" => subject.Subject,
            _ => null
        };

        return target?.TryGetFirstParentOrSelf<LlmAgentBase>() is not null
            ? "Agents cannot change the metadata of agents."
            : null;
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
