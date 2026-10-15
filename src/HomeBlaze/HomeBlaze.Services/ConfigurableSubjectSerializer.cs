using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services.Serialization;
using Microsoft.Extensions.DependencyInjection;
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

    private readonly TypeProvider _typeProvider;
    private readonly IServiceProvider _serviceProvider;
    private readonly JsonSerializerOptions _jsonPropertyOptions;
    private readonly JsonSerializerOptions _cSharpPropertyOptions;

    public ConfigurableSubjectSerializer(TypeProvider typeProvider, IServiceProvider serviceProvider)
    {
        _typeProvider = typeProvider;
        _serviceProvider = serviceProvider;
        _jsonPropertyOptions = CreateOptions(typeProvider, serviceProvider, ConfigurationNaming.JsonProperty);
        _cSharpPropertyOptions = CreateOptions(typeProvider, serviceProvider, ConfigurationNaming.CSharpProperty);
    }

    private static JsonSerializerOptions CreateOptions(
        TypeProvider typeProvider, IServiceProvider serviceProvider, ConfigurationNaming naming) =>
        new()
        {
            TypeInfoResolver = new ConfigurationJsonTypeInfoResolver(typeProvider, serviceProvider, naming),
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
    private static string GetJsonName(string propertyName, ConfigurationNaming naming) =>
        naming == ConfigurationNaming.JsonProperty ? JsonNamingPolicy.CamelCase.ConvertName(propertyName) : propertyName;

    /// <summary>
    /// Serializes an IConfigurable to JSON with $type discriminator.
    /// </summary>
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
    /// <exception cref="InvalidOperationException">The subject is not registered.</exception>
    /// <remarks>
    /// The value of an excluded property is not read. A subject that is nested in another property is written
    /// in full, also when it has a property that would be excluded.
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

        // The properties, their names and their filter are those of the serializer, so that the result
        // equals Serialize apart from what is excluded.
        foreach (var property in options.GetTypeInfo(type).Properties)
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
        var type = _typeProvider.Types.FirstOrDefault(t => t.FullName == typeName);
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
    /// services. A hosted service that was not started yet is stopped without having been started.
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

        // The context the constructor got from the service provider is a fallback context of the subject.
        // Removing it is what detaches a subject that no property holds.
        if (_serviceProvider.GetService<IInterceptorSubjectContext>() is { } context)
        {
            subject.Context.RemoveFallbackContext(context);
        }
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
        var options = GetOptions(naming);

        if (subject is IInterceptorSubject interceptorSubject)
        {
            var registered = interceptorSubject.TryGetRegisteredSubject();
            if (registered != null)
            {
                foreach (var property in registered.Properties)
                {
                    if (property.TryGetAttribute(KnownAttributes.Configuration) == null)
                        continue;

                    var jsonName = GetJsonName(property.Name, naming);
                    if (root.TryGetProperty(jsonName, out var jsonValue))
                    {
                        var valueJson = jsonValue.GetRawText();
                        try
                        {
                            if (TryConvert(valueJson, property.Name, property.Type, options, skipped, out var value))
                            {
                                property.SetValue(value);
                            }
                        }
                        catch (JsonException exception)
                        {
                            // Skip properties that can't be deserialized
                            skipped?.Invoke(new SkippedConfigurationProperty(property.Name, property.Type, valueJson, exception));
                        }
                    }
                }
                return;
            }
        }

        // Fallback to reflection for subjects without registry context
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var hasConfigAttribute = property.GetCustomAttributes(typeof(ConfigurationAttribute), true).Length > 0;
            if (!hasConfigAttribute || !property.CanWrite)
                continue;

            var jsonName = GetJsonName(property.Name, naming);
            if (root.TryGetProperty(jsonName, out var jsonValue))
            {
                var valueJson = jsonValue.GetRawText();
                try
                {
                    if (TryConvert(valueJson, property.Name, property.PropertyType, options, skipped, out var value))
                    {
                        property.SetValue(subject, value);
                    }
                }
                catch (JsonException exception)
                {
                    // Skip properties that can't be deserialized
                    skipped?.Invoke(new SkippedConfigurationProperty(property.Name, property.PropertyType, valueJson, exception));
                }
            }
        }
    }

    /// <summary>
    /// Converts the JSON value of one configuration property. A value that the JSON reader does not support
    /// is handed to <paramref name="skipped"/> and gives false. Without the callback it throws, like every
    /// value that does not convert.
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
        catch (NotSupportedException exception) when (skipped is not null)
        {
            skipped(new SkippedConfigurationProperty(propertyName, propertyType, valueJson, exception));
            value = null;
            return false;
        }
    }

    /// <summary>
    /// Checks whether a JSON value converts to the given property type with the options that
    /// <see cref="Deserialize(string, ConfigurationNaming)"/> and
    /// <see cref="UpdateConfiguration(IInterceptorSubject, string, ConfigurationNaming)"/> use
    /// for <paramref name="naming"/>.
    /// </summary>
    /// <remarks>
    /// Both methods skip a property whose value does not convert, and report it only to a caller that
    /// passes a callback for the skipped properties.
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
        var root = document.RootElement;
        var options = GetOptions(naming);

        foreach (var property in subject.GetConfigurationProperties())
        {
            var jsonName = GetJsonName(property.Name, naming);
            if (root.TryGetProperty(jsonName, out var jsonValue))
            {
                var valueJson = jsonValue.GetRawText();
                try
                {
                    // Use the options of Serialize/Deserialize for consistent deserialization behavior
                    if (TryConvert(valueJson, property.Name, property.Type, options, skipped, out var value))
                    {
                        property.SetValue(value);
                    }
                }
                catch (JsonException exception)
                {
                    // Skip properties that can't be deserialized
                    skipped?.Invoke(new SkippedConfigurationProperty(property.Name, property.Type, valueJson, exception));
                }
            }
        }
    }
}
