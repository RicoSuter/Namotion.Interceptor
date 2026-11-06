using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Expressions;

/// <summary>
/// The values of the properties that the triggers of an automation read, in the order the worker observed their changes.
/// A trigger evaluates against these and not against the graph, so a short pulse that the graph has already left is
/// still seen. Used by the worker only.
/// </summary>
internal sealed class ObservedValues
{
    private readonly Dictionary<PropertyReference, ObservedValue> _values = new();

    /// <summary>Gets the observed value, or reads the property and observes it with the revision of its last write.</summary>
    public object? GetValue(RegisteredSubjectProperty property)
    {
        var reference = property.Reference;
        if (_values.TryGetValue(reference, out var observed))
        {
            return observed.Value;
        }

        // The revision first: a write between the two reads gives a newer value with an older revision, so its change
        // is applied again later instead of being dropped as stale.
        reference.TryGetWriteState(includeSourceCommitsInRevision: true, out var revision, out _);
        var value = property.GetValue();
        _values[reference] = new ObservedValue(value, revision);
        return value;
    }

    /// <summary>
    /// Observes a change; returns false for a change that is not newer than the observed value, which then stays. A
    /// revision of 0 comes from a change outside a committed write and is always applied.
    /// </summary>
    public bool TryApply(PropertyReference property, object? value, long revision)
    {
        if (revision != 0 && _values.TryGetValue(property, out var observed) && observed.Revision >= revision)
        {
            return false;
        }

        _values[property] = new ObservedValue(value, revision);
        return true;
    }

    /// <summary>Forgets the properties that are not in the set, whose changes are no longer received.</summary>
    public void Retain(IReadOnlySet<PropertyReference> properties)
    {
        foreach (var property in _values.Keys.Where(property => !properties.Contains(property)).ToList())
        {
            _values.Remove(property);
        }
    }

    /// <summary>Forgets every value, so that each property is read again on its next evaluation.</summary>
    public void Clear() => _values.Clear();

    private readonly record struct ObservedValue(object? Value, long Revision);
}
