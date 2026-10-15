using System.Text;
using HomeBlaze.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Components;

/// <summary>
/// Names a subject for a heading, such as the title and subtitle of its edit dialog.
/// </summary>
public static class SubjectNames
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
