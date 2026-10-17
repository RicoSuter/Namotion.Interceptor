using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Host.Services.Display;

/// <summary>
/// Extension methods for resolving display properties from subjects.
/// </summary>
public static class SubjectDisplayExtensions
{
    /// <summary>
    /// Gets the display title for a subject.
    /// Falls back to type name if ITitleProvider not implemented.
    /// </summary>
    public static string GetTitle(this IInterceptorSubject subject)
    {
        if (subject is ITitleProvider titleProvider && !string.IsNullOrEmpty(titleProvider.Title))
            return titleProvider.Title;

        return subject.GetType().Name;
    }

    /// <summary>
    /// Gets the label of a child subject listed under its parent property.
    /// A direct subject reference is labeled with the property display name.
    /// A collection or dictionary item is labeled with its <see cref="ITitleProvider"/> title,
    /// else its index or key, else its type name.
    /// </summary>
    public static string GetChildDisplayName(this RegisteredSubjectProperty property, SubjectPropertyChild child)
    {
        if (child.Index is null)
            return property.GetDisplayName();

        if (child.Subject is ITitleProvider titleProvider && !string.IsNullOrEmpty(titleProvider.Title))
            return titleProvider.Title;

        var key = child.Index.ToString();
        return string.IsNullOrEmpty(key) ? child.Subject.GetType().Name : key;
    }

    /// <summary>
    /// Gets a label that identifies a collection or dictionary item: the display name followed by
    /// the index or key in parentheses when they differ, else the display name.
    /// </summary>
    public static string GetIdentifyingDisplayName(string displayName, object? index)
    {
        var key = index?.ToString();
        if (string.IsNullOrEmpty(key) || key == displayName)
            return displayName;

        return string.IsNullOrEmpty(displayName) ? key : $"{displayName} ({key})";
    }

}
