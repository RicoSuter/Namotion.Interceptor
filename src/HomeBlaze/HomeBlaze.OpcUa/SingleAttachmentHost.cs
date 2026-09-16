using HomeBlaze.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace HomeBlaze.OpcUa;

/// <summary>
/// The subject a <see cref="SingleAttachmentHost{TService}"/> maintains: the state it reports, the
/// configuration it starts from and the instance it builds.
/// </summary>
/// <remarks>
/// Implement explicitly whatever the wrapper does not publish anyway, so hosting a service adds nothing
/// to what the subject shows a user. <see cref="Status"/>, <see cref="StatusMessage"/> and
/// <see cref="IsEnabled"/> are already the generated partial properties.
/// </remarks>
internal interface IAttachmentOwner<TService> : IInterceptorSubject
    where TService : class, IHostedService
{
    ServiceStatus Status { get; set; }

    /// <summary>The text behind <see cref="ServiceStatus.Error"/>, and null at every other status.</summary>
    string? StatusMessage { get; set; }

    /// <summary>Whether a start is wanted, read by the run loop and after a configuration edit.</summary>
    bool IsEnabled { get; }

    /// <summary>What the wrapper calls itself in its own log messages.</summary>
    string LogName { get; }

    /// <summary>The endpoint or path those messages name, so a host running several is readable.</summary>
    string LogTarget { get; }

    /// <summary>
    /// The message to report when the configuration cannot produce an instance, or null when it can.
    /// Read before anything the start path waits for, so an unconfigured wrapper reports at once rather
    /// than sitting in a wait whose result it cannot use.
    /// </summary>
    string? GetConfigurationError();

    /// <summary>
    /// Waits for whatever has to be in place before the attach, and returns false when the start must
    /// abandon, in which case the implementation has reported that outcome itself. Parking here cannot
    /// sit inside the handler's stop transition: the start path is never reached through StopAsync.
    /// </summary>
    ValueTask<bool> WaitUntilStartableAsync(CancellationToken cancellationToken) => new(true);

    /// <summary>
    /// Builds the instance. Invoked by the handler on every attach, so it reads the configuration each
    /// time rather than capturing a snapshot: a re-attach must produce a new instance, because the
    /// handler has already disposed the previous one.
    /// </summary>
    TService CreateInstance();

    /// <summary>Publishes the running instance's diagnostics.</summary>
    void ApplyDiagnostics(TService instance);

    /// <summary>Clears those diagnostics, which read null whenever nothing is running.</summary>
    void ResetDiagnostics();

    /// <summary>
    /// Drops what the instance published into the subject beyond its diagnostics, for a wrapper that
    /// publishes anything else. Called wherever the attachment stops holding an instance whose output
    /// can still be trusted, and deliberately never from the unwind, which runs while it is still live.
    /// </summary>
    void DropInstanceState()
    {
    }
}

/// <summary>
/// The one hosted service a wrapper subject owns: the attachment itself, the gate that serializes
/// everything touching it, the start and stop paths, the diagnostics poll and the unwind.
/// </summary>
internal sealed class SingleAttachmentHost<TService>
    where TService : class, IHostedService
{
    private const string NotAttachedMessage = "Not attached to a running host, so nothing was started";

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(10);

    private readonly IAttachmentOwner<TService> _owner;
    private readonly ILogger _logger;
    private readonly TimeSpan _pollInterval;

    /// <summary>
    /// Serializes the start path, the stop path and the diagnostics poll against each other, and is
    /// what publishes <see cref="_attachment"/> between the threads that touch it. Held across the
    /// whole of each path, because the guard on the attachment spans the attach's own await. Never
    /// waited for from the subject's own StopAsync or from the unwind in <see cref="RunAsync"/>, so it
    /// can never park inside the handler's stop transition for that subject.
    /// </summary>
    private readonly SemaphoreSlim _attachmentGate = new(1, 1);

    /// <summary>
    /// Orders a status commit against a stop. The gate cannot do this: the unwind writes Stopped without
    /// taking it, deliberately, because taking it there would wait on the very transition the unwind runs
    /// inside. Held across the intercepted writes only, never across an await or a graph operation, and
    /// nothing that holds a graph lock reaches these methods, so it is a leaf.
    /// </summary>
    private readonly Lock _statusLock = new();

    /// <summary>
    /// Bumped by every stop report. A reconciliation captures it before it reads anything and each commit
    /// re-checks it, so a stop is detected even when a later writer has already moved the status off
    /// Stopped. Reading the status alone would not: any writer can clear that, this only ever advances.
    /// </summary>
    private int _stopGeneration;


    /// <summary>
    /// The single attachment the wrapper owns, or null when nothing is attached. Read and written only
    /// under <see cref="_attachmentGate"/>. Each attach builds its own instance, so a second one leaves
    /// one of them unreachable from here and therefore impossible to stop from here.
    /// </summary>
    private IHostedServiceAttachment<TService>? _attachment;

    /// <remarks>
    /// <paramref name="pollInterval"/> is null for the default, and injectable because a suite that
    /// runs in seconds cannot reach the reconciled states on the production interval.
    /// </remarks>
    public SingleAttachmentHost(IAttachmentOwner<TService> owner, ILogger logger, TimeSpan? pollInterval = null)
    {
        _owner = owner;
        _logger = logger;
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    /// <summary>
    /// The wrapper's own background loop: the start it wants on startup, the diagnostics poll, and the
    /// unwind that runs when the subject leaves the graph or the host shuts down.
    /// </summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (_owner.IsEnabled)
        {
            await StartAsync(stoppingToken);
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TryUpdateFromAttachment();
                await Task.Delay(_pollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        // Deliberately does NOT detach, and deliberately does not take the gate: this unwind runs inside
        // the handler's own stop transition for this subject, and either one would wait on that
        // transition. See docs/hosting.md#do-not-detach-from-your-own-stop-path. What the instance
        // published is left alone for the same reason, and UpdateFromAttachment drops it instead.
        ReportStopped();
    }

    /// <summary>
    /// Applies a configuration edit by stopping what is attached and starting it again from the edited
    /// configuration.
    /// </summary>
    public async Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken);

        // Guarded here rather than left to the run loop's caller-side check: without it an edit that
        // disables the wrapper stops it and starts it again in the same call.
        if (_owner.IsEnabled)
        {
            await StartAsync(cancellationToken);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!await TryEnterAttachmentGateAsync(cancellationToken))
        {
            return;
        }

        // Captured at the gate rather than deeper in, and outside the try so the catch commits against
        // this same reading: every status this path writes has to lose to a stop that lands while it
        // runs, and writing Starting first would otherwise put the wrapper back on a status that reads
        // as not stopped. A capture taken at the moment of a commit would compare the generation with
        // itself and gate nothing.
        var generation = CaptureStopGeneration();

        try
        {
            if (!TryCommitActive(generation, ServiceStatus.Starting, message: null))
            {
                return;
            }

            if (_owner.GetConfigurationError() is { } configurationError)
            {
                TryCommitActive(generation, ServiceStatus.Error, configurationError);
                return;
            }

            if (!await _owner.WaitUntilStartableAsync(cancellationToken))
            {
                return;
            }

            // The attachment survives a context detach, so on re-attach the handler re-invokes the
            // factory itself. Without this guard a restarted run loop would attach a second instance
            // alongside the one the handler just re-created.
            if (_attachment is null)
            {
                // CancellationToken.None rather than the caller's token: the returned handle is the
                // only record of the attachment and the transition runs to completion whatever the
                // token does, so a cancelled wait strands a live attachment with nothing pointing at
                // it and lets the next start attach a second instance. Bounded: the instance is a
                // BackgroundService whose StartAsync returns at its first await, and a start appended
                // during shutdown returns without creating anything.
                var attachment = await _owner.AttachHostedServiceAsync(_owner.CreateInstance, CancellationToken.None);
                if (attachment.Current is null)
                {
                    // No instance means nothing was started and nothing will be before a context
                    // re-attach: the awaited overload appends nothing without a handler, outside the
                    // graph or while draining, and throws rather than returning when a start faulted.
                    // Dropped rather than kept, which would report Starting forever.
                    _owner.DetachHostedService(attachment);

                    TryCommitActive(generation, ServiceStatus.Error, NotAttachedMessage);
                    _logger.LogWarning(
                        "{Service} for {Target} was not started: the subject is not attached to a running host.",
                        _owner.LogName, _owner.LogTarget);
                    return;
                }

                _attachment = attachment;
            }

            UpdateFromAttachment(generation);
            _logger.LogInformation("{Service} started for {Target}", _owner.LogName, _owner.LogTarget);
        }
        catch (Exception exception)
        {
            // No OperationCanceledException filter: the only wait on the caller's token in this try is
            // the startable wait, which reports its own cancellation and returns instead of throwing,
            // so a filter could only swallow a genuine start failure and report it as a clean stop.
            TryCommitActive(generation, ServiceStatus.Error, exception.Message);
            _logger.LogError(exception, "Failed to start {Service} for {Target}", _owner.LogName, _owner.LogTarget);
        }
        finally
        {
            _attachmentGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!await TryEnterAttachmentGateAsync(cancellationToken))
        {
            return;
        }

        try
        {
            if (_attachment is not { } attachment)
            {
                // A wrapper whose start failed sits at Error with no attachment, and a stop is the only
                // thing that takes it out of there. Dropping what that start published is safe here
                // because nothing is attached.
                _owner.DropInstanceState();
                ReportStopped();
                return;
            }

            lock (_statusLock)
            {
                // Under the lock but not generation guarded: this is itself a stop, so it never loses to
                // one. Paired so the unwind cannot land between the two writes and leave Stopping on top
                // of the Stopped it just reported.
                //
                // Cleared ahead of the status: a wrapper that faulted with its attachment still held
                // arrives here from Error, and the text stands under Error alone.
                _owner.StatusMessage = null;
                _owner.Status = ServiceStatus.Stopping;
            }

            try
            {
                // CancellationToken.None, symmetrically with the attach: the detach removes the
                // attachment before it stops anything, so a cancelled wait returns while the instance
                // is still stopping with nothing left pointing at it. Bounded by the stop the handler
                // runs.
                await _owner.DetachHostedServiceAsync(attachment, CancellationToken.None);
                _logger.LogInformation("{Service} stopped", _owner.LogName);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to stop {Service}", _owner.LogName);
            }

            // Cleared from the subject's own attachment set rather than from the detach having
            // returned: the field must never read null while an attachment is still live, or the guard
            // in the start path attaches a second instance over it.
            if (!_owner.GetHostedServiceAttachments().Contains(attachment))
            {
                _attachment = null;
                _owner.DropInstanceState();
            }

            ReportStopped();
        }
        finally
        {
            _attachmentGate.Release();
        }
    }

    /// <summary>
    /// Reports a stop. <see cref="IAttachmentOwner{TService}.DropInstanceState"/> is deliberately not
    /// part of it: each caller decides that for itself.
    /// </summary>
    private void ReportStopped()
    {
        lock (_statusLock)
        {
            _stopGeneration++;

            // Cleared first: both callers reach this from Error, and the text stands under Error alone.
            _owner.StatusMessage = null;
            _owner.Status = ServiceStatus.Stopped;
            _owner.ResetDiagnostics();
        }
    }

    /// <summary>
    /// Polls the attachment from the run loop. Skips the round rather than waiting when a start or a
    /// stop holds the gate: that caller reconciles the state itself before it releases, and a poll that
    /// waited here would sit in the way of the shutdown that cancels it. Ungated, a preempted poll
    /// resumes after a completed stop and writes Running over Stopped for good.
    /// </summary>
    private void TryUpdateFromAttachment()
    {
        if (!_attachmentGate.Wait(0))
        {
            return;
        }

        try
        {
            UpdateFromAttachment(CaptureStopGeneration());
        }
        finally
        {
            _attachmentGate.Release();
        }
    }

    /// <summary>
    /// Reconciles the reported status and the diagnostics with what the attachment actually holds. The
    /// handler creates, faults and disposes the instance on its own chain, so polling the handle is the
    /// only way those outcomes reach the UI. Must be called with <see cref="_attachmentGate"/> held.
    /// </summary>
    private void UpdateFromAttachment(int generation)
    {
        if (_attachment is not { } attachment || _owner.Status is ServiceStatus.Stopping or ServiceStatus.Stopped)
        {
            return;
        }


        // Read below the fault, not above it: a retry start clears the fault before it enters its start
        // window, so a reading taken above would still be settled for a fault this poll is about to act
        // on. Each drop takes its own reading immediately before dropping, so nothing but the branch
        // sits between the reading and the act it decides.
        var fault = attachment.Fault;

        if (fault is not null)
        {
            // Re-checked inside the lock rather than trusting the guard this round read on entry: the
            // two are many statements apart, several of them full interceptor chain passes, and a stop
            // landing between them would otherwise be overwritten for good.
            if (!TryCommitActive(generation, ServiceStatus.Error, fault.Message, _owner.ResetDiagnostics))
            {
                return;
            }

            // One reading, for the reason the not running branch below gives. Current is read here too,
            // because a fault says nothing about whether a later start has already produced an instance.
            if (attachment.GetState(out var faultedCurrent) is not HostedServiceAttachmentState.Starting &&
                faultedCurrent is null)
            {
                _owner.DropInstanceState();
            }

            return;
        }

        // One reading, so the state and the instance cannot disagree: a start in flight may already have
        // published its tree, and dropping it here would take that tree back out of the graph underneath
        // the factory. Reading them separately is what let a stale state stand beside a fresh instance.
        var state = attachment.GetState(out var instance);

        if (instance is null)
        {
            // Attached but not yet created: the start transition has not run, or it has just disposed
            // the previous instance on a re-attach.
            if (state is not HostedServiceAttachmentState.Starting)
            {
                _owner.DropInstanceState();
            }

            TryCommitActive(generation, ServiceStatus.Starting, message: null);
            return;
        }

        TryCommitActive(generation, ServiceStatus.Running, message: null, () => _owner.ApplyDiagnostics(instance));
    }

    /// <summary>Reads the stop generation an operation must commit against.</summary>
    private int CaptureStopGeneration()
    {
        lock (_statusLock)
        {
            return _stopGeneration;
        }
    }

    /// <summary>
    /// Writes a status that claims the wrapper is doing something, unless a stop has landed since
    /// <paramref name="generation"/> was captured, and reports whether it wrote.
    /// </summary>
    /// <remarks>
    /// The check and the write are one step because the unwind reports Stopped without the gate every
    /// other writer holds, so ordering against it cannot come from the gate. The generation is what that
    /// check reads, rather than the status itself: any writer can move the status off Stopped, and one
    /// that did would make a later reading of it say the wrapper is running again.
    /// </remarks>
    private bool TryCommitActive(int generation, ServiceStatus status, string? message, Action? afterCommit = null)
    {
        lock (_statusLock)
        {
            if (_stopGeneration != generation)
            {
                return false;
            }

            if (status is ServiceStatus.Error)
            {
                // Status first when moving to Error, message first when moving away from it: the text
                // stands under Error alone and must never be readable beside a status that lacks it.
                _owner.Status = status;
                _owner.StatusMessage = message;
            }
            else
            {
                _owner.StatusMessage = message;
                _owner.Status = status;
            }

            afterCommit?.Invoke();
            return true;
        }
    }



    /// <summary>
    /// Waits for the attachment gate. Returns false when the wait was cancelled, in which case the
    /// caller holds nothing, must not release it and must report nothing. The caller's token is honoured
    /// so the wait can never stand in the way of the stop that cancels it.
    /// </summary>
    private async Task<bool> TryEnterAttachmentGateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _attachmentGate.WaitAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
