using System.Collections.Concurrent;

namespace Namotion.Devices.Sonos;

/// <summary>
/// Tracks which failures persist, so a failure that repeats at every retry is reported once, when it starts.
/// Thread-safe.
/// </summary>
internal sealed class FailureTracker
{
    private readonly ConcurrentDictionary<string, string> _failures = new(StringComparer.Ordinal);

    /// <summary>
    /// Records a failure of <paramref name="key"/> and returns whether it is new: the key was not failing, or it
    /// failed with another message.
    /// </summary>
    internal bool ReportFailure(string key, string message = "")
    {
        while (true)
        {
            if (_failures.TryAdd(key, message))
            {
                return true;
            }

            if (_failures.TryGetValue(key, out var previous))
            {
                if (previous == message)
                {
                    return false;
                }

                if (_failures.TryUpdate(key, message, previous))
                {
                    return true;
                }
            }
        }
    }

    /// <summary>
    /// Records a success of <paramref name="key"/>, so its next failure is new again.
    /// </summary>
    internal void ReportSuccess(string key)
    {
        // The lock-free lookup first: a success is the common case, and removing takes a lock.
        if (_failures.ContainsKey(key))
        {
            _failures.TryRemove(key, out _);
        }
    }
}
