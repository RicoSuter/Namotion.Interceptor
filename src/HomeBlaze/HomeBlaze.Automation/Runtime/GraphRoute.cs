using System.Collections.Immutable;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The graph route of one stored resolution, see <see cref="HomeBlaze.Services.ISubjectPathRecorder"/>: the properties
/// whose change can make it resolve differently, each once, and whether a change of any property that holds subjects can
/// (<see cref="IsUnbounded"/>). Immutable, so that a resolution whose route stayed the same keeps the instance.
/// </summary>
internal sealed class GraphRoute(ImmutableArray<PropertyReference> properties, bool isUnbounded)
{
    public static GraphRoute Empty { get; } = new([], isUnbounded: false);

    public ImmutableArray<PropertyReference> Properties { get; } = properties;

    public bool IsUnbounded { get; } = isUnbounded;
}
