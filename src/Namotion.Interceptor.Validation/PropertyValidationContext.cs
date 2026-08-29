namespace Namotion.Interceptor.Validation;

/// <summary>
/// Validation input for a single property write. When the validator is invoked by
/// <c>ValidationInterceptor</c>, <see cref="Origin"/> is the write's effective origin and is never
/// that of an authoritative source, because those writes are not validated at all: it is either
/// <see cref="ChangeOriginKind.Local"/>, or <see cref="ChangeOriginKind.FromSource"/> for a write a
/// server-role connector accepted from a remote peer, which is untrusted input rather than truth.
/// </summary>
public readonly struct PropertyValidationContext<TProperty>(
    PropertyReference property, TProperty value, ChangeOrigin origin)
{
    /// <summary>Gets the property being written.</summary>
    public PropertyReference Property { get; } = property;

    /// <summary>Gets the new value to validate.</summary>
    public TProperty Value { get; } = value;

    /// <summary>Gets the effective origin of the write; see the type summary for what it can be.</summary>
    public ChangeOrigin Origin { get; } = origin;
}
