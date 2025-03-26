using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources;

public static class SubjectDataExtensions
{
    private const string IsChangingFromSourceKey = "Namotion.IsChangingFromSource";

    /// <summary>
    /// Sets the value of the property and marks the assignment as applied by the specified source.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="source">The source.</param>
    /// <param name="valueFromSource">The value</param>
    public static void SetValueFromSource(this RegisteredSubjectProperty property, ISubjectSource source, object? valueFromSource)
    {
        property.Property.AddOrUpdatePropertyData<ISubjectSource[]?>(IsChangingFromSourceKey, 
            list => list is not null ? list.Concat([source]).ToArray() : [source]);

        try
        {
            property.SetValue(valueFromSource);
        }
        finally
        {
            property.Property.AddOrUpdatePropertyData<ISubjectSource[]?>(IsChangingFromSourceKey, 
                list => list?.Where(p => p != source).ToArray());
        }
    }

    /// <summary>
    /// Checks if the property change is from the specified source.
    /// </summary>
    /// <param name="change">The property change.</param>
    /// <param name="source">The source find.</param>
    /// <returns>The result.</returns>
    public static bool IsChangingFromSource(this SubjectPropertyChange change, ISubjectSource source)
    {
        return change.TryGetPropertySnapshotData(IsChangingFromSourceKey, out var value)
           && value is ISubjectSource[] sources 
           && sources.Contains(source);
    }
}
