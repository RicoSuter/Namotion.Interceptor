using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services.Secrets;
using HomeBlaze.Services.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services;

/// <summary>
/// Serializes and deserializes IConfigurable instances to/from JSON.
/// Uses "$type" discriminator for polymorphic serialization via System.Text.Json.
/// Only serializes properties marked with [Configuration].
/// Uses ActivatorUtilities for DI-aware construction during deserialization.
/// </summary>
/// <remarks>
/// Every method that reads or writes JSON takes a <see cref="ConfigurationNaming"/>. It defaults to
/// <see cref="ConfigurationNaming.JsonProperty"/>, the naming of configuration files.
/// </remarks>
public class ConfigurableSubjectSerializer
{
    /// <summary>
    /// Name of the JSON property that holds the full type name of a serialized subject.
    /// </summary>
    public const string TypeDiscriminatorPropertyName = "$type";

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };
    private static readonly JsonDocumentOptions NoDuplicatePropertiesOptions = new() { AllowDuplicateProperties = false };

    private readonly TypeProvider _typeProvider;
    private readonly IServiceProvider _serviceProvider;
    private readonly JsonSerializerOptions _jsonPropertyOptions;
    private readonly JsonSerializerOptions _cSharpPropertyOptions;
    // Null without a SecretStore service, which leaves secret values as they are.
    private readonly SecretReferences? _secrets;

    // Null without a SecretStore service, which leaves configuration text as it is.
    private readonly SecretConverter? _secretConverter;

    // Never cleared: holds its Type keys, and with them their assemblies, as long as the serializer lives, like TypeProvider.
    private readonly ConcurrentDictionary<(Type Type, ConfigurationNaming Naming), JsonElement?> _defaultConfigurations = new();

    public ConfigurableSubjectSerializer(TypeProvider typeProvider, IServiceProvider serviceProvider)
    {
        _typeProvider = typeProvider;
        _serviceProvider = serviceProvider;

        // One instance for both namings, so a key read with one naming is kept when written with the other.
        var store = serviceProvider.GetService<SecretStore>();
        _secrets = store is not null
            ? new SecretReferences(store, serviceProvider.GetService<ILogger<ConfigurableSubjectSerializer>>())
            : null;
        _secretConverter = store is not null ? new SecretConverter(store) : null;
        _jsonPropertyOptions = CreateOptions(typeProvider, serviceProvider, ConfigurationNaming.JsonProperty, _secrets);
        _cSharpPropertyOptions = CreateOptions(typeProvider, serviceProvider, ConfigurationNaming.CSharpProperty, _secrets);
    }

    private static JsonSerializerOptions CreateOptions(
        TypeProvider typeProvider, IServiceProvider serviceProvider, ConfigurationNaming naming, SecretReferences? secrets) =>
        new()
        {
            TypeInfoResolver = new ConfigurationJsonTypeInfoResolver(typeProvider, serviceProvider, naming) { Secrets = secrets },
            PropertyNamingPolicy = naming == ConfigurationNaming.JsonProperty ? JsonNamingPolicy.CamelCase : null,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

    private JsonSerializerOptions GetOptions(ConfigurationNaming naming) =>
        naming == ConfigurationNaming.JsonProperty ? _jsonPropertyOptions : _cSharpPropertyOptions;

    private static IConfigurable GetConfigurable(IInterceptorSubject subject)
    {
        return subject as IConfigurable ?? throw new ArgumentException(
            $"Subject must implement IConfigurable. Type: {subject.GetType().FullName}",
            nameof(subject));
    }

    // The configuration properties of the subject itself are matched by this name and not by the options.
    internal static string GetJsonName(string propertyName, ConfigurationNaming naming) =>
        naming == ConfigurationNaming.JsonProperty ? JsonNamingPolicy.CamelCase.ConvertName(propertyName) : propertyName;

    /// <summary>
    /// Serializes an IConfigurable to JSON with $type discriminator.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The type of the subject, or of a value in its configuration, has a secret configuration property that is not a string property of a subject.
    /// </exception>
    /// <exception cref="IOException">The secret store cannot be written.</exception>
    /// <remarks>
    /// A secret value is written as a reference to the <see cref="SecretStore"/>, and a changed one is stored when it is
    /// serialized, before the file that references it is written. A write of the result that is refused or fails leaves
    /// the changed secret in effect, under the key the file references already.
    /// </remarks>
    public string Serialize(IInterceptorSubject subject, ConfigurationNaming naming = ConfigurationNaming.JsonProperty)
    {
        return JsonSerializer.Serialize(GetConfigurable(subject), typeof(IConfigurable), GetOptions(naming));
    }

    /// <summary>
    /// Serializes like <see cref="Serialize(IInterceptorSubject, ConfigurationNaming)"/>, without the configuration
    /// properties of the subject itself for which <paramref name="isExcluded"/> returns true.
    /// </summary>
    /// <param name="subject">The subject to serialize. It must be registered.</param>
    /// <param name="isExcluded">Receives a registered property of the subject and returns true to leave it out.</param>
    /// <param name="naming">The naming of the JSON.</param>
    /// <exception cref="InvalidOperationException">
    /// The subject is not registered, or its type, or that of a value in its configuration, has a secret configuration property that is not a string property of a subject.
    /// </exception>
    /// <exception cref="IOException">The secret store cannot be written.</exception>
    /// <remarks>
    /// The value of an excluded property is not read. A subject that is nested in another property is written
    /// in full, also when it has a property that would be excluded. Secret values are stored as by
    /// <see cref="Serialize(IInterceptorSubject, ConfigurationNaming)"/>.
    /// </remarks>
    public string Serialize(
        IInterceptorSubject subject,
        Func<RegisteredSubjectProperty, bool> isExcluded,
        ConfigurationNaming naming = ConfigurationNaming.JsonProperty)
    {
        _ = GetConfigurable(subject);

        var options = GetOptions(naming);
        var type = subject.GetType();
        var registeredSubject = subject.TryGetRegisteredSubject() ?? throw new InvalidOperationException(
            $"Subject must be registered to exclude its properties. Type: {type.FullName}");
        var result = new JsonObject { [TypeDiscriminatorPropertyName] = type.FullName };

        // The properties, their names, their filter and the callback of the type are those of the serializer, so that
        // the result equals Serialize apart from what is excluded.
        var typeInfo = options.GetTypeInfo(type);
        typeInfo.OnSerializing?.Invoke(subject);
        foreach (var property in typeInfo.Properties)
        {
            // The resolver creates every property from a member of the type and adds none of its own. A property
            // without a registered counterpart is skipped, as the filter of the serializer skips it.
            var declaredName = ((MemberInfo)property.AttributeProvider!).Name;
            if (property.Get is not { } getValue ||
                registeredSubject.TryGetProperty(declaredName) is not { } registeredProperty ||
                isExcluded(registeredProperty))
            {
                continue;
            }

            var value = getValue(subject);
            if (property.ShouldSerialize is { } shouldSerialize && !shouldSerialize(subject, value))
            {
                continue;
            }

            result[property.Name] = JsonSerializer.SerializeToNode(value, property.PropertyType, options);
        }

        return result.ToJsonString(options);
    }

    /// <summary>
    /// Gets the configuration of a new subject of <paramref name="type"/> as
    /// <see cref="Serialize(IInterceptorSubject, ConfigurationNaming)"/> writes it: the values that
    /// <see cref="Deserialize(string, ConfigurationNaming)"/> gives a property that the JSON does not have.
    /// Null when <see cref="Deserialize(string, ConfigurationNaming)"/> does not create the type, or when creating or
    /// serializing the subject throws, which is logged.
    /// </summary>
    /// <remarks>
    /// Creates a subject of the type and releases it before a hosted service of it starts. Calls that race on the
    /// first request for a type can each create one. The result is cached, except null for a type that is not known
    /// yet, because a plugin can add it later.
    /// </remarks>
    public JsonElement? GetDefaultConfiguration(Type type, ConfigurationNaming naming = ConfigurationNaming.JsonProperty)
    {
        var key = (type, naming);
        if (_defaultConfigurations.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var defaults = CreateDefaultConfiguration(type, naming, out var isKnownType);
        return isKnownType ? _defaultConfigurations.GetOrAdd(key, defaults) : null;
    }

    private JsonElement? CreateDefaultConfiguration(Type type, ConfigurationNaming naming, out bool isKnownType)
    {
        isKnownType = _typeProvider.Types.Contains(type);
        if (!isKnownType)
        {
            return null;
        }

        // Work that a lifecycle handler starts for the subject does not carry the flow of the caller, and
        // the subject is released before the deferral ends, so its hosted services never start.
        using var suppressedFlow = ExecutionContext.IsFlowSuppressed() ? (AsyncFlowControl?)null : ExecutionContext.SuppressFlow();
        using var startup = _serviceProvider.GetService<IInterceptorSubjectContext>()?.DeferHostedServiceStarts();

        IInterceptorSubject? subject = null;
        try
        {
            var json = new JsonObject { [TypeDiscriminatorPropertyName] = type.FullName }.ToJsonString();
            Deserialize(json, created => subject = created, naming);

            // Another type of the same name would give the wrong values.
            if (subject is null || subject.GetType() != type)
            {
                return null;
            }

            using var document = JsonDocument.Parse(Serialize(subject, naming));
            return document.RootElement.Clone();
        }
        catch (Exception exception)
        {
            // Cached as null, so it is logged once per type.
            _serviceProvider.GetService<ILogger<ConfigurableSubjectSerializer>>()?.LogWarning(
                exception, "The default configuration of {Type} could not be read, so all its values are written and ReplaceConfiguration keeps the values of missing keys.", type.FullName);
            return null;
        }
        finally
        {
            if (subject is not null)
            {
                Release(subject);
            }
        }
    }

    /// <summary>
    /// Deserializes JSON to an IConfigurable using $type discriminator.
    /// Uses ActivatorUtilities for DI-aware construction.
    /// </summary>
    public IConfigurable? Deserialize(string json, ConfigurationNaming naming = ConfigurationNaming.JsonProperty) =>
        Deserialize(json, created: null, naming);

    /// <summary>
    /// Deserializes like <see cref="Deserialize(string, ConfigurationNaming)"/> and hands a new subject to
    /// <paramref name="created"/> before its configuration properties are set.
    /// </summary>
    /// <remarks>
    /// The subject is attached to the context from its constructor on. When setting a configuration property
    /// throws, this method throws and the callback is the only way to get the subject, which
    /// <see cref="Release"/> then detaches. Call <see cref="Release"/> only after this method has returned or thrown.
    /// </remarks>
    public IConfigurable? Deserialize(
        string json, Action<IInterceptorSubject>? created, ConfigurationNaming naming = ConfigurationNaming.JsonProperty) =>
        Deserialize(json, created, skipped: null, naming);

    /// <summary>
    /// Deserializes like <see cref="Deserialize(string, Action{IInterceptorSubject}, ConfigurationNaming)"/> and
    /// hands every configuration property whose value does not convert to <paramref name="skipped"/>.
    /// </summary>
    /// <remarks>
    /// Such a property is skipped and keeps its default, with and without the callback. A subject that was
    /// created inside its value before the conversion failed is in no property. If its constructor attached
    /// it to the context, <see cref="Release"/> of the returned subject does not detach it.
    /// A value that the JSON reader does not support, such as a subject without a type discriminator, makes
    /// this method throw a <see cref="NotSupportedException"/> without the callback. With the callback the
    /// property is skipped and handed to it like the others.
    /// </remarks>
    public IConfigurable? Deserialize(
        string json,
        Action<IInterceptorSubject>? created,
        Action<SkippedConfigurationProperty>? skipped,
        ConfigurationNaming naming = ConfigurationNaming.JsonProperty)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // Extract $type discriminator. Only an object can have one.
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(TypeDiscriminatorPropertyName, out var typeElement))
        {
            return null;
        }

        var typeName = typeElement.GetString();
        if (string.IsNullOrEmpty(typeName))
        {
            return null;
        }

        // Find the type in registered types
        var type = FindType(typeName);
        if (type == null)
        {
            return null;
        }

        using var startup = _serviceProvider.GetService<IInterceptorSubjectContext>()?.DeferHostedServiceStarts();
        // Create instance using ActivatorUtilities for DI-aware construction
        var subject = ActivatorUtilities.CreateInstance(_serviceProvider, type) as IConfigurable;
        if (subject == null)
        {
            return null;
        }

        if (created is not null && subject is IInterceptorSubject interceptorSubject)
        {
            created(interceptorSubject);
        }

        PopulateConfigurationProperties(subject, type, root, skipped, naming);
        return subject;
    }

    /// <summary>
    /// Releases a subject that <see cref="Deserialize(string, ConfigurationNaming)"/> created and that is not used
    /// after all: detaches it and the subjects in its properties from the context, which stops their hosted
    /// services, and lets go of their secret keys, see <see cref="ReleaseSecrets"/>. A hosted service that was not
    /// started yet is stopped without having been started.
    /// </summary>
    /// <remarks>
    /// Does nothing for a subject that a property references. Such a subject is in the graph and leaves the
    /// context when the reference is removed.
    /// </remarks>
    public void Release(IInterceptorSubject subject)
    {
        if (subject.GetReferenceCount() > 0)
        {
            return;
        }

        ReleaseSecrets(subject);

        // The context the constructor got from the service provider is a fallback context of the subject.
        // Removing it is what detaches a subject that no property holds.
        if (_serviceProvider.GetService<IInterceptorSubjectContext>() is { } context)
        {
            subject.Context.RemoveFallbackContext(context);
        }
    }

    /// <summary>
    /// Lets go of the secret keys that the subject and the subjects in its configuration hold, for a subject that is no
    /// longer used, so that another subject that holds one of them keeps it when it changes its value. A subject in its
    /// configuration that something else references keeps its keys.
    /// </summary>
    /// <remarks>Call it before the subject is detached from the context.</remarks>
    public void ReleaseSecrets(IInterceptorSubject subject)
    {
        if (_secrets is null)
        {
            return;
        }

        _secrets.Release(subject);
        foreach (var property in subject.GetConfigurationProperties())
        {
            foreach (var child in property.Children)
            {
                // Before the subject is detached, so its own property is the one reference of a child released with it.
                if (child.Subject.GetReferenceCount() <= 1)
                {
                    ReleaseSecrets(child.Subject);
                }
            }
        }
    }

    /// <summary>
    /// Replaces each plaintext secret in configuration JSON, whose root names its type with <c>$type</c>, by a reference
    /// to a new key of the <see cref="SecretStore"/>, and stores the values with one write. Returns false, with
    /// <paramref name="converted"/> being <paramref name="json"/>, when there is nothing to replace, when the text is not
    /// a JSON object of a known type, or without a <see cref="SecretStore"/> service.
    /// </summary>
    /// <exception cref="IOException">The secret store cannot be written. Nothing is stored.</exception>
    /// <exception cref="UnauthorizedAccessException">The secret store cannot be written. Nothing is stored.</exception>
    /// <exception cref="InvalidOperationException">
    /// A type in the configuration has a secret configuration property that is not a string property of a subject.
    /// </exception>
    public bool TryConvertSecrets(string json, ConfigurationNaming naming, out string converted)
    {
        converted = json;
        if (_secretConverter is null || ParseObject(json) is not { } root ||
            root[TypeDiscriminatorPropertyName] is not JsonValue discriminator ||
            !discriminator.TryGetValue<string>(out var typeName) ||
            FindType(typeName) is not { } type)
        {
            return false;
        }

        return TryConvert(root, type, naming, ref converted);
    }

    /// <summary>
    /// Replaces each plaintext secret in configuration JSON of <paramref name="type"/>, such as a block configuration whose
    /// type is given elsewhere, like <see cref="TryConvertSecrets(string, ConfigurationNaming, out string)"/>.
    /// </summary>
    /// <exception cref="IOException">The secret store cannot be written. Nothing is stored.</exception>
    /// <exception cref="UnauthorizedAccessException">The secret store cannot be written. Nothing is stored.</exception>
    /// <exception cref="InvalidOperationException">
    /// A type in the configuration has a secret configuration property that is not a string property of a subject.
    /// </exception>
    public bool TryConvertSecrets(string json, Type type, ConfigurationNaming naming, out string converted)
    {
        converted = json;
        return _secretConverter is not null && ParseObject(json) is { } root && TryConvert(root, type, naming, ref converted);
    }

    private bool TryConvert(JsonObject root, Type type, ConfigurationNaming naming, ref string converted)
    {
        if (!_secretConverter!.TryConvert(root, type, GetOptions(naming), naming))
        {
            return false;
        }

        converted = root.ToJsonString(IndentedOptions);
        return true;
    }

    private static JsonObject? ParseObject(string json)
    {
        try
        {
            // Rejected here: a JsonObject would throw an ArgumentException for a duplicate property on first access.
            return JsonNode.Parse(json, documentOptions: NoDuplicatePropertiesOptions) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Type? FindType(string fullName)
    {
        foreach (var type in _typeProvider.Types)
        {
            if (type.FullName == fullName)
            {
                return type;
            }
        }

        return null;
    }

    /// <summary>
    /// Populates [Configuration] properties on a newly created subject.
    /// Uses registry attribute lookup when available, falls back to reflection.
    /// </summary>
    private void PopulateConfigurationProperties(
        IConfigurable subject,
        Type type,
        JsonElement root,
        Action<SkippedConfigurationProperty>? skipped,
        ConfigurationNaming naming)
    {
        if (subject is IInterceptorSubject interceptorSubject && interceptorSubject.TryGetRegisteredSubject() != null)
        {
            SetConfigurationProperties(interceptorSubject, root, defaults: null, skipped, naming);
            return;
        }

        // Fallback to reflection for subjects without registry context
        var options = GetOptions(naming);
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var hasConfigAttribute = property.GetCustomAttributes(typeof(ConfigurationAttribute), true).Length > 0;
            if (!hasConfigAttribute || !property.CanWrite)
                continue;

            var isSecret = SecretReferences.IsSecret(property);
            if (isSecret)
            {
                SecretReferences.EnsureSupported(type, property.Name, property.PropertyType);
            }

            var jsonName = GetJsonName(property.Name, naming);
            if (root.TryGetProperty(jsonName, out var jsonValue))
            {
                var valueJson = jsonValue.GetRawText();
                if (TryConvert(valueJson, property.Name, property.PropertyType, options, skipped, out var value))
                {
                    if (isSecret && _secrets is not null)
                    {
                        // A subject: EnsureSupported threw for any other type.
                        _secrets.Read(
                            (IInterceptorSubject)subject, property.Name, value, property,
                            static (owner, propertyInfo, resolved) => propertyInfo.SetValue(owner, resolved));
                    }
                    else
                    {
                        property.SetValue(subject, value);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Sets the configuration properties of a registered subject from <paramref name="root"/>, and a property that
    /// <paramref name="root"/> does not have from <paramref name="defaults"/> when they are given.
    /// </summary>
    private void SetConfigurationProperties(
        IInterceptorSubject subject,
        JsonElement root,
        JsonElement? defaults,
        Action<SkippedConfigurationProperty>? skipped,
        ConfigurationNaming naming)
    {
        var options = GetOptions(naming);
        foreach (var property in subject.GetConfigurationProperties())
        {
            // Checked before the value is looked up, so a type with an unsupported secret fails for every file.
            var isSecret = SecretReferences.IsSecret(property);
            if (isSecret)
            {
                SecretReferences.EnsureSupported(subject.GetType(), property.Name, property.Type);
            }

            var jsonName = GetJsonName(property.Name, naming);
            string valueJson;
            if (root.TryGetProperty(jsonName, out var jsonValue))
            {
                valueJson = jsonValue.GetRawText();
            }
            else if (defaults is { } defaultValues)
            {
                // Defaults are written without nulls, so a key they do not have has a null default.
                valueJson = defaultValues.TryGetProperty(jsonName, out var defaultValue) ? defaultValue.GetRawText() : "null";
            }
            else
            {
                continue;
            }

            if (TryConvert(valueJson, property.Name, property.Type, options, skipped, out var value))
            {
                if (isSecret && _secrets is not null)
                {
                    _secrets.Read(
                        subject, property.Name, value, property,
                        static (_, registeredProperty, resolved) => registeredProperty.SetValue(resolved));
                }
                else
                {
                    property.SetValue(value);
                }
            }
        }
    }

    /// <summary>
    /// Converts the JSON value of one configuration property. A value that does not convert is handed to
    /// <paramref name="skipped"/> and gives false. A value that the JSON reader does not support does the same
    /// with the callback and throws without it.
    /// </summary>
    private static bool TryConvert(
        string valueJson,
        string propertyName,
        Type propertyType,
        JsonSerializerOptions options,
        Action<SkippedConfigurationProperty>? skipped,
        out object? value)
    {
        try
        {
            value = JsonSerializer.Deserialize(valueJson, propertyType, options);
            return true;
        }
        catch (Exception exception) when (exception is JsonException || (exception is NotSupportedException && skipped is not null))
        {
            skipped?.Invoke(new SkippedConfigurationProperty(propertyName, propertyType, valueJson, exception));
            value = null;
            return false;
        }
    }

    /// <summary>
    /// Describes the properties that a method of this class handed to its callback for skipped properties, each by
    /// its key in JSON with the given naming.
    /// </summary>
    public static string DescribeSkippedProperties(
        IEnumerable<SkippedConfigurationProperty> skipped,
        ConfigurationNaming naming = ConfigurationNaming.JsonProperty) =>
        string.Join("; ", skipped.Select(property =>
            $"the value of '{GetJsonName(property.PropertyName, naming)}' could not be read: {property.Exception.Message}"));

    /// <summary>
    /// Checks whether a JSON value converts to the given property type with the options that
    /// <see cref="Deserialize(string, ConfigurationNaming)"/> and
    /// <see cref="UpdateConfiguration(IInterceptorSubject, string, ConfigurationNaming)"/> use
    /// for <paramref name="naming"/>.
    /// </summary>
    /// <remarks>
    /// Use it to check a value before one of the methods that skip a value that does not convert.
    /// </remarks>
    public bool CanDeserialize(
        string json, Type propertyType, ConfigurationNaming naming = ConfigurationNaming.JsonProperty)
    {
        try
        {
            JsonSerializer.Deserialize(json, propertyType, GetOptions(naming));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Updates a subject's configuration properties from JSON.
    /// Uses same options as Serialize/Deserialize for consistent property handling.
    /// Note: STJ doesn't support populating existing objects, so we deserialize each property individually.
    /// </summary>
    public void UpdateConfiguration(
        IInterceptorSubject subject, string json, ConfigurationNaming naming = ConfigurationNaming.JsonProperty) =>
        UpdateConfiguration(subject, json, skipped: null, naming);

    /// <summary>
    /// Updates like <see cref="UpdateConfiguration(IInterceptorSubject, string, ConfigurationNaming)"/> and hands
    /// every configuration property whose value does not convert to <paramref name="skipped"/>.
    /// </summary>
    /// <remarks>
    /// Such a property is skipped and keeps its value, with and without the callback. The properties
    /// before and after it are updated. A value that the JSON reader does not support, such as a subject
    /// without a type discriminator, makes this method throw a <see cref="NotSupportedException"/> without
    /// the callback. With the callback the property is skipped and handed to it like the others.
    /// </remarks>
    public void UpdateConfiguration(
        IInterceptorSubject subject,
        string json,
        Action<SkippedConfigurationProperty>? skipped,
        ConfigurationNaming naming = ConfigurationNaming.JsonProperty)
    {
        using var document = JsonDocument.Parse(json);
        SetConfigurationProperties(subject, document.RootElement, defaults: null, skipped, naming);
    }

    /// <summary>
    /// Updates like <see cref="UpdateConfiguration(IInterceptorSubject, string, Action{SkippedConfigurationProperty}, ConfigurationNaming)"/>
    /// and gives every configuration property that the JSON does not have the value of a new subject of its type,
    /// see <see cref="GetDefaultConfiguration"/>, so that the subject holds what a subject created from the JSON holds,
    /// apart from a value that does not convert, which keeps its value.
    /// </summary>
    /// <remarks>
    /// For a type without a default configuration, a property that the JSON does not have keeps its value.
    /// </remarks>
    public void ReplaceConfiguration(
        IInterceptorSubject subject,
        string json,
        Action<SkippedConfigurationProperty>? skipped,
        ConfigurationNaming naming = ConfigurationNaming.JsonProperty)
    {
        using var document = JsonDocument.Parse(json);
        var defaults = GetDefaultConfiguration(subject.GetType(), naming);
        SetConfigurationProperties(subject, document.RootElement, defaults, skipped, naming);
    }
}
