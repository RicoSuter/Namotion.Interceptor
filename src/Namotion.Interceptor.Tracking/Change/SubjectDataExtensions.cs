namespace Namotion.Interceptor.Tracking.Change;

public static class SubjectDataExtensions
{
    [ThreadStatic]
    private static object? _currentChangingSource;

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
    
    /// <summary>
    /// Sets the value of the property and marks the assignment as applied by the specified source.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="source">The source.</param>
    /// <param name="valueFromSource">The value</param>
    public static void SetValueFromSource(this PropertyReference property, object source, object? valueFromSource)
    {
        _currentChangingSource = source;
        try
        {
            property.SetValue(valueFromSource);
        }
        finally
        {
            _currentChangingSource = null;
        }
    }

    /// <summary>
    /// Executes the action and marks the assignment as applied by the specified source.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="source">The source.</param>
    /// <param name="action">The action</param>
    public static void ApplyChangesFromSource(this PropertyReference property, object source, Action action)
    {
        _currentChangingSource = source;
        try
        {
            action();
        }
        finally
        {
            _currentChangingSource = null;
        }
    }

    internal static object? GetChangingSource(this PropertyReference property)
    {
        return _currentChangingSource;
    }
}