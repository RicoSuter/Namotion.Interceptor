using HomeBlaze.Abstractions;
using Namotion.Interceptor.Expressions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Services.Display;

/// <summary>
/// What a value map and the expressions of its cases see of a value: the value with the property it was read from, as the
/// variable <c>value</c>, and its subject, as the variable <c>subject</c>. A property that holds a subject binds that
/// subject and no value; any other property binds its value and its owner.
/// </summary>
/// <param name="property">The property the value was read from, or null.</param>
/// <param name="value">The value, or null when it is missing or the binding has a subject only.</param>
/// <param name="subject">The subject of the value, or null.</param>
public readonly struct ValueBinding(RegisteredSubjectProperty? property, object? value, IInterceptorSubject? subject)
{
    /// <summary>The property the value was read from, or null.</summary>
    public RegisteredSubjectProperty? Property { get; } = property;

    /// <summary>The value, or null when it is missing or the binding has a subject only.</summary>
    public object? Value { get; } = value;

    /// <summary>The subject of the value, or null.</summary>
    public IInterceptorSubject? Subject { get; } = subject;

    /// <summary>Whether the binding has no property, no value and no subject, as for a path that leads nowhere.</summary>
    public bool IsEmpty => Property is null && Value is null && Subject is null;

    /// <summary>Binds the value of a property, or the subject it references.</summary>
    public static ValueBinding FromProperty(RegisteredSubjectProperty property) =>
        property.IsSubjectReference
            ? new ValueBinding(property, null, property.GetValue() as IInterceptorSubject)
            : new ValueBinding(property, property.GetValue(), property.Subject);

    /// <summary>
    /// Binds the path of a widget: the value of the property it ends at, or the subject it leads to when it ends at no
    /// property, such as the name of a block or an entry of a collection. A path that is null, empty, malformed or leads
    /// nowhere gives an empty binding.
    /// </summary>
    public static ValueBinding FromPath(SubjectPathResolver resolver, IInterceptorSubject from, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return default;
        }

        try
        {
            if (resolver.ResolveScopedDisplayableProperty(path, PathStyle.Canonical, from) is { } property)
            {
                return FromProperty(property);
            }

            return resolver.ResolveScopedSubject(path, PathStyle.Canonical, from) is { } subject
                ? new ValueBinding(null, null, subject)
                : default;
        }
        catch
        {
            // The path is written by the author of a page, and a malformed one must not take the page down.
            return default;
        }
    }

    /// <summary>
    /// Binds the result of an expression, with the same rule for its property as <see cref="FromProperty"/>. A result that
    /// has no property and is a subject, such as the variable <c>subject</c> itself, binds that subject and no value.
    /// </summary>
    public static ValueBinding FromExpressionValue(ExpressionValue result)
    {
        if (result.Property is not { } property)
        {
            if (result.Value is IInterceptorSubject subject)
            {
                return new ValueBinding(null, null, subject);
            }

            return new ValueBinding(null, result.IsText ? null : result.Value, null);
        }

        return property.IsSubjectReference
            ? new ValueBinding(property, null, result.Value as IInterceptorSubject)
            : new ValueBinding(property, result.Value, property.Subject);
    }
}
