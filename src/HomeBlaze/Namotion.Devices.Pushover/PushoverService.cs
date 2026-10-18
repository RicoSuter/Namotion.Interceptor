using System.ComponentModel;
using System.Text;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Pushover.Client;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Pushover;

/// <summary>
/// A Pushover application that sends notifications to its targets. Only the targets are notification publishers, so
/// sending to all publishers reaches each recipient once.
/// </summary>
[Category("Notifications")]
[Description("Pushover push notifications to phones and desktops, one target per user or group")]
[DisplayName("Pushover")]
[SubjectIcon("NotificationsActive")]
[InterceptorSubject]
public partial class PushoverService : BackgroundService, IConfigurable, IMonitoredService, ITitleProvider, IIconProvider
{
    private readonly PushoverClient _client;
    private readonly ILogger<PushoverService> _logger;
    private readonly PushoverEmergencyTracker _emergencyTracker;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private volatile bool _isValidationRequested = true;

    [Configuration]
    public partial string Name { get; set; }

    /// <summary>
    /// The Pushover application API token.
    /// </summary>
    [Configuration(IsSecret = true)]
    public partial string? ApplicationToken { get; set; }

    /// <summary>
    /// The targets by a key that stays the same when the target is renamed.
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

    // Not intercepted, so tests can run faster than production timing: polling receipts below the five-second
    // minimum the receipts API allows, and without waiting out the retry delay after a failure.
    internal TimeSpan ReceiptPollingInterval { get; set; } = TimeSpan.FromSeconds(10);
    internal TimeSpan MinimumReceiptPollingInterval { get; set; } = TimeSpan.FromSeconds(5);
    internal TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(30);
    internal TimeSpan FinishedEmergencyRetention { get; set; } = TimeSpan.FromHours(24);

    // Not intercepted, for the same reason as Service on PushoverTarget: shared with the target through this
    // property instead, so a subscriber exception can always be logged.
    internal ILogger Logger => _logger;

    internal PushoverEmergencyTracker EmergencyTracker => _emergencyTracker;

    public PushoverService(IHttpClientFactory httpClientFactory, ILogger<PushoverService> logger)
    {
        _client = new PushoverClient(httpClientFactory);
        _logger = logger;
        _emergencyTracker = new PushoverEmergencyTracker(this);

        Name = string.Empty;
        ApplicationToken = null;
        Targets = new Dictionary<string, PushoverTarget>();
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

    [Operation(Title = "Send to all targets", Icon = "Send", Position = 1)]
    public async Task SendToAllTargetsAsync(Notification notification, CancellationToken cancellationToken)
    {
        ValidateNotification(notification);
        var targets = Targets.Values.ToArray();
        if (targets.Length == 0)
        {
            throw new InvalidOperationException("No Pushover targets are configured.");
        }

        await PushoverBatch.RunAllAsync(
            targets,
            target => SendAsync(target, notification, cancellationToken),
            target => target.Title,
            "Sending to",
            "targets",
            cancellationToken);
    }

    /// <summary>
    /// Stops the repeats of the active emergency notifications of every target. When some fail, the others are still
    /// canceled and the error names the failed ones.
    /// </summary>
    [Operation(Title = "Cancel all emergencies", Icon = "NotificationsOff", Position = 2)]
    public Task CancelAllEmergenciesAsync(CancellationToken cancellationToken) =>
        _emergencyTracker.CancelAllAsync(cancellationToken);

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
    /// emergency notification, which is then tracked under the key of the target, otherwise the request id.
    /// </summary>
    internal async Task<string?> SendAsync(PushoverTarget target, Notification notification, CancellationToken cancellationToken)
    {
        ValidateNotification(notification);

        var token = GetRequiredApplicationToken();
        var userKey = target.UserKey;
        if (string.IsNullOrEmpty(userKey))
        {
            throw new InvalidOperationException($"The user key of the Pushover target '{target.Title}' is not configured.");
        }

        var targetKey = target.Key ?? throw new InvalidOperationException($"The target '{target.Title}' is not part of a Pushover service.");
        var expire = target.EmergencyExpire;
        var request = PushoverMessageMapping.Create(token, userKey, notification, target.EmergencyRetry, expire, _logger);
        var response = await _client.SendMessageAsync(request, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        target.LastSentAt = now;
        if (response.Receipt is not { } receipt)
        {
            return response.Request;
        }

        _emergencyTracker.Add(targetKey, receipt, request.Title, request.Message, now, now + expire);
        Signal();
        return receipt;
    }

    private static void ValidateNotification(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Message);
        _ = PushoverMessageMapping.ToPushoverPriority(notification.Priority);
    }

    internal async Task CancelReceiptAsync(string receipt, CancellationToken cancellationToken)
    {
        var token = GetRequiredApplicationToken();
        await _client.CancelReceiptAsync(token, receipt, cancellationToken);
    }

    private string GetRequiredApplicationToken() =>
        ApplicationToken is { Length: > 0 } token ? token : throw new InvalidOperationException("The Pushover application token is not configured.");

    internal void RequestValidation()
    {
        _isValidationRequested = true;
        Signal();
    }

    private void Signal()
    {
        // Avoids a first-chance exception on every redundant signal. The catch below is a backstop for the race
        // between this check and the release.
        if (_signal.CurrentCount != 0)
        {
            return;
        }

        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A signal is already pending; the loop handles every request when it wakes.
        }
    }

    // The back-references are assigned here, not in OnTargetsChanged, so there is no window in which an attached
    // target is reachable through Targets but its Service, Key or Emergencies are not yet set.
    partial void OnTargetsChanging(ref Dictionary<string, PushoverTarget> newValue, ref bool cancel)
    {
        newValue ??= new Dictionary<string, PushoverTarget>();

        if (Targets is { } current)
        {
            foreach (var (key, previousTarget) in current)
            {
                if (!newValue.ContainsKey(key))
                {
                    previousTarget.Service = null;
                }
            }
        }

        foreach (var (key, target) in newValue)
        {
            target.Service = this;
            target.Key = key;
        }

        _emergencyTracker.SetTargets(newValue);
    }

    partial void OnTargetsChanged(Dictionary<string, PushoverTarget> newValue)
    {
        RequestValidation();
    }
}
