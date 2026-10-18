namespace Namotion.Devices.Pushover;

internal static class PushoverBatch
{
    /// <summary>
    /// Runs <paramref name="run"/> for all <paramref name="items"/> at once and waits for every one, so a failure does
    /// not stop the others. When some fail, throws one <see cref="PushoverException"/> naming each failed item by
    /// <paramref name="describe"/>, for example "Sending to 1 of 2 targets failed. Anna: user key is invalid".
    /// </summary>
    public static async Task RunAllAsync<TItem>(
        TItem[] items, Func<TItem, Task> run, Func<TItem, string> describe, string action, string itemsName, CancellationToken cancellationToken)
    {
        var tasks = Array.ConvertAll(items, item => run(item));
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            var failures = items
                .Zip(tasks)
                .Where(pair => !pair.Second.IsCompletedSuccessfully)
                .Select(pair => (Item: pair.First, Exception: pair.Second.Exception?.InnerException ?? new TaskCanceledException(pair.Second)))
                .ToArray();

            var message = string.Join(" ", failures.Select(failure => $"{describe(failure.Item)}: {failure.Exception.Message}"));
            throw new PushoverException(
                $"{action} {failures.Length} of {items.Length} {itemsName} failed. {message}",
                new AggregateException(failures.Select(failure => failure.Exception)));
        }
    }
}
