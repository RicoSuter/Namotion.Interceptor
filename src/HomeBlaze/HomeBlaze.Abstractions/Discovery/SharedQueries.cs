using System.Collections.Concurrent;

namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// Runs each distinct question once and gives every caller that asks it the same answer.
/// </summary>
internal sealed class SharedQueries<TAnswer>
{
    private readonly Func<string, CancellationToken, Task<TAnswer>> _query;
    private readonly CancellationToken _scanCancellationToken;
    private readonly ConcurrentDictionary<string, Lazy<Task<TAnswer>>> _answers = new(StringComparer.OrdinalIgnoreCase);

    public SharedQueries(Func<string, CancellationToken, Task<TAnswer>> query, CancellationToken scanCancellationToken)
    {
        _query = query;
        _scanCancellationToken = scanCancellationToken;
    }

    public Task<TAnswer> GetAsync(string question, CancellationToken cancellationToken)
    {
        // The query runs with the token of the scan: a caller that stops waiting must not cancel it for the others.
        var answer = _answers.GetOrAdd(
            question,
            static (key, queries) => new Lazy<Task<TAnswer>>(() => queries._query(key, queries._scanCancellationToken)),
            this);

        return answer.Value.WaitAsync(cancellationToken);
    }
}
