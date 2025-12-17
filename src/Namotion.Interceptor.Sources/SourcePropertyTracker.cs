using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Sources;

/// <summary>
/// Tracks properties owned by a source and manages their lifecycle.
/// Automatically sets and removes source ownership when properties are tracked/untracked,
/// and cleans up when subjects are detached from the object graph.
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// public class MySource : ISubjectSource, IDisposable
/// {
///     private readonly SourcePropertyTracker _propertyTracker;
///
///     public MySource(ILogger logger)
///     {
///         _propertyTracker = new SourcePropertyTracker(this, logger);
///     }
///
///     public Task&lt;IDisposable?&gt; StartListeningAsync(SubjectPropertyWriter writer, CancellationToken ct)
///     {
///         _propertyTracker.SubscribeToLifecycle(_root);
///
///         foreach (var property in GetMyProperties())
///         {
///             _propertyTracker.Track(property.Reference);
///         }
///
///         return Task.FromResult&lt;IDisposable?&gt;(null);
///     }
///
///     public void Dispose() => _propertyTracker.Dispose();
/// }
/// </code>
/// </remarks>
public class SourcePropertyTracker : PropertyTracker
{
    private readonly ISubjectSource _source;

    /// <summary>
    /// Initializes a new instance of the <see cref="SourcePropertyTracker"/> class.
    /// </summary>
    /// <param name="source">The source that owns the tracked properties.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public SourcePropertyTracker(ISubjectSource source, ILogger? logger = null)
        : base(logger)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>
    /// Gets the source that owns the tracked properties.
    /// </summary>
    protected ISubjectSource Source => _source;

    /// <inheritdoc />
    protected override void OnPropertyTracked(PropertyReference property)
    {
        property.SetSource(_source, Logger);
    }

    /// <inheritdoc />
    protected override void OnPropertyUntracked(PropertyReference property)
    {
        property.RemoveSource(_source);
    }
}
