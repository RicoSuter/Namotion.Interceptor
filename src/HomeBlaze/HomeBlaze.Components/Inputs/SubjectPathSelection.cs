using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Components.Inputs;

/// <summary>
/// Decides what a path picker lets the user select for a property marked with <see cref="SubjectPathAttribute"/>.
/// </summary>
internal static class SubjectPathSelection
{
    /// <summary>
    /// Gets whether the path of the property can be selected. Without a <see cref="SubjectPathAttribute.SubjectType"/>
    /// the property holds no subjects and matches <see cref="SubjectPathAttribute.Kind"/>,
    /// <see cref="SubjectPathAttribute.ValueTypes"/> and <see cref="SubjectPathAttribute.IsWritable"/>; an attribute that
    /// holds HomeBlaze metadata never matches. With a type the property is a reference that currently leads to an
    /// instance of the type.
    /// </summary>
    public static bool CanSelectProperty(RegisteredSubjectProperty property, SubjectPathAttribute path)
    {
        if (path.SubjectType is { } subjectType)
        {
            return CanSelectReference(property, subjectType);
        }

        // Checked before the method metadata, whose lookup evaluates getters for interface- and object-typed properties.
        if (property.IsAttribute)
        {
            return (path.Kind & SubjectPathKind.Attribute) != 0 &&
                property.AttributeMetadata.AttributeName is not (KnownAttributes.State or KnownAttributes.Configuration) &&
                CanSelectValue(property, path);
        }

        if (property.TryGetMethodMetadata() is not null)
        {
            return (path.Kind & SubjectPathKind.Operation) != 0 && !path.IsWritable;
        }

        return (path.Kind & SubjectPathKind.Property) != 0 && CanSelectValue(property, path);
    }

    /// <summary>
    /// Gets whether the type, a nullable type as its underlying type, matches <paramref name="valueTypes"/> as
    /// <see cref="SubjectPathAttribute.ValueTypes"/> describes; every type matches null.
    /// </summary>
    public static bool MatchesValueTypes(Type type, Type[]? valueTypes)
    {
        if (valueTypes is null)
        {
            return true;
        }

        type = Nullable.GetUnderlyingType(type) ?? type;
        foreach (var valueType in valueTypes)
        {
            if (valueType.IsGenericTypeDefinition ? IsClosedFormOf(type, valueType) : valueType.IsAssignableFrom(type))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the attributes of the property that a picker lists below it: those that can be selected and those that
    /// list attributes of their own.
    /// </summary>
    public static IEnumerable<RegisteredSubjectProperty> GetListedAttributes(
        RegisteredSubjectProperty property, Func<RegisteredSubjectProperty, bool> canSelectProperty)
    {
        foreach (var attribute in property.Attributes)
        {
            if (canSelectProperty(attribute) || GetListedAttributes(attribute, canSelectProperty).Any())
            {
                yield return attribute;
            }
        }
    }

    /// <summary>
    /// Gets whether <paramref name="attribute"/> is an attribute of <paramref name="property"/>, directly or as an
    /// attribute of one of its attributes.
    /// </summary>
    public static bool IsAttributeOf(RegisteredSubjectProperty? attribute, RegisteredSubjectProperty property)
    {
        while (attribute is { IsAttribute: true } && attribute.Parent == property.Parent)
        {
            var attributedPropertyName = attribute.AttributeMetadata.PropertyName;
            if (attributedPropertyName == property.Name)
            {
                return true;
            }

            attribute = property.Parent.TryGetProperty(attributedPropertyName);
        }

        return false;
    }

    /// <summary>
    /// Gets whether the path of the subject can be selected: an instance of <paramref name="subjectType"/>, and no
    /// subject without a type.
    /// </summary>
    public static bool CanSelectSubject(IInterceptorSubject subject, Type? subjectType)
    {
        return subjectType is not null && subjectType.IsInstanceOfType(subject);
    }

    private static bool CanSelectValue(RegisteredSubjectProperty property, SubjectPathAttribute path)
    {
        return !property.CanContainSubjects &&
            MatchesValueTypes(property.Type, path.ValueTypes) &&
            (!path.IsWritable || property.HasPublicSetter);
    }

    private static bool IsClosedFormOf(Type type, Type genericTypeDefinition)
    {
        // Char implements the generic math interfaces, but it is no number.
        if (type == typeof(char) && genericTypeDefinition.Namespace == "System.Numerics")
        {
            return false;
        }

        if (genericTypeDefinition.IsInterface)
        {
            foreach (var implementedInterface in type.GetInterfaces())
            {
                if (implementedInterface.IsGenericType && implementedInterface.GetGenericTypeDefinition() == genericTypeDefinition)
                {
                    return true;
                }
            }

            return false;
        }

        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == genericTypeDefinition)
            {
                return true;
            }
        }

        return false;
    }

    private static bool CanSelectReference(RegisteredSubjectProperty property, Type subjectType)
    {
        if (!property.IsSubjectReference)
        {
            return false;
        }

        var children = property.Children;
        return children.Length > 0 && subjectType.IsInstanceOfType(children[0].Subject);
    }
}
