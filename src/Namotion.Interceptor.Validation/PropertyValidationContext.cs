namespace Namotion.Interceptor.Validation;

/// <summary>
/// Validation input for a single property value. Origin is the attempted origin of the write: Local for user writes,
/// FromSource for inbound source applies, and Confirmed with the accepting source for a commit's apply of a value a
/// source accepted. In the commit check it is the captured origin of the change; in the pre-apply check it is the
/// origin the change is applied with, Confirmed for a change a source accepted.
/// </summary>
public readonly struct PropertyValidationContext<TProperty>(
    PropertyReference property, TProperty value, ChangeOrigin origin)
{
    /// <summary>Gets the property being written.</summary>
    public PropertyReference Property { get; } = property;

    /// <summary>Gets the new value to validate.</summary>
    public TProperty Value { get; } = value;

    /// <summary>Gets the attempted origin of the write (see the type summary).</summary>
    public ChangeOrigin Origin { get; } = origin;
}
