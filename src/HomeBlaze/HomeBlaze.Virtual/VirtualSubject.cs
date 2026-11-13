using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Virtual;

/// <summary>
/// A subject whose properties are computed from other subjects or bound to them, as configured in
/// <see cref="Properties"/>. The properties are added when the subject attaches; a change of them creates the subject
/// again, as the registry cannot remove a property (see #964).
/// </summary>
[Category("Virtual")]
[Description("Subject whose properties are computed from or bound to other subjects.")]
[DisplayName("Virtual subject")]
[SubjectIcon("AccountTree")]
[InterceptorSubject]
public partial class VirtualSubject : IConfigurable, IMonitoredService, ITitleProvider, IIconProvider, IVirtualSubject, ILifecycleHandler
{
    private readonly SubjectPathResolver _pathResolver;
    private readonly VirtualLevel _level;
    private readonly ILogger _logger;

    /// <summary>Creates a virtual subject; the services come from dependency injection.</summary>
    public VirtualSubject(SubjectPathResolver pathResolver, VirtualPropertyAwaiter awaiter, ILogger<VirtualSubject> logger)
    {
        _pathResolver = pathResolver;
        _logger = logger;
        _level = new VirtualLevel(this, new VirtualRuntime(pathResolver, awaiter, logger), VirtualEntryPlace.Root);
        Status = ServiceStatus.Stopped;
    }

    /// <summary>The title of the subject.</summary>
    [Configuration]
    public partial string? Title { get; set; }

    /// <summary>The entries that become the properties of the subject, by name.</summary>
    [Configuration(RecreateOnChange = true)]
    public partial IReadOnlyDictionary<string, VirtualProperty>? Properties { get; set; }

    /// <inheritdoc />
    [State(IsDiscrete = true, Position = 910)]
    public partial ServiceStatus Status { get; internal set; }

    /// <inheritdoc />
    [State(Position = 911)]
    public partial string? StatusMessage { get; internal set; }

    /// <inheritdoc />
    public string? IconName => "AccountTree";

    /// <inheritdoc />
    [Derived]
    public string? IconColor => Status == ServiceStatus.Error ? "Error" : null;

    /// <inheritdoc />
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    void ILifecycleHandler.HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (!ReferenceEquals(change.Subject, this))
        {
            return;
        }

        if (change.IsContextAttach)
        {
            var properties = Properties;
            // The registry has placed the subject by now, so its path is known.
            var ownPath = _pathResolver.GetPath(this, PathStyle.Canonical);
            if (!_level.Attach(properties, prefix: null, VirtualPropertyValidator.FindCyclicEntries(properties, ownPath)))
            {
                _logger.LogError("The virtual subject {Title} added no properties: {Message}", Title, VirtualLevel.MissingRegistryMessage);
                Status = ServiceStatus.Error;
                StatusMessage = VirtualLevel.MissingRegistryMessage;
                return;
            }

            var issues = VirtualPropertyValidator.Validate(properties, ownPath);
            if (issues.IsEmpty)
            {
                Status = ServiceStatus.Running;
                StatusMessage = null;
            }
            else
            {
                var message = string.Join("; ", issues.Select(issue => $"{issue.Path}: {issue.Message}"));
                _logger.LogWarning("The virtual subject {Path} skipped entries: {Issues}", ownPath ?? Title, message);
                Status = ServiceStatus.Error;
                StatusMessage = message;
            }
        }
        else if (change.IsContextDetach)
        {
            _level.Detach();
            Status = ServiceStatus.Stopped;
            StatusMessage = null;
        }
    }
}
