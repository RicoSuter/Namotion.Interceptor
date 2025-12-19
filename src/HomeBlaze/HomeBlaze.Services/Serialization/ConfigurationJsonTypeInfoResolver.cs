using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;

namespace HomeBlaze.Services.Serialization;

/// <summary>
/// JSON type info resolver that filters properties based on serialization rules:
/// - IInterceptorSubject types: only [Configuration] properties
/// - Plain objects (value objects): all properties
/// </summary>
public class ConfigurationJsonTypeInfoResolver : DefaultJsonTypeInfoResolver
{
    public override JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var typeInfo = base.GetTypeInfo(type, options);

        if (typeof(IInterceptorSubject).IsAssignableFrom(type) &&
            typeInfo.Kind == JsonTypeInfoKind.Object)
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
