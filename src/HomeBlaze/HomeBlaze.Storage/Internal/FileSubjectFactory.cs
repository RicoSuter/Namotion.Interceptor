using System.Text.Json;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Creates the subjects of the files of a storage.
/// </summary>
internal sealed class FileSubjectFactory
{
    // What the serializer writes into a configurable subject's JSON: the full name of its type.
    private const string TypeDiscriminator = "$type";

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
        var mappedType = _typeRegistry.ResolveTypeForExtension(Path.GetExtension(path));
        return mappedType != null
            ? (IInterceptorSubject)ActivatorUtilities.CreateInstance(_serviceProvider, mappedType, storage, path)
            : new GenericFile(storage, path);
    }

    /// <summary>
    /// Serializes a subject to JSON.
    /// </summary>
    public string Serialize(IInterceptorSubject subject)
        => _serializer.Serialize(subject);

    /// <summary>
    /// Creates the subject of a JSON file from its text: the configurable subject it describes,
    /// or a <see cref="JsonFile"/> when the text is plain or invalid JSON.
    /// </summary>
    public IInterceptorSubject CreateFromJson(IStorageContainer storage, string path, string json)
    {
        try
        {
            // Every configurable type is a subject as well.
            if (_serializer.Deserialize(json) is IInterceptorSubject subject)
            {
                return subject;
            }
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "Failed to deserialize JSON subject from: {Path}", path);
        }

        return new JsonFile(storage, path);
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
               document.RootElement.TryGetProperty(TypeDiscriminator, out var typeName) &&
               typeName.ValueKind == JsonValueKind.String &&
               typeName.ValueEquals(subject.GetType().FullName);
    }
}
