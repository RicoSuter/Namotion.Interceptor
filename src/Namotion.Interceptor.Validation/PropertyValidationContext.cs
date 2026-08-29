namespace Namotion.Interceptor.Validation;

/// <summary>
/// Validation input for a single property write. Under the current contract validators run only for
/// local writes, so <see cref="Origin"/> is always <see cref="ChangeOriginKind.Local"/> when the
/// validator is invoked by <c>ValidationInterceptor</c>. It is retained because callers that invoke
/// validators directly, such as the ASP.NET Core update endpoint, construct the context themselves.
/// </summary>
public readonly struct PropertyValidationContext<TProperty>(
    PropertyReference property, TProperty value, ChangeOrigin origin)
{
    /// <summary>Gets the property being written.</summary>
    public PropertyReference Property { get; } = property;

    /// <summary>Gets the new value to validate.</summary>
    public TProperty Value { get; } = value;

    /// <summary>Gets the attempted origin of the write; always Local when invoked from the interceptor.</summary>
    public ChangeOrigin Origin { get; } = origin;
}
