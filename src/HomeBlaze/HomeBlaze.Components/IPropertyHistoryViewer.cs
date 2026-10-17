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
