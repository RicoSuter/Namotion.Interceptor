namespace Namotion.Interceptor.Registry.Abstractions;

/// <summary>
/// Contributes attributes or derived properties to a subject's properties as the subject is
/// registered. Implementations are invoked by <c>SubjectRegistry</c> for every property of a subject,
/// including properties added dynamically by other initializers, and are picked up either from the
/// context (<c>context.AddService&lt;ISubjectPropertyInitializer&gt;(...)</c>) or from a .NET attribute
/// on the property that implements this interface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Implementations must be idempotent.</b> <see cref="InitializeProperty"/> runs again for the
/// same property every time the subject is re-attached to a graph, which includes moving a subject
/// from one parent to another: the move detaches it, dropping it from the registry, and re-attaching
/// builds a fresh registration. What does not reset is the subject itself, so any property or
/// attribute added the first time is still there when the initializer runs again.
/// </para>
/// <para>
/// Adding unconditionally therefore throws on the second run. Check first:
/// </para>
/// <code>
/// public void InitializeProperty(RegisteredSubjectProperty property)
/// {
///     if (property.IsAttribute || property.TryGetAttribute("Unit") is not null)
///     {
///         return;
///     }
///
///     property.AddAttribute("Unit", typeof(string), _ => "°C", null);
/// }
/// </code>
/// <para>
/// The same applies to work other than adding properties. An initializer that mutates external state
/// should expect to see each property more than once over the subject's lifetime.
/// </para>
/// </remarks>
public interface ISubjectPropertyInitializer
{
    /// <summary>
    /// Initializes a single registered property. Called once per property per registration, so more
    /// than once for a subject that is re-attached. Must tolerate being called again for a property
    /// it has already initialized.
    /// </summary>
    /// <param name="property">The property being registered.</param>
    void InitializeProperty(RegisteredSubjectProperty property);
}
