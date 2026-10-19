using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Components;

/// <summary>
/// Shows the history of a property, which a value widget offers when the host registers a viewer.
/// </summary>
public interface IPropertyHistoryViewer
{
    /// <summary>
    /// Gets whether the property has a history that <see cref="ShowAsync"/> can show. Called on every render of a widget,
    /// so it must be cheap.
    /// </summary>
    bool CanShow(RegisteredSubjectProperty property);

    /// <summary>
    /// Shows the history of the property under the display name.
    /// </summary>
    Task ShowAsync(RegisteredSubjectProperty property, string displayName);
}

internal static class PropertyHistoryViewerExtensions
{
    /// <summary>
    /// Gets whether a widget offers the history of the property: outside edit mode, with a viewer that can show it.
    /// </summary>
    public static bool CanShow(
        [NotNullWhen(true)] this IPropertyHistoryViewer? viewer,
        [NotNullWhen(true)] RegisteredSubjectProperty? property,
        bool isEditing) =>
        !isEditing && viewer is not null && property is not null && viewer.CanShow(property);

    /// <summary>
    /// Shows the history of the property under the display name when <see cref="CanShow"/> allows it, else nothing.
    /// </summary>
    public static Task TryShowAsync(this IPropertyHistoryViewer? viewer, RegisteredSubjectProperty? property, bool isEditing, string displayName) =>
        viewer.CanShow(property, isEditing) ? viewer.ShowAsync(property, displayName) : Task.CompletedTask;
}
