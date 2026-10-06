namespace HomeBlaze.Services;

/// <summary>
/// Holds the result of the latest started evaluation of a <c>[Derived]</c> getter, so that rendering can
/// read the value without invoking the getter again. Invoking the getter during rendering records a read
/// of every property it touches, for example every folder a path walk passes.
/// </summary>
/// <typeparam name="T">The type of the derived value.</typeparam>
/// <remarks>
/// The getter calls <see cref="BeginEvaluation"/> before it reads any state and passes the token to
/// <see cref="Store"/> with its result.
/// </remarks>
public sealed class LatestDerivedValue<T>
{
    private readonly Lock _lock = new();
    private long _startedEvaluations;
    private long _storedEvaluation;
    private T? _value;

    /// <summary>
    /// Starts an evaluation and returns the token to pass to <see cref="Store"/>.
    /// </summary>
    public long BeginEvaluation() => Interlocked.Increment(ref _startedEvaluations);

    /// <summary>
    /// Stores <paramref name="value"/> unless an evaluation that started later has already stored its result.
    /// </summary>
    /// <param name="evaluation">The token returned by <see cref="BeginEvaluation"/>.</param>
    /// <param name="value">The evaluated value.</param>
    /// <returns>The evaluated <paramref name="value"/>, also when it is not stored.</returns>
    public T Store(long evaluation, T value)
    {
        lock (_lock)
        {
            // Getter calls can overlap (a recalculation and another caller of the property), and the one
            // that started first may have read state from before the latest write.
            if (evaluation > _storedEvaluation)
            {
                _storedEvaluation = evaluation;
                _value = value;
            }
        }

        return value;
    }

    /// <summary>
    /// Gets the stored value.
    /// </summary>
    /// <param name="value">The stored value, or the default when no evaluation has completed yet.</param>
    /// <returns><c>true</c> when an evaluation has stored a value; otherwise <c>false</c>.</returns>
    public bool TryGetValue(out T? value)
    {
        lock (_lock)
        {
            value = _value;
            return _storedEvaluation > 0;
        }
    }
}
