using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;

namespace HomeBlaze.Services.Serialization;

/// <summary>
/// JSON type info resolver that:
/// - Sets up polymorphism for IConfigurableSubject with $type discriminator
/// - Filters properties to only [Configuration] for IConfigurableSubject types
/// - Allows all properties for plain value objects
/// </summary>
public class ConfigurationJsonTypeInfoResolver : DefaultJsonTypeInfoResolver
{
    private readonly TypeProvider _typeProvider;

    public ConfigurationJsonTypeInfoResolver(TypeProvider typeProvider)
    {
        _typeProvider = typeProvider;
    }

    public override JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var typeInfo = base.GetTypeInfo(type, options);

        // Set up polymorphism on IConfigurableSubject and IInterceptorSubject base types
        if (type == typeof(IConfigurableSubject) || type == typeof(IInterceptorSubject))
        {
            typeInfo.PolymorphismOptions = new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = "$type",
                IgnoreUnrecognizedTypeDiscriminators = false,
                UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization
            };

            // Register all IConfigurableSubject implementations from TypeProvider
            foreach (var derivedType in _typeProvider.Types
                .Where(t => typeof(IConfigurableSubject).IsAssignableFrom(t)
                            && t is { IsAbstract: false, IsInterface: false }))
            {
                typeInfo.PolymorphismOptions.DerivedTypes.Add(
                    new JsonDerivedType(derivedType, derivedType.FullName!));
            }
        }

        // Filter [Configuration] properties for all object types
        if (typeInfo.Kind == JsonTypeInfoKind.Object)
        {
            foreach (var property in typeInfo.Properties)
            {
                var hasConfigurationAttribute = property.AttributeProvider?
                    .GetCustomAttributes(typeof(ConfigurationAttribute), true)
                    .Any() ?? false;

                if (!hasConfigurationAttribute)
                {
                    property.ShouldSerialize = static (_, _) => false;
                }
            }
        }

        return typeInfo;
    }
}
