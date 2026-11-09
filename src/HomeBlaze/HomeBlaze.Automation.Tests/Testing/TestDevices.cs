using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Messaging;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Automation.Tests.Testing;

/// <summary>
/// Blinds with operations that succeed, fail, never end, wait for the test, ignore their cancellation, block their thread
/// or need confirmation.
/// </summary>
[InterceptorSubject]
public partial class TestBlinds
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stall = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _unblock = new();
    private volatile bool _isWaiting;
    private volatile bool _isStalled;
    private volatile bool _isBlocked;
    private volatile bool _isDisconnecting;
    private int _closeCount;
    private int _openCount;

    [State]
    public partial bool IsMoving { get; set; }

    [State]
    public partial bool IsLocked { get; set; }

    [State]
    public partial int Position { get; set; }

    [State]
    public partial int Level { get; internal set; }

    [Configuration]
    public partial string? Label { get; set; }

    public int CloseCount => Volatile.Read(ref _closeCount);

    public int OpenCount => Volatile.Read(ref _openCount);

    public bool IsWaiting => _isWaiting;

    public bool IsStalled => _isStalled;

    public bool IsBlocked => _isBlocked;

    public bool IsDisconnecting => _isDisconnecting;

    [Derived]
    [PropertyAttribute(nameof(Open), KnownAttributes.IsEnabled)]
    public bool Open_IsEnabled => !IsLocked;

    [Operation]
    public void Close() => Interlocked.Increment(ref _closeCount);

    [Operation]
    public void Open() => Interlocked.Increment(ref _openCount);

    [Operation(RequiresConfirmation = true)]
    public void Calibrate() => Position = 0;

    [Operation]
    public Task MoveToAsync(int position, CancellationToken cancellationToken)
    {
        Position = position;
        return Task.CompletedTask;
    }

    [Operation]
    public void Jam() => throw new InvalidOperationException("The motor is jammed.");

    [Operation]
    public Task HangAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

    [Operation]
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        _isWaiting = true;
        await _gate.Task.WaitAsync(cancellationToken);
        _isWaiting = false;
    }

    /// <summary>Runs until its cancellation, which it turns into another exception, as a client disposed on cancellation does.</summary>
    [Operation]
    public async Task DisconnectOnCancelAsync(CancellationToken cancellationToken)
    {
        _isDisconnecting = true;
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new ObjectDisposedException("connection");
        }
    }

    /// <summary>Ends only when the test calls <see cref="EndStall"/>, whatever its caller cancels.</summary>
    [Operation]
    public Task StallAsync()
    {
        _isStalled = true;
        return _stall.Task;
    }

    /// <summary>Blocks the thread of its caller until the test calls <see cref="Unblock"/>.</summary>
    [Operation]
    public void Block()
    {
        _isBlocked = true;
        _unblock.Wait();
        _isBlocked = false;
    }

    [Query]
    public int GetPosition() => Position;

    /// <summary>Ends every call of <c>Wait</c>.</summary>
    public void Release() => _gate.TrySetResult();

    public void EndStall() => _stall.TrySetResult();

    public void Unblock() => _unblock.Set();
}

/// <summary>A step whose call number <see cref="BlockOnCall"/> waits until <see cref="Release"/>.</summary>
[InterceptorSubject]
public partial class TestGate
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _isWaiting;
    private int _calls;

    public int BlockOnCall { get; init; }

    public bool IsWaiting => _isWaiting;

    [Operation]
    public async Task StepAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _calls) == BlockOnCall)
        {
            _isWaiting = true;
            await _gate.Task.WaitAsync(cancellationToken);
            _isWaiting = false;
        }
    }

    public void Release() => _gate.TrySetResult();
}

/// <summary>A notification channel that keeps what it was sent.</summary>
[InterceptorSubject]
public partial class TestNotifier : INotificationPublisher
{
    private readonly List<Notification> _sent = [];

    public IReadOnlyList<Notification> Sent
    {
        get
        {
            lock (_sent)
            {
                return [.. _sent];
            }
        }
    }

    public Task<NotificationResult> SendNotificationAsync(Notification notification, CancellationToken cancellationToken)
    {
        lock (_sent)
        {
            _sent.Add(notification);
        }

        return Task.FromResult(new NotificationResult());
    }
}
