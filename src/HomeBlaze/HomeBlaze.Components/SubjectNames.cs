using System.Collections;
using System.Text;
using HomeBlaze.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Components;

/// <summary>
/// Names a subject for a heading, such as the title and subtitle of its edit dialog.
/// </summary>
internal static class SubjectNames
{
    /// <summary>The name of the text property that names a widget without a title.</summary>
    public const string LabelPropertyName = "Label";

    /// <summary>
    /// Gets the name of the type as a block of a page names it: the full type name. A generic type is shown with its
    /// type arguments in angle brackets instead of the arity suffix.
    /// </summary>
    public static string GetTypeName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        var builder = new StringBuilder();
        AppendGenericTypeName(builder, type);
        return builder.ToString();
    }

    /// <summary>
    /// Gets the title of the subject: its <see cref="ITitleProvider.Title"/>, otherwise the text of its
    /// <see cref="LabelPropertyName"/> property, otherwise null.
    /// </summary>
    public static string? TryGetTitle(IInterceptorSubject subject) =>
        TryGetTitle(subject, subject.TryGetRegisteredSubject()?.TryGetProperty(LabelPropertyName));

    /// <summary>
    /// Gets the title of the subject like <see cref="TryGetTitle(IInterceptorSubject)"/>, with its label property
    /// already looked up.
    /// </summary>
    public static string? TryGetTitle(IInterceptorSubject subject, RegisteredSubjectProperty? labelProperty)
    {
        if (subject is ITitleProvider { Title: { Length: > 0 } title })
        {
            return title;
        }

        return labelProperty?.GetValue() is string { Length: > 0 } label ? label : null;
    }

    /// <summary>
    /// Gets the title of the subject like <see cref="TryGetTitle(IInterceptorSubject)"/>, otherwise the title of the first
    /// of its child subjects that has one, in the order of its properties and of their values. This names the content of a
    /// layout cell, which on a page is the page content that holds the widgets of the cell.
    /// </summary>
    public static string? TryGetContentTitle(IInterceptorSubject subject)
    {
        if (TryGetTitle(subject) is { } title)
        {
            return title;
        }

        if (subject.TryGetRegisteredSubject() is not { } registeredSubject)
        {
            return null;
        }

        // One level only: on a page the subject of a cell or a node is its page content, and its direct children are the
        // widgets the user placed there. A deeper search would name a cell after a widget nested in one of them.
        foreach (var property in registeredSubject.Properties)
        {
            if (!property.CanContainSubjects)
            {
                continue;
            }

            // The value and not the children of the registry, which keeps a reused child at its old position when the
            // content is parsed again.
            var childTitle = property.GetValue() switch
            {
                IInterceptorSubject child => TryGetTitle(child),
                IDictionary dictionary => TryGetFirstTitle(dictionary.Values),
                IEnumerable children => TryGetFirstTitle(children),
                _ => null
            };

            if (childTitle is not null)
            {
                return childTitle;
            }
        }

        return null;
    }

    private static string? TryGetFirstTitle(IEnumerable children)
    {
        foreach (var child in children)
        {
            if (child is IInterceptorSubject subject && TryGetTitle(subject) is { } title)
            {
                return title;
            }
        }

        return null;
    }

    private static void AppendGenericTypeName(StringBuilder builder, Type type)
    {
        if (type.IsNested && type.DeclaringType is { } declaringType)
        {
            builder.Append(GetTypeName(declaringType)).Append('+');
        }
        else if (!string.IsNullOrEmpty(type.Namespace))
        {
            builder.Append(type.Namespace).Append('.');
        }

        var name = type.Name;
        var aritySeparator = name.IndexOf('`');
        builder.Append(aritySeparator < 0 ? name : name.AsSpan(0, aritySeparator)).Append('<');

        var arguments = type.GetGenericArguments();
        for (var index = 0; index < arguments.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(GetTypeName(arguments[index]));
        }

        builder.Append('>');
    }
}
