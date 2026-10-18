namespace Namotion.Devices.Pushover;

internal static class PushoverBatch
{
    /// <summary>
    /// Runs <paramref name="run"/> for all <paramref name="targets"/> at once and waits for every one, so a failure
    /// does not stop the others. When some fail, throws one <see cref="PushoverException"/> naming each failed target,
    /// for example "Sending to 1 of 2 targets failed. Anna: user key is invalid".
    /// </summary>
    public static async Task RunForEachTargetAsync(
        Dictionary<string, PushoverTarget> targets, Func<string, PushoverTarget, Task> run, string action, CancellationToken cancellationToken)
    {
        var items = targets.ToArray();
        var tasks = Array.ConvertAll(items, item => run(item.Key, item.Value));
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            var failures = items
                .Zip(tasks)
                .Where(pair => !pair.Second.IsCompletedSuccessfully)
                .Select(pair => (Target: pair.First.Value, Exception: pair.Second.Exception?.InnerException ?? new TaskCanceledException(pair.Second)))
                .ToArray();

            var message = string.Join(" ", failures.Select(failure => $"{failure.Target.Title}: {failure.Exception.Message}"));
            throw new PushoverException(
                $"{action} {failures.Length} of {items.Length} targets failed. {message}",
                new AggregateException(failures.Select(failure => failure.Exception)));
        }
    }
}
