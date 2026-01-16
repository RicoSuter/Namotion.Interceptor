using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using HotChocolate.Execution.Configuration;
using HotChocolate.Types;
using Namotion.Interceptor;
using Namotion.Interceptor.GraphQL;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class SubjectGraphQLExtensions
{
    /// <summary>
    /// Adds GraphQL support for the specified subject type using default configuration.
    /// </summary>
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(this IRequestExecutorBuilder builder)
        where TSubject : class, IInterceptorSubject
    {
        return builder.AddSubjectGraphQL<TSubject>(
            sp => sp.GetRequiredService<TSubject>(),
            _ => new GraphQLSubjectConfiguration());
    }

    /// <summary>
    /// Adds GraphQL support for the specified subject type with a custom root name.
    /// </summary>
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(
        this IRequestExecutorBuilder builder,
        string rootName)
        where TSubject : class, IInterceptorSubject
    {
        return builder.AddSubjectGraphQL<TSubject>(
            sp => sp.GetRequiredService<TSubject>(),
            _ => new GraphQLSubjectConfiguration { RootName = rootName });
    }

    /// <summary>
    /// Adds GraphQL support with custom subject selector using default configuration.
    /// </summary>
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(
        this IRequestExecutorBuilder builder,
        Func<IServiceProvider, TSubject> subjectSelector)
        where TSubject : class, IInterceptorSubject
    {
        return builder.AddSubjectGraphQL(
            subjectSelector,
            _ => new GraphQLSubjectConfiguration());
    }

    /// <summary>
    /// Adds GraphQL support with custom subject selector and root name.
    /// </summary>
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(
        this IRequestExecutorBuilder builder,
        Func<IServiceProvider, TSubject> subjectSelector,
        string rootName)
        where TSubject : class, IInterceptorSubject
    {
        return builder.AddSubjectGraphQL(
            subjectSelector,
            _ => new GraphQLSubjectConfiguration { RootName = rootName });
    }

    /// <summary>
    /// Adds GraphQL support with custom subject selector and full configuration.
    /// </summary>
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(
        this IRequestExecutorBuilder builder,
        Func<IServiceProvider, TSubject> subjectSelector,
        Func<IServiceProvider, GraphQLSubjectConfiguration> configurationProvider)
        where TSubject : class, IInterceptorSubject
    {
        // Evaluate config synchronously to get the root name for schema definition
        var tempConfiguration = configurationProvider(null!);
        var rootName = tempConfiguration.RootName;

        var key = Guid.NewGuid().ToString();

        builder.Services
            .AddKeyedSingleton(key, (sp, _) => configurationProvider(sp))
            .AddKeyedSingleton(key, (sp, _) => subjectSelector(sp));

        builder
            .AddQueryType(d => d
                .Name("Query")
                .Field(rootName)
                .Resolve(ctx => ctx.Services.GetRequiredKeyedService<TSubject>(key)))
            .AddSubscriptionType(d => d
                .Name("Subscription")
                .Field(rootName)
                .Type<ObjectType<TSubject>>()
                .Subscribe(ctx =>
                {
                    var subject = ctx.Services.GetRequiredKeyedService<TSubject>(key);
                    var configuration = ctx.Services.GetRequiredKeyedService<GraphQLSubjectConfiguration>(key);

                    // Extract selected field names from the subscription
                    var selectedPaths = ExtractSelectionPaths(ctx);

                    return CreateFilteredStream(subject, configuration, selectedPaths, ctx.RequestAborted);
                })
                .Resolve(ctx => ctx.GetEventMessage<TSubject>()));

        return builder;
    }

    private static IReadOnlySet<string> ExtractSelectionPaths(HotChocolate.Resolvers.IResolverContext context)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var selections = context.Selection.SelectionSet?.Selections;
            if (selections != null)
            {
                ExtractPathsRecursive(selections, "", paths);
            }
        }
        catch
        {
            // If we can't extract selections, return empty set (notify for all changes)
        }

        return paths;
    }

    private static void ExtractPathsRecursive(
        IReadOnlyList<HotChocolate.Language.ISelectionNode> selections,
        string prefix,
        HashSet<string> paths)
    {
        foreach (var selection in selections)
        {
            if (selection is HotChocolate.Language.FieldNode field)
            {
                var fieldName = field.Name.Value;
                var path = string.IsNullOrEmpty(prefix) ? fieldName : $"{prefix}.{fieldName}";
                paths.Add(path);

                // Recurse into nested selections
                if (field.SelectionSet?.Selections != null)
                {
                    ExtractPathsRecursive(field.SelectionSet.Selections, path, paths);
                }
            }
        }
    }

    private static async IAsyncEnumerable<TSubject> CreateFilteredStream<TSubject>(
        TSubject subject,
        GraphQLSubjectConfiguration configuration,
        IReadOnlySet<string> selectedPaths,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TSubject : class, IInterceptorSubject
    {
        var observable = subject.Context.GetPropertyChangeObservable();

        // Filter changes by selection and buffer them
        var buffered = observable
            .Where(change =>
                selectedPaths.Count == 0 ||
                GraphQLSelectionMatcher.IsPropertyInSelection(
                    change, selectedPaths, configuration.PathProvider, subject))
            .Buffer(configuration.BufferTime)
            .Where(batch => batch.Count > 0);

        await foreach (var batch in buffered.ToAsyncEnumerable().WithCancellation(cancellationToken))
        {
            yield return subject;
        }
    }
}
