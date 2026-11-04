using System.Text.Json;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Creates the subjects of the files of a storage.
/// </summary>
internal sealed class FileSubjectFactory
{
    private readonly SubjectTypeRegistry _typeRegistry;
    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger? _logger;

    public FileSubjectFactory(
        SubjectTypeRegistry typeRegistry,
        ConfigurableSubjectSerializer serializer,
        IServiceProvider serviceProvider,
        ILogger? logger = null)
    {
        _typeRegistry = typeRegistry;
        _serializer = serializer;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Creates the subject of a file from the type that is registered for its extension, or a
    /// <see cref="GenericFile"/> when none is. The subject is not loaded. A JSON file is created from its text
    /// with <see cref="CreateFromJson"/> instead.
    /// </summary>
    /// <remarks>
    /// The constructor of a registered type takes the storage and the path, and after them services.
    /// </remarks>
    public IInterceptorSubject CreateFile(IStorageContainer storage, string path)
    {
        // Without the flow of the caller, as for a subject from JSON: the constructor is code of the file type.
        using var suppressedFlow = ExecutionContext.SuppressFlow();

        var mappedType = _typeRegistry.ResolveTypeForExtension(Path.GetExtension(path));
        return mappedType != null
            ? (IInterceptorSubject)ActivatorUtilities.CreateInstance(_serviceProvider, mappedType, storage, path)
            : new GenericFile(storage, path);
    }

    /// <summary>
    /// Creates the subject of a JSON file from its text: the configurable subject it describes,
    /// or a <see cref="JsonFile"/> when the text is plain or invalid JSON.
    /// </summary>
    public IInterceptorSubject CreateFromJson(IStorageContainer storage, string path, string json)
    {
        // A configurable subject enters the context when it is created, which runs lifecycle handlers as an
        // assignment to the tree does. With the flow of the caller, work that a handler starts would count as
        // part of the work item that creates the subject, and its calls into the storage would be refused.
        using var suppressedFlow = ExecutionContext.SuppressFlow();

        try
        {
            // Every configurable type is a subject as well.
            if (_serializer.Deserialize(json) is IInterceptorSubject subject)
            {
                return subject;
            }
        }
        catch (JsonException exception)
        {
            // Expected, for example for a file that is still being written.
            _logger?.LogDebug(exception, "Not valid JSON, read as a plain file: {Path}", path);
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "Failed to deserialize JSON subject from: {Path}", path);
        }

        return new JsonFile(storage, path);
    }

    /// <summary>
    /// Takes a subject that entered the context when it was created and that no property holds out of that
    /// context, which detaches it as leaving the tree would have: a hosted service is stopped. The same for
    /// the subjects that are embedded in a page which is not in the tree. Does nothing for any other subject.
    /// </summary>
    public void Release(IInterceptorSubject? subject)
    {
        if (subject is null)
        {
            return;
        }

        // Without the flow of the caller, as for a subject from JSON: detaching runs lifecycle handlers too.
        using var suppressedFlow = ExecutionContext.SuppressFlow();

        // A page enters the context only with the tree, but the subjects embedded in it when they are created.
        // A page in the tree holds them in a property, so they are left alone here.
        if (subject is MarkdownFile page)
        {
            foreach (var embedded in page.Children.Values)
            {
                ReleaseUnreferenced(embedded);
            }
        }

        ReleaseUnreferenced(subject);
    }

    private void ReleaseUnreferenced(IInterceptorSubject subject)
    {
        // A subject that a property holds leaves the context with the assignment that removes it from there.
        // Both answers are acted on after the lock of the lifecycle is released, which TryRunWhileAttached
        // warns against. Its callback cannot do it either, because that must not change the graph. It is
        // safe only because nothing but the worker of the storage has a subject that is not placed.
        if (subject.GetReferenceCount() == 0 &&
            _serviceProvider.GetService<IInterceptorSubjectContext>() is { } context &&
            context.TryGetLifecycleInterceptor() is { } lifecycle &&
            lifecycle.TryRunWhileAttached(subject, static () => { }))
        {
            subject.Context.RemoveFallbackContext(context);
        }
    }

    /// <summary>
    /// Checks whether the JSON text describes a subject of the type of the given one, which can then take the
    /// text as its configuration instead of being replaced.
    /// </summary>
    /// <exception cref="JsonException">The text is not valid JSON.</exception>
    public static bool DescribesTypeOf(IInterceptorSubject subject, string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object &&
               document.RootElement.TryGetProperty(ConfigurableSubjectSerializer.TypeDiscriminatorPropertyName, out var typeName) &&
               typeName.ValueKind == JsonValueKind.String &&
               typeName.ValueEquals(subject.GetType().FullName);
    }
}
