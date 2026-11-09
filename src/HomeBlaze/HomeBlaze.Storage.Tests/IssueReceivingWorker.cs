using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Tests;

/// <summary>
/// A hosted configurable subject that reads its configuration issues when its worker starts.
/// </summary>
[InterceptorSubject]
public partial class IssueReceivingWorker : BackgroundService, IConfigurable, IConfigurationIssueReceiver
{
    private string[] _issues = [];
    private readonly TaskCompletionSource<string[]> _issuesAtStart = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Configuration]
    public partial int Count { get; set; }

    /// <summary>The issues the subject had when its worker started.</summary>
    public Task<string[]> IssuesAtStart => _issuesAtStart.Task;

    public void SetConfigurationIssues(string[] issues) => Volatile.Write(ref _issues, issues);

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _issuesAtStart.TrySetResult(Volatile.Read(ref _issues));
        return Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
