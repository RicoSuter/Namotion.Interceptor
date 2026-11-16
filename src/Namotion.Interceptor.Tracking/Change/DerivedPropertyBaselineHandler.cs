using System.Runtime.CompilerServices;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Records the value of each derived property of a subject that attaches, before any other lifecycle handler makes
/// the subject observable, such as through the registry, or publishes its writes, through context inheritance. The
/// derived properties attach only after the subject's handlers ran, and an attach whose value differs from the
/// recorded one publishes the difference, see "Attach" in docs/design/tracking-derived-properties.md.
/// </summary>
[RunsFirst]
internal sealed class DerivedPropertyBaselineHandler : ILifecycleHandler
{
    // The derived property names of a property dictionary: per type for the static dictionary of generated subjects,
    // per subject for one with properties added at run time. Weak, so that neither the dictionary nor its type is kept.
    private static readonly ConditionalWeakTable<IReadOnlyDictionary<string, SubjectPropertyMetadata>, string[]> NamesByProperties = new();

    // Consecutive attaches mostly attach subjects of one type, which the thread's last entry answers without a lookup.
    [ThreadStatic]
    private static IReadOnlyDictionary<string, SubjectPropertyMetadata>? _lastProperties;

    [ThreadStatic]
    private static string[]? _lastNames;

    /// <inheritdoc />
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (!change.IsContextAttach)
        {
            return;
        }

        var subject = change.Subject;
        foreach (var name in GetDerivedPropertyNames(subject.Properties))
        {
            DerivedPropertyOwnership.CaptureAttachBaseline(new PropertyReference(subject, name));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string[] GetDerivedPropertyNames(IReadOnlyDictionary<string, SubjectPropertyMetadata> properties)
    {
        if (ReferenceEquals(_lastProperties, properties))
        {
            return _lastNames!;
        }

        var names = NamesByProperties.GetValue(properties, static properties => FindDerivedPropertyNames(properties));
        _lastProperties = properties;
        _lastNames = names;
        return names;
    }

    private static string[] FindDerivedPropertyNames(IReadOnlyDictionary<string, SubjectPropertyMetadata> properties)
    {
        List<string>? names = null;
        foreach (var (name, metadata) in properties)
        {
            if (metadata.IsDerived && metadata.GetValue is not null)
            {
                (names ??= []).Add(name);
            }
        }

        return names?.ToArray() ?? [];
    }
}
