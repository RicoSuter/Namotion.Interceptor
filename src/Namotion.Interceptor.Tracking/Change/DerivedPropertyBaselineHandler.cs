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
    // The derived property names of a type's property dictionary, the static one of generated subjects. Weak, so that
    // neither a type nor its dictionary is kept; a subject whose dictionary differs, such as one with properties added
    // at run time, is enumerated instead.
    private static readonly ConditionalWeakTable<Type, TypeEntry> Entries = new();

    // Consecutive attaches mostly attach subjects of one type, which the thread's last entry answers without a lookup.
    // Keeps at most one type alive per thread, as the type gate does.
    [ThreadStatic]
    private static TypeEntry? _last;

    /// <inheritdoc />
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (!change.IsContextAttach)
        {
            return;
        }

        var subject = change.Subject;
        var properties = subject.Properties;
        if (GetEntry(subject.GetType()).TryGetNames(properties) is { } names)
        {
            foreach (var name in names)
            {
                DerivedPropertyOwnership.CaptureAttachBaseline(new PropertyReference(subject, name));
            }

            return;
        }

        foreach (var (name, metadata) in properties)
        {
            if (IsCaptured(metadata))
            {
                DerivedPropertyOwnership.CaptureAttachBaseline(new PropertyReference(subject, name));
            }
        }
    }

    private static bool IsCaptured(SubjectPropertyMetadata metadata) => metadata.IsDerived && metadata.GetValue is not null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TypeEntry GetEntry(Type type)
    {
        var last = _last;
        if (last is not null && ReferenceEquals(last.Type, type))
        {
            return last;
        }

        var entry = Entries.GetValue(type, static type => new TypeEntry(type));
        _last = entry;
        return entry;
    }

    private sealed class TypeEntry(Type type)
    {
        // Replaced as a whole, so a reader sees a dictionary with its own names.
        private Snapshot? _snapshot;

        public Type Type { get; } = type;

        // The names when the dictionary is the one cached for the type, or null. The first dictionary seen, and one that
        // replaces a collected one, is cached: a type's static dictionary is never collected, so it stays.
        public string[]? TryGetNames(IReadOnlyDictionary<string, SubjectPropertyMetadata> properties)
        {
            var snapshot = Volatile.Read(ref _snapshot);
            if (snapshot is not null && snapshot.Properties.TryGetTarget(out var cached))
            {
                return ReferenceEquals(cached, properties) ? snapshot.Names : null;
            }

            var names = FindNames(properties);
            Volatile.Write(ref _snapshot, new Snapshot(new WeakReference<IReadOnlyDictionary<string, SubjectPropertyMetadata>>(properties), names));
            return names;
        }

        private static string[] FindNames(IReadOnlyDictionary<string, SubjectPropertyMetadata> properties)
        {
            List<string>? names = null;
            foreach (var (name, metadata) in properties)
            {
                if (IsCaptured(metadata))
                {
                    (names ??= []).Add(name);
                }
            }

            return names?.ToArray() ?? [];
        }
    }

    private sealed record Snapshot(WeakReference<IReadOnlyDictionary<string, SubjectPropertyMetadata>> Properties, string[] Names);
}
