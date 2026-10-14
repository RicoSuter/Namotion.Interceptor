namespace Namotion.Interceptor.Validation;

/// <summary>
/// Validation input for a single property value. Origin is the attempted origin of the write (validation runs
/// before the terminal write): Local for user writes, FromSource for inbound source applies. When a transaction
/// commit checks its final set, Origin is the captured origin of the change.
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
