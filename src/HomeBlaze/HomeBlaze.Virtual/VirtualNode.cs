using System.Collections.Immutable;
using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Virtual;

/// <summary>
/// A nested subject of a virtual subject, created from an entry with properties. Not configurable: it is edited and
/// saved with the virtual subject it belongs to.
/// </summary>
[InterceptorSubject]
public partial class VirtualNode : ITitleProvider, ILifecycleHandler
{
    private readonly IReadOnlyDictionary<string, VirtualProperty> _entries;
    private readonly string _path;
    private readonly ImmutableHashSet<string> _rejected;
    private readonly VirtualLevel _level;
    private readonly ILogger _logger;

    internal VirtualNode(string name, IReadOnlyDictionary<string, VirtualProperty> entries, string path, ImmutableHashSet<string> rejected, VirtualRuntime runtime)
    {
        Title = name;
        _entries = entries;
        _path = path;
        _rejected = rejected;
        _level = new VirtualLevel(this, runtime, VirtualEntryPlace.Nested);
        _logger = runtime.Logger;
    }

    /// <summary>The name of the entry the subject was created from.</summary>
    public string Title { get; }

    void ILifecycleHandler.HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (!ReferenceEquals(change.Subject, this))
        {
            return;
        }

        if (change.IsContextAttach)
        {
            if (!_level.Attach(_entries, _path, _rejected))
            {
                _logger.LogError("The virtual properties of {Path} were not added: {Message}", _path, VirtualLevel.MissingRegistryMessage);
            }
        }
        else if (change.IsContextDetach)
        {
            _level.Detach();
        }
    }
}
