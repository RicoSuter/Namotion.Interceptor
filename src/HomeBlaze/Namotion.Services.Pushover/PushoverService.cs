using System.ComponentModel;
using System.Text;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Services.Pushover.Client;
using Namotion.Interceptor.Attributes;

namespace Namotion.Services.Pushover;

/// <summary>
/// A Pushover application that sends notifications to its targets.
/// </summary>
[Category("Notifications")]
[Description("Pushover push notifications to phones and desktops, one target per user or group")]
[DisplayName("Pushover")]
[SubjectIcon("NotificationsActive")]
[InterceptorSubject]
public partial class PushoverService :
    BackgroundService,
    IConfigurable,
    IMonitoredService,
    ITitleProvider,
    IIconProvider
{
    private readonly PushoverClient _client;
    private readonly ILogger<PushoverService> _logger;
    private readonly SemaphoreSlim _validationSignal = new(0, 1);

    [Configuration]
    public partial string Name { get; set; }

    /// <summary>
    /// The Pushover application API token.
    /// </summary>
    [Configuration(IsSecret = true)]
    public partial string? ApplicationToken { get; set; }

    /// <summary>
    /// The targets by a key that stays the same when the target is renamed. The dictionary is replaced as a whole,
    /// never changed in place.
    /// </summary>
    [Configuration]
    [State]
    public partial Dictionary<string, PushoverTarget> Targets { get; set; }

    [State(IsDiscrete = true)]
    public partial ServiceStatus Status { get; internal set; }

    [State]
    public partial string? StatusMessage { get; internal set; }

    [Derived]
    public string Title => string.IsNullOrEmpty(Name) ? "Pushover" : Name;

    public string IconName => "NotificationsActive";

    [Derived]
    public string? IconColor => Status switch
    {
        ServiceStatus.Running => "Success",
        ServiceStatus.Error => "Error",
        _ => null
    };

    // Test hook.
    internal TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(30);

    public PushoverService(IHttpClientFactory httpClientFactory, ILogger<PushoverService> logger)
    {
        _client = new PushoverClient(httpClientFactory);
        _logger = logger;

        Name = string.Empty;
        ApplicationToken = null;
        Targets = new Dictionary<string, PushoverTarget>();
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

    [Operation(Title = "Send to all targets", Icon = "Send", Position = 1)]
    public async Task SendToAllTargetsAsync(Notification notification, CancellationToken cancellationToken)
    {
        PushoverMessageMapping.Validate(notification);
        var targets = Targets;
        if (targets.Count == 0)
        {
            throw new InvalidOperationException("No Pushover targets are configured.");
        }

        // Sent to all at once and awaited together, so a failing target does not stop the others.
        var items = targets.Values.ToArray();
        var tasks = Array.ConvertAll(items, target => SendAsync(target, notification, cancellationToken));
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            var failures = items
                .Zip(tasks)
                .Where(pair => !pair.Second.IsCompletedSuccessfully)
                .Select(pair => (Target: pair.First, Exception: pair.Second.Exception?.InnerException ?? new TaskCanceledException(pair.Second)))
                .ToArray();

            var message = string.Join(" ", failures.Select(failure => $"{failure.Target.Title}: {failure.Exception.Message}"));
            throw new PushoverException(
                $"Sending to {failures.Length} of {items.Length} targets failed. {message}",
                new AggregateException(failures.Select(failure => failure.Exception)));
        }
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        RequestValidation();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates a key for a new target from its name: lowercase letters, digits and dashes, unique among the existing
    /// keys. The key is kept when the target is renamed, so paths to the target stay valid.
    /// </summary>
    public static string CreateTargetKey(string name, IReadOnlyCollection<string> existingKeys)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var key = builder.ToString().TrimEnd('-');
        if (key.Length == 0)
        {
            key = "target";
        }

        var uniqueKey = key;
        var suffix = 2;
        while (existingKeys.Contains(uniqueKey))
        {
            uniqueKey = $"{key}-{suffix}";
            suffix++;
        }

        return uniqueKey;
    }

    /// <summary>
    /// Sends <paramref name="notification"/> to <paramref name="target"/> and returns its id: the receipt of an
    /// emergency notification, otherwise the request id.
    /// </summary>
    internal async Task<string?> SendAsync(PushoverTarget target, Notification notification, CancellationToken cancellationToken)
    {
        PushoverMessageMapping.Validate(notification);

        // Read from the committed Targets, so a target that a reload replaced or removed cannot send anymore.
        if (!Targets.ContainsValue(target))
        {
            throw new InvalidOperationException($"The target '{target.Title}' is not part of a Pushover service.");
        }

        var token = GetRequiredApplicationToken();
        var userKey = target.UserKey;
        if (string.IsNullOrEmpty(userKey))
        {
            throw new InvalidOperationException($"The user key of the Pushover target '{target.Title}' is not configured.");
        }

        var fields = PushoverMessageMapping.Create(token, userKey, notification, target.EmergencyRetry, target.EmergencyExpire, _logger);
        var response = await _client.SendMessageAsync(fields, cancellationToken);

        target.LastSentAt = DateTimeOffset.UtcNow;
        return response.Receipt ?? response.Request;
    }

    private string GetRequiredApplicationToken() =>
        ApplicationToken is { Length: > 0 } token ? token : throw new InvalidOperationException("The Pushover application token is not configured.");

    internal void RequestValidation()
    {
        try
        {
            _validationSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A validation is already pending.
        }
    }

    // Set before Targets changes, so there is no window in which a target is reachable through Targets but cannot
    // send yet.
    partial void OnTargetsChanging(ref Dictionary<string, PushoverTarget> newValue, ref bool cancel)
    {
        newValue ??= new Dictionary<string, PushoverTarget>();
        foreach (var target in newValue.Values)
        {
            target.Service = this;
        }
    }

    partial void OnApplicationTokenChanged(string? newValue) => RequestValidation();

    partial void OnTargetsChanged(Dictionary<string, PushoverTarget> newValue)
    {
        RequestValidation();
    }
}
