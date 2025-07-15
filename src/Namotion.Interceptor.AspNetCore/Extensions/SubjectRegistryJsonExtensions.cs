using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.AspNetCore.Extensions;

public static class SubjectRegistryJsonExtensions
{
    public static string GetJsonPath(this PropertyReference property)
    {
        var registry = property.Subject.Context.TryGetService<ISubjectRegistry>();
        if (registry is not null)
        {
            // TODO: avoid endless recursion
            string? path = null;
            var parent = new SubjectParent(property, null);
            do
            {
                var attribute = registry
                    .TryGetRegisteredSubject(parent.Property.Subject)?
                    .TryGetProperty(parent.Property.Name)?
                    .ReflectionAttributes
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
            var parent = new SubjectParent(property, null);
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

    public static JsonObject ToJsonObject(this IInterceptorSubject subject, JsonSerializerOptions jsonSerializerOptions)
    {
        var registry = subject.Context.TryGetService<ISubjectRegistry>();
        var obj = new JsonObject();
        foreach (var property in subject
            .Properties
            .Where(p => p.Value.GetValue is not null))
        {
            var propertyName = GetJsonPropertyName(subject, property.Value, jsonSerializerOptions);
            var value = property.Value.GetValue?.Invoke(subject);
            if (value is IInterceptorSubject childProxy)
            {
                obj[propertyName] = childProxy.ToJsonObject(jsonSerializerOptions);
            }
            else if (value is ICollection collection && collection.OfType<IInterceptorSubject>().Any())
            {
                var children = new JsonArray();
                foreach (var arrayProxyItem in collection.OfType<IInterceptorSubject>())
                {
                    children.Add(arrayProxyItem.ToJsonObject(jsonSerializerOptions));
                }
                obj[propertyName] = children;
            }
            else
            {
                obj[propertyName] = JsonValue.Create(value);
            }
        }

        var registeredSubject = registry?.TryGetRegisteredSubject(subject);
        if (registeredSubject is not null)
        {
            foreach (var property in registeredSubject
                .Properties
                .Where(p => p.Value.HasGetter && 
                            subject.Properties.ContainsKey(p.Key) == false))
            {
                var propertyName = property.GetJsonPropertyName(jsonSerializerOptions);
                var value = property.Value.GetValue();
                if (value is IInterceptorSubject childProxy)
                {
                    obj[propertyName] = childProxy.ToJsonObject(jsonSerializerOptions);
                }
                else if (value is ICollection collection && collection.OfType<IInterceptorSubject>().Any())
                {
                    var children = new JsonArray();
                    foreach (var arrayProxyItem in collection.OfType<IInterceptorSubject>())
                    {
                        children.Add(arrayProxyItem.ToJsonObject(jsonSerializerOptions));
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

    private static string GetJsonPropertyName(IInterceptorSubject subject, SubjectPropertyMetadata property, JsonSerializerOptions jsonSerializerOptions)
    {
        var attribute = property
            .Attributes
            .OfType<PropertyAttributeAttribute>()
            .FirstOrDefault();

        if (attribute is not null)
        {
            var propertyName = GetJsonPropertyName(subject, subject.Properties[attribute.PropertyName], jsonSerializerOptions);
            return $"{propertyName}@{attribute.AttributeName}";
        }

        return jsonSerializerOptions.PropertyNamingPolicy?.ConvertName(property.Name) ?? property.Name;
    }

    private static string GetJsonPropertyName(this KeyValuePair<string, RegisteredSubjectProperty> property, JsonSerializerOptions jsonSerializerOptions)
    {
        var attribute = property
            .Value
            .ReflectionAttributes
            .OfType<PropertyAttributeAttribute>()
            .FirstOrDefault();

        if (attribute is not null)
        {
            // TODO(perf): Use cache to improve performance
            return property.Value
                .Parent.Properties
                .Single(p => p.Key == attribute.PropertyName)
                .GetJsonPropertyName(jsonSerializerOptions) + "@" + attribute.AttributeName;
        }

        return jsonSerializerOptions.PropertyNamingPolicy?.ConvertName(property.Key) ?? property.Key;
    }

    public static (IInterceptorSubject?, SubjectPropertyMetadata) FindPropertyFromJsonPath(this IInterceptorSubject subject, string path)
    {
        // TODO: Should return RegisteredSubjectProperty and use registry internally, also handle attributes
        return subject.FindPropertyFromJsonPath(path.Split('.'));
    }

    private static (IInterceptorSubject?, SubjectPropertyMetadata) FindPropertyFromJsonPath(this IInterceptorSubject subject, Span<string> segments)
    {
        while (true)
        {
            var nextSegment = segments[0];
            nextSegment = ConvertToUpperCamelCase(nextSegment);

            if (segments.Length > 1)
            {
                segments = segments[1..];
                if (nextSegment.Contains('['))
                {
                    var arraySegments = nextSegment.Split('[', ']');
                    var index = int.Parse(arraySegments[1]);
                    nextSegment = arraySegments[0];

                    if (subject.Properties.TryGetValue(nextSegment, out var property))
                    {
                        var collection = property.GetValue?.Invoke(subject) as ICollection;
                        var child = collection?.OfType<IInterceptorSubject>().ElementAt(index);
                        if (child is not null)
                        {
                            subject = child;
                            continue;
                        }
                    }

                    return (null, default);
                }
                else
                {
                    if (subject.Properties.TryGetValue(nextSegment, out var property) &&
                        property.GetValue?.Invoke(subject) is IInterceptorSubject child)
                    {
                        subject = child;
                        continue;
                    }

                    return (null, default);
                }
            }
            else
            {
                if (subject.Properties.TryGetValue(nextSegment, out var property))
                {
                    return (subject, property);
                }

                return (null, default);
            }
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
