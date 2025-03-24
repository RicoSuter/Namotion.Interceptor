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
        // TODO: Use async local here instead? Verify correctness of the method

        var contexts = property.Property.GetOrAddPropertyData(IsChangingFromSourceKey, () => new HashSet<ISubjectSource>())!;
        lock (contexts)
            contexts.Add(source);

        try
        {
            property.SetValue(valueFromSource);
        }
        finally
        {
            lock (contexts)
                contexts.Remove(source);
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
        var contexts = change.Property.GetOrAddPropertyData(IsChangingFromSourceKey, () => new HashSet<ISubjectSource>())!;
        lock (contexts)
        {
            return contexts.Contains(source);
        }
    }
}
