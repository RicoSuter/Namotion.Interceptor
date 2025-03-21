using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources.Extensions;

public static class SubjectDataExtensions
{
    private const string IsChangingFromSourceKey = "Namotion.IsChangingFromSource";

    public static void SetValueFromSource(this PropertyReference property, ISubjectSource source, object? valueFromSource)
    {
        // TODO: Use async local here instead? Verify correctness of the method

        var contexts = property.GetOrAddPropertyData(IsChangingFromSourceKey, () => new HashSet<ISubjectSource>())!;
        lock (contexts)
        {
            contexts.Add(source);
        }

        try
        {
            property.Metadata.SetValue?.Invoke(property.Subject, valueFromSource);
        }
        finally
        {
            lock (contexts)
            {
                contexts.Remove(source);
            }
        }
    }

    public static bool IsChangingFromSource(this SubjectPropertyChange change, ISubjectSource source)
    {
        var contexts = change.Property.GetOrAddPropertyData(IsChangingFromSourceKey, () => new HashSet<ISubjectSource>())!;
        lock (contexts)
        {
            return contexts.Contains(source);
        }
    }
}
