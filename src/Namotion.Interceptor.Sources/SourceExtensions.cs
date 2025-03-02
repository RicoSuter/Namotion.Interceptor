using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources;

public static class SourceExtensions
{
    private const string IsChangingFromSourceKey = "Namotion.IsChangingFromSource";

    public static void SetValueFromSource(this PropertyReference property, ISubjectSource source, object? valueFromSource)
    {
        var contexts = property.GetOrAddPropertyData(IsChangingFromSourceKey, () => new HashSet<ISubjectSource>())!;
        lock (contexts)
        {
            contexts.Add(source);
        }

        try
        {
            var newValue = valueFromSource;

            var currentValue = property.Metadata.GetValue?.Invoke(property.Subject);
            if (!Equals(currentValue, newValue))
            {
                property.Metadata.SetValue?.Invoke(property.Subject, newValue);
            }
        }
        finally
        {
            lock (contexts)
            {
                contexts.Remove(source);
            }
        }
    }

    public static bool IsChangingFromSource(this PropertyChangedContext change, ISubjectSource source)
    {
        var contexts = change.Property.GetOrAddPropertyData(IsChangingFromSourceKey, () => new HashSet<ISubjectSource>())!;
        lock (contexts)
        {
            return contexts.Contains(source);
        }
    }
}
