using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Services.Serialization;

/// <summary>
/// JSON type info resolver that:
/// - Sets up polymorphism for IConfigurable with $type discriminator
/// - Filters properties to only [Configuration] for IConfigurable types
/// - Allows all properties for plain value objects
/// - Creates IConfigurable types that have no parameterless constructor through the service provider
/// </summary>
public class ConfigurationJsonTypeInfoResolver : DefaultJsonTypeInfoResolver
{
    private readonly TypeProvider _typeProvider;
    private readonly IServiceProvider _serviceProvider;

    public ConfigurationJsonTypeInfoResolver(TypeProvider typeProvider, IServiceProvider serviceProvider)
    {
        _typeProvider = typeProvider;
        _serviceProvider = serviceProvider;
    }

    public override JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var typeInfo = base.GetTypeInfo(type, options);

        // Set up polymorphism on IConfigurable and IInterceptorSubject base types
        if (type == typeof(IConfigurable) || type == typeof(IInterceptorSubject))
        {
            typeInfo.PolymorphismOptions = new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = "$type",
                IgnoreUnrecognizedTypeDiscriminators = false,
                UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization
            };

            // Register all IConfigurable implementations from TypeProvider
            foreach (var derivedType in _typeProvider.Types
                .Where(t => typeof(IConfigurable).IsAssignableFrom(t)
                            && t is { IsAbstract: false, IsInterface: false }))
            {
                typeInfo.PolymorphismOptions.DerivedTypes.Add(
                    new JsonDerivedType(derivedType, derivedType.FullName!));
            }
        }

        // CreateObject is null for a type without a parameterless constructor. The serializer then binds the
        // constructor parameters to JSON properties and throws on a parameter that is a dependency, so a
        // subject nested in the configuration of another one is created the way the root of a configuration is.
        if (typeInfo is { Kind: JsonTypeInfoKind.Object, CreateObject: null } &&
            type is { IsAbstract: false, IsInterface: false } &&
            typeof(IConfigurable).IsAssignableFrom(type))
        {
            typeInfo.CreateObject = () => ActivatorUtilities.CreateInstance(_serviceProvider, type);
        }

        // Filter [Configuration] properties for all object types
        if (typeInfo.Kind == JsonTypeInfoKind.Object)
        {
            foreach (var property in typeInfo.Properties)
            {
                var propertyName = (property.AttributeProvider as MemberInfo)?.Name ?? property.Name;
                var ignoreNulls = options.DefaultIgnoreCondition == JsonIgnoreCondition.WhenWritingNull;
                property.ShouldSerialize = (parent, value) =>
                {
                    // Respect WhenWritingNull since setting ShouldSerialize overrides default behavior
                    if (ignoreNulls && value is null)
                        return false;

                    if (parent is IInterceptorSubject subject)
                    {
                        var registered = subject.TryGetRegisteredSubject();
                        if (registered != null)
                        {
                            var registeredProperty = registered.TryGetProperty(propertyName);
                            return registeredProperty?.TryGetAttribute(KnownAttributes.Configuration) != null;
                        }
                    }

                    // For non-subject types (value objects) or subjects without registry, fall back to reflection
                    var hasConfigurationAttribute = property.AttributeProvider?
                        .GetCustomAttributes(typeof(ConfigurationAttribute), true)
                        .Any() ?? false;

                    return hasConfigurationAttribute;
                };
            }
        }

        return typeInfo;
    }
}
