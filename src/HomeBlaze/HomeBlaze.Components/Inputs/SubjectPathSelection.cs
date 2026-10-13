using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Components.Inputs;

/// <summary>
/// Decides what a path picker lets the user select for a property marked with <see cref="SubjectPathAttribute"/>.
/// </summary>
public static class SubjectPathSelection
{
    /// <summary>
    /// Gets whether the path of the property can be selected: without a <paramref name="subjectType"/> a property
    /// that holds no subjects, otherwise a reference that currently leads to an instance of the type.
    /// </summary>
    public static bool CanSelectProperty(RegisteredSubjectProperty property, Type? subjectType)
    {
        if (subjectType is null)
        {
            return !property.CanContainSubjects;
        }

        if (!property.IsSubjectReference)
        {
            return false;
        }

        var children = property.Children;
        return children.Length > 0 && subjectType.IsInstanceOfType(children[0].Subject);
    }

    /// <summary>
    /// Gets whether the path of the subject can be selected: an instance of <paramref name="subjectType"/>, and no
    /// subject without a type.
    /// </summary>
    public static bool CanSelectSubject(IInterceptorSubject subject, Type? subjectType)
    {
        return subjectType is not null && subjectType.IsInstanceOfType(subject);
    }
}
