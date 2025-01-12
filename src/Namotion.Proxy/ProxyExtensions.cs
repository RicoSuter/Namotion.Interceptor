using Namotion.Proxy.Abstractions;
using Namotion.Proxy.Attributes;
using Namotion.Proxy.Lifecycle;
using Namotion.Proxy.Registry.Abstractions;

using System.Collections;
using System.Text.Json.Nodes;
using System.Text.Json;
using Namotion.Interceptor;

namespace Namotion.Proxy;

public static class ProxyExtensions
{
    /// <summary>
    /// Will attach the subject and its children to the context and 
    /// detach only the subject itself from the previous context.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="context">The context.</param>
    public static void SetContext(this IInterceptorSubject subject, IProxyContext? context)
    {
        var currentContext = subject.Interceptor as IProxyContext;
        if (currentContext != context)
        {
            if (currentContext is not null)
            {
                var registryContext = new ProxyLifecycleContext(default, null, subject, 0, currentContext);
                foreach (var handler in currentContext.GetHandlers<IProxyLifecycleHandler>())
                {
                    handler.OnProxyDetached(registryContext);
                }
            }

            subject.Interceptor = context;

            if (context is not null)
            {
                var registryContext = new ProxyLifecycleContext(default, null, subject, 1, context);
                foreach (var handler in context.GetHandlers<IProxyLifecycleHandler>())
                {
                    handler.OnProxyAttached(registryContext);
                }
            }
        }
    }

    public static void SetData(this IInterceptorSubject subject, string key, object? value)
    {
        subject.Data[key] = value;
    }

    public static bool TryGetData(this IInterceptorSubject subject, string key, out object? value)
    {
        return subject.Data.TryGetValue(key, out value);
    }

    public static string GetJsonPath(this PropertyReference property)
    {
        var context = property.Subject.Interceptor as IProxyContext;
        var registry = context?.GetHandler<IProxyRegistry>();
        if (registry is not null)
        {
            // TODO: avoid endless recursion
            string? path = null;
            var parent = new ProxyParent(property, null);
            do
            {
                var attribute = registry
                    .KnownProxies[parent.Property.Subject]
                    .Properties[parent.Property.Name]
                    .Attributes
                    .OfType<PropertyAttributeAttribute>()
                    .FirstOrDefault();

                if (attribute is not null)
                {
                    return GetJsonPath(new PropertyReference(
                        parent.Property.Subject,
                        attribute.PropertyName)) +
                        "@" + attribute.AttributeName;
                }

                path = JsonNamingPolicy.CamelCase.ConvertName(parent.Property.Name) +
                    (parent.Index is not null ? $"[{parent.Index}]" : string.Empty) +
                    (path is not null ? $".{path}" : string.Empty);

                parent = parent.Property.Subject.GetParents().FirstOrDefault();
            }
            while (parent.Property.Subject is not null);

            return path.Trim('.');
        }
        else
        {
            // TODO: avoid endless recursion
            string? path = null;
            var parent = new ProxyParent(property, null);
            do
            {
                var attribute = parent.Property.Subject
                    .Properties[parent.Property.Name]
                    .Attributes
                    .OfType<PropertyAttributeAttribute>()
                    .FirstOrDefault();

                if (attribute is not null)
                {
                    return GetJsonPath(new PropertyReference(
                        parent.Property.Subject,
                        attribute.PropertyName)) +
                        "@" + attribute.AttributeName;
                }

                path = JsonNamingPolicy.CamelCase.ConvertName(parent.Property.Name) +
                    (parent.Index is not null ? $"[{parent.Index}]" : string.Empty) +
                    (path is not null ? $".{path}" : string.Empty);

                parent = parent.Property.Subject.GetParents().FirstOrDefault();
            }
            while (parent.Property.Subject is not null);

            return path.Trim('.');
        }
    }

    public static JsonObject ToJsonObject(this IInterceptorSubject subject)
    {
        var obj = new JsonObject();
        foreach (var property in subject
            .Properties
            .Where(p => p.Value.GetValue is not null))
        {
            var propertyName = GetJsonPropertyName(subject, property.Value);
            var value = property.Value.GetValue?.Invoke(subject);
            if (value is IInterceptorSubject childProxy)
            {
                obj[propertyName] = childProxy.ToJsonObject();
            }
            else if (value is ICollection collection && collection.OfType<IInterceptorSubject>().Any())
            {
                var children = new JsonArray();
                foreach (var arrayProxyItem in collection.OfType<IInterceptorSubject>())
                {
                    children.Add(arrayProxyItem.ToJsonObject());
                }
                obj[propertyName] = children;
            }
            else
            {
                obj[propertyName] = JsonValue.Create(value);
            }
        }

        var context = subject.Interceptor as IProxyContext;
        var registry = context?.GetHandler<IProxyRegistry>()
            ?? throw new InvalidOperationException($"The {nameof(IProxyRegistry)} is missing.");

        if (registry?.KnownProxies.TryGetValue(subject, out var metadata) == true)
        {
            foreach (var property in metadata
                .Properties
                .Where(p => p.Value.HasGetter && 
                            subject.Properties.ContainsKey(p.Key) == false))
            {
                var propertyName = property.GetJsonPropertyName();
                var value = property.Value.GetValue();
                if (value is IInterceptorSubject childProxy)
                {
                    obj[propertyName] = childProxy.ToJsonObject();
                }
                else if (value is ICollection collection && collection.OfType<IInterceptorSubject>().Any())
                {
                    var children = new JsonArray();
                    foreach (var arrayProxyItem in collection.OfType<IInterceptorSubject>())
                    {
                        children.Add(arrayProxyItem.ToJsonObject());
                    }
                    obj[propertyName] = children;
                }
                else
                {
                    obj[propertyName] = JsonValue.Create(value);
                }
            }
        }

        return obj;
    }

    private static string GetJsonPropertyName(IInterceptorSubject subject, SubjectPropertyMetadata property)
    {
        var attribute = property
            .Attributes
            .OfType<PropertyAttributeAttribute>()
            .FirstOrDefault();

        if (attribute is not null)
        {
            var propertyName = GetJsonPropertyName(subject, subject.Properties[attribute.PropertyName]);
            return $"{propertyName}@{attribute.AttributeName}";
        }

        return JsonNamingPolicy.CamelCase.ConvertName(property.Name);
    }

    public static string GetJsonPropertyName(this KeyValuePair<string, RegisteredProxyProperty> property)
    {
        var attribute = property
            .Value
            .Attributes
            .OfType<PropertyAttributeAttribute>()
            .FirstOrDefault();

        if (attribute is not null)
        {
            return property.Value
                .Parent.Properties
                .Single(p => p.Key == attribute.PropertyName) // TODO: Improve performance??
                .GetJsonPropertyName() + "@" + attribute.AttributeName;
        }

        return JsonNamingPolicy.CamelCase.ConvertName(property.Key);
    }

    public static (IInterceptorSubject?, SubjectPropertyMetadata) FindPropertyFromJsonPath(this IInterceptorSubject subject, string path)
    {
        return subject.FindPropertyFromJsonPath(path.Split('.'));
    }

    private static (IInterceptorSubject?, SubjectPropertyMetadata) FindPropertyFromJsonPath(this IInterceptorSubject subject, IEnumerable<string> segments)
    {
        var nextSegment = segments.First();
        nextSegment = ConvertToUpperCamelCase(nextSegment);

        segments = segments.Skip(1);
        if (segments.Any())
        {
            if (nextSegment.Contains('['))
            {
                var arraySegments = nextSegment.Split('[', ']');
                var index = int.Parse(arraySegments[1]);
                nextSegment = arraySegments[0];

                var collection = subject.Properties[nextSegment].GetValue?.Invoke(subject) as ICollection;
                var child = collection?.OfType<IInterceptorSubject>().ElementAt(index);
                return child is not null ? FindPropertyFromJsonPath(child, segments) : (null, default);
            }
            else
            {
                var child = subject.Properties[nextSegment].GetValue?.Invoke(subject) as IInterceptorSubject;
                return child is not null ? FindPropertyFromJsonPath(child, segments) : (null, default);
            }
        }
        else
        {
            return (subject, subject.Properties[nextSegment]);
        }
    }

    private static string ConvertToUpperCamelCase(string nextSegment)
    {
        if (string.IsNullOrEmpty(nextSegment))
        {
            return nextSegment;
        }

        var characters = nextSegment.ToCharArray();
        characters[0] = char.ToUpperInvariant(characters[0]);
        return new string(characters);
    }
}
