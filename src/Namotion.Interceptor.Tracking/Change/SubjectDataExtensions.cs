namespace Namotion.Interceptor.Tracking.Change;

public static class SubjectDataExtensions
{
    private const string IsChangingFromSourceKey = "Namotion.IsChangingFromSource";

    /// <summary>
    /// Sets the value of the property and marks the assignment as applied by the specified source.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="source">The source.</param>
    /// <param name="valueFromSource">The value</param>
    public static void SetValueFromSource(this PropertyReference property, object source, object? valueFromSource)
    {
        property.AddOrUpdatePropertyData<object?>(IsChangingFromSourceKey, _ => source);
        try
        {
            property.SetValue(valueFromSource);
        }
        finally
        {
            property.AddOrUpdatePropertyData<object?>(IsChangingFromSourceKey, _ => null);
        }
    }

    /// <summary>
    /// Executes the action and marks the assignment as applied by the specified source.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="source">The source.</param>
    /// <param name="action">The action</param>
    public static void ApplyWithChangingFromSource(this PropertyReference property, object source, Action action)
    {
        property.AddOrUpdatePropertyData<object?>(IsChangingFromSourceKey, _ => source);
        try
        {
            action();
        }
        finally
        {
            property.AddOrUpdatePropertyData<object?>(IsChangingFromSourceKey, _ => null);
        }
    }

    /// <summary>
    /// Checks if the property change is from the specified source.
    /// </summary>
    /// <param name="change">The property change.</param>
    /// <param name="source">The source find.</param>
    /// <returns>The result.</returns>
    public static bool IsChangingFromSource(this SubjectPropertyChange change, object source)
    {
        return change.Source == source;
    }

    internal static object? GetSource(this PropertyReference property)
    {
        return property.TryGetPropertyData(IsChangingFromSourceKey, out var source) ? source : null;
    }
}