using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Components.Inputs;

/// <summary>
/// Decides what a path picker lets the user select for a property marked with <see cref="SubjectPathAttribute"/>.
/// </summary>
public static class SubjectPathSelection
{
    /// <summary>
    /// Gets whether the path of the property can be selected: without a <paramref name="subjectType"/> a value that
    /// holds no subjects, otherwise a reference that currently leads to an instance of the type.
    /// </summary>
    public static bool CanSelectProperty(RegisteredSubjectProperty property, Type? subjectType)
    {
        return CanSelectProperty(property, subjectType, SubjectPathKinds.Value, writable: false);
    }

    /// <summary>
    /// Gets whether the path of the property can be selected. Without a <paramref name="subjectType"/> the property
    /// holds no subjects, matches <paramref name="kind"/> and, when <paramref name="writable"/> is set, has a public
    /// setter. With a <paramref name="subjectType"/> the property is a reference that currently leads to an instance
    /// of the type, and <paramref name="kind"/> and <paramref name="writable"/> are ignored.
    /// </summary>
    public static bool CanSelectProperty(
        RegisteredSubjectProperty property, Type? subjectType, SubjectPathKinds kind, bool writable)
    {
        if (subjectType is not null)
        {
            return CanSelectReference(property, subjectType);
        }

        var propertyKind = GetKind(property);
        if (propertyKind != SubjectPathKinds.Operation && property.CanContainSubjects)
        {
            return false;
        }

        return (propertyKind & kind) != 0 &&
            (!writable || (propertyKind != SubjectPathKinds.Operation && property.HasPublicSetter));
    }

    /// <summary>
    /// Gets whether the type of the property, a nullable type as its underlying type, is one of <paramref name="kind"/>.
    /// A property that holds an operation or a query matches only <see cref="SubjectPathKinds.Operation"/>.
    /// </summary>
    public static bool MatchesKind(RegisteredSubjectProperty property, SubjectPathKinds kind)
    {
        return (GetKind(property) & kind) != 0;
    }

    /// <summary>
    /// Gets whether the path of the subject can be selected: an instance of <paramref name="subjectType"/>, and no
    /// subject without a type.
    /// </summary>
    public static bool CanSelectSubject(IInterceptorSubject subject, Type? subjectType)
    {
        return subjectType is not null && subjectType.IsInstanceOfType(subject);
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

    private static SubjectPathKinds GetKind(RegisteredSubjectProperty property)
    {
        if (property.TryGetMethodMetadata() is not null)
        {
            return SubjectPathKinds.Operation;
        }

        var type = Nullable.GetUnderlyingType(property.Type) ?? property.Type;
        if (type == typeof(bool))
        {
            return SubjectPathKinds.Boolean;
        }

        // Before the type code, which is the one of the underlying integral type for an enum.
        if (type.IsEnum)
        {
            return SubjectPathKinds.Enum;
        }

        return Type.GetTypeCode(type) is >= TypeCode.SByte and <= TypeCode.Decimal
            ? SubjectPathKinds.Number
            : SubjectPathKinds.OtherValue;
    }
}
