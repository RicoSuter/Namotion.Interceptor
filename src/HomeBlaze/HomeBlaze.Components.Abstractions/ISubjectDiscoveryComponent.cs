using HomeBlaze.Abstractions.Discovery;

namespace HomeBlaze.Components.Abstractions;

/// <summary>
/// A Setup or Edit component that takes a device the user picked on the Discover page of the create dialog. A
/// subject discovery is only used when the component the create dialog shows for its subject type implements this.
/// </summary>
public interface ISubjectDiscoveryComponent
{
    /// <summary>
    /// The result the user picked, which the component casts to the result type of its subject discovery, or null when
    /// the type was picked directly. Declare it as a <c>[Parameter]</c>.
    /// </summary>
    IDiscoveryResult? DiscoveryResult { get; set; }
}
