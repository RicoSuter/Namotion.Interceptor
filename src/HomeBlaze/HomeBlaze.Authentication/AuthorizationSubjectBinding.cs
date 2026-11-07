using System.Reactive.Concurrency;
using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using AuthorizationSubject = HomeBlaze.Authorization.Authorization;

namespace HomeBlaze.Authentication;

/// <summary>
/// Keeps the <see cref="AuthorizationProjection"/> attached to the single <see cref="AuthorizationSubject"/> of the graph,
/// detached while there is none or several, and adds one to the root storage when none exists. Thread-safe.
/// </summary>
/// <remarks>
/// A failure to attach or to add the subject is logged, not thrown, so that the startup still completes; without a
/// projection, administration refuses, as it finds no subject.
/// </remarks>
/// <remarks>The lifecycle handler runs after the registry, so that the registry already lists or no longer lists the subject.</remarks>
[RunsAfter(typeof(SubjectRegistry))]
internal sealed class AuthorizationSubjectBinding : ILifecycleHandler
{
    /// <summary>The file the binding adds to the root storage when no <see cref="AuthorizationSubject"/> exists.</summary>
    public const string FileName = "Authorization.json";

    private readonly IInterceptorSubjectContext _context;
    private readonly AuthorizationProjection _projection;
    private readonly ILogger _logger;
    private readonly Lock _attachLock = new();
    private int _isStarted;

    public AuthorizationSubjectBinding(IInterceptorSubjectContext context, AuthorizationProjection projection, ILogger<AuthorizationSubjectBinding> logger)
    {
        _context = context;
        _projection = projection;
        _logger = logger;
    }

    /// <summary>
    /// Follows the <see cref="AuthorizationSubject"/> subjects from now on and, when none exists and
    /// <paramref name="root"/> is a storage, waits until the storage loaded its files and adds <see cref="FileName"/> if
    /// there is still none; failing to add it is logged. Once completed, the projection is attached to what exists.
    /// </summary>
    public async Task StartAsync(IInterceptorSubject root, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _isStarted, 1) == 0)
        {
            _context.AddService(this);
        }

        Attach();
        await EnsureAuthorizationAsync(root, cancellationToken);
        // The lifecycle handler attaches in the background, so a subject added or loaded meanwhile may not be attached yet.
        Attach();
    }

    private async Task EnsureAuthorizationAsync(IInterceptorSubject root, CancellationToken cancellationToken)
    {
        if (GetAuthorizations().Count > 0)
        {
            return;
        }

        if (root is not IStorageContainer storage)
        {
            _logger.LogWarning("No Authorization subject exists and the root is no storage to add one to, so roles and users are not shown.");
            return;
        }

        _logger.LogInformation("Waiting for the root storage to load its files before adding {FileName}.", FileName);
        await WaitUntilConnectedAsync(root, storage, cancellationToken);
        if (GetAuthorizations().Count > 0)
        {
            return;
        }

        try
        {
            await storage.AddSubjectAsync(FileName, new AuthorizationSubject(), cancellationToken);
            _logger.LogInformation("Added {FileName} to the root storage to show the roles and users.", FileName);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not add {FileName} to the root storage, so roles and users are not shown.", FileName);
        }
    }

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.Subject is AuthorizationSubject && (change.IsContextAttach || change.IsContextDetach))
        {
            // Off the lifecycle callback, which must not write the graph; every run reads the registry again, so the
            // last run attaches what the registry lists last, in whatever order the runs end.
            ThreadPool.QueueUserWorkItem(_ => Attach());
        }
    }

    private void Attach()
    {
        try
        {
            lock (_attachLock)
            {
                var authorizations = GetAuthorizations();
                _projection.Attach(authorizations.Count == 1 ? authorizations[0] : null);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not show the roles and users in the Authorization subject.");
        }
    }

    private List<AuthorizationSubject> GetAuthorizations() =>
        _context.TryGetService<ISubjectRegistry>() is { } registry
            ? registry.KnownSubjects.Keys.OfType<AuthorizationSubject>().Take(2).ToList()
            : [];

    private async Task WaitUntilConnectedAsync(IInterceptorSubject root, IStorageContainer storage, CancellationToken cancellationToken)
    {
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = _context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                if (ReferenceEquals(change.Property.Subject, root) && change.Property.Name == nameof(IStorageContainer.Status))
                {
                    OnStatusChanged(storage.Status, connected);
                }
            });

        // After subscribing, so that a status set in between is not missed.
        OnStatusChanged(storage.Status, connected);
        await connected.Task.WaitAsync(cancellationToken);
    }

    private void OnStatusChanged(StorageStatus status, TaskCompletionSource connected)
    {
        if (status == StorageStatus.Connected)
        {
            connected.TrySetResult();
        }
        else if (status == StorageStatus.Error)
        {
            _logger.LogWarning("The root storage failed to load its files, so authentication stays unavailable until it loads them.");
        }
    }
}
