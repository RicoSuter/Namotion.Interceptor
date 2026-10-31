namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// A clock that stands still and can run a callback inside its next reading on the thread that arms it, which lets a
/// test act while the game holds its lock.
/// </summary>
internal sealed class FrozenTimeProvider : TimeProvider
{
    private Action? _onNextTimestamp;
    private int _armedThreadId;

    public void OnNextTimestamp(Action action)
    {
        _armedThreadId = Environment.CurrentManagedThreadId;
        Volatile.Write(ref _onNextTimestamp, action);
    }

    public override long GetTimestamp()
    {
        if (Environment.CurrentManagedThreadId == Volatile.Read(ref _armedThreadId))
        {
            Interlocked.Exchange(ref _onNextTimestamp, null)?.Invoke();
        }

        return 0;
    }
}
