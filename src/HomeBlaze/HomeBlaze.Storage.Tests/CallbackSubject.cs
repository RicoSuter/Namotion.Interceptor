using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Tests;

/// <summary>
/// A configurable subject whose next <see cref="ApplyConfigurationAsync"/> a test can replace, to call back
/// into the storage from code that runs on the storage worker.
/// </summary>
[InterceptorSubject]
public partial class CallbackSubject : IConfigurable
{
    private static Func<CallbackSubject, CancellationToken, Task>? _nextApply;

    [Configuration]
    public partial string Name { get; set; }

    public CallbackSubject()
    {
        Name = string.Empty;
    }

    /// <summary>
    /// Makes the next configuration that is applied to any instance run the callback.
    /// </summary>
    public static void RunOnNextApply(Func<CallbackSubject, CancellationToken, Task> callback)
        => Volatile.Write(ref _nextApply, callback);

    public static void Reset()
        => Volatile.Write(ref _nextApply, null);

    public static string Serialize(string name)
        => $$"""
            {
              "$type": "{{typeof(CallbackSubject).FullName}}",
              "name": "{{name}}"
            }
            """;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
        => Interlocked.Exchange(ref _nextApply, null) is { } callback
            ? callback(this, cancellationToken)
            : Task.CompletedTask;
}
