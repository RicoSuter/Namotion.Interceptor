using System.Collections.Immutable;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Services;

/// <summary>
/// Receives the graph route of a resolution of <see cref="SubjectPathResolver"/>: what the result depends on besides the
/// path, so that a caller can tell which changes of the graph may change the result. A resolution served from a cache
/// reports the route it was computed with. Called on the thread that resolves, while it resolves.
/// </summary>
public interface ISubjectPathRecorder
{
    /// <summary>
    /// A property whose change may change the result: a property that holds subjects which the resolution read, or a
    /// property that it looked up on a registered subject and did not find, since a property may be added at run time.
    /// </summary>
    void AddProperty(PropertyReference property);

    /// <summary>
    /// The result depends on the parents of <paramref name="subject"/>, which are <paramref name="parents"/> now: on the
    /// properties that hold it, and on a parent that it gains later, which no property reported to the recorder tells.
    /// </summary>
    void AddParentStep(IInterceptorSubject subject, ImmutableArray<SubjectPropertyParent> parents);

    /// <summary>The route is not known exactly: a change of any property that holds subjects may change the result.</summary>
    void MarkUnbounded();
}
