using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

/// <summary>
/// Test output for a component shared by tests that run in parallel, such as the shared server.
/// Keeps the most recent lines so each test can write out the ones logged while it ran.
/// </summary>
public sealed class SharedLogBuffer : ITestOutputHelper
{
    private const int Capacity = 2000;

    private readonly Lock _lock = new();
    private readonly Queue<string> _lines = new();
    private long _position;

    /// <summary>
    /// Gets the number of lines written so far, to pass to <see cref="ReadFrom"/> later.
    /// </summary>
    public long Position
    {
        get
        {
            lock (_lock)
            {
                return _position;
            }
        }
    }

    public void WriteLine(string message)
    {
        lock (_lock)
        {
            _lines.Enqueue(message);
            _position++;
            if (_lines.Count > Capacity)
            {
                _lines.Dequeue();
            }
        }
    }

    public void WriteLine(string format, params object[] args) => WriteLine(string.Format(format, args));

    /// <summary>
    /// Returns the retained lines written since <paramref name="position"/>.
    /// </summary>
    public string[] ReadFrom(long position)
    {
        lock (_lock)
        {
            var firstRetainedPosition = _position - _lines.Count;
            var skip = (int)Math.Max(0, position - firstRetainedPosition);
            return _lines.Skip(skip).ToArray();
        }
    }
}
