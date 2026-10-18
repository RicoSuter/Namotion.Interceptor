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
    IObservable<PushoverEmergencyAcknowledgedEvent>,
    IMonitoredService,
    ITitleProvider,
    IIconProvider
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

    // Test hooks.
    internal TimeSpan ReceiptPollingInterval { get; set; } = TimeSpan.FromSeconds(10);
    internal TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(30);
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    internal ILogger<PushoverService> Logger => _logger;

    internal PushoverEmergencyTracker EmergencyTracker => _emergencyTracker;

    public PushoverService(IHttpClientFactory httpClientFactory, ILogger<PushoverService> logger)
    {
        _client = new PushoverClient(httpClientFactory);
        _logger = logger;
        _emergencyTracker = new PushoverEmergencyTracker(this, _client);

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

        await PushoverBatch.RunForEachTargetAsync(
            targets, (key, target) => SendAsync(target, key, notification, cancellationToken), "Sending to", cancellationToken);
    }

    /// <summary>
    /// Stops the repeats of the emergency notifications of every target by their tags, also those sent before a
    /// restart. When some targets fail, the others are still canceled and the error names the failed ones.
    /// </summary>
    [Operation(Title = "Cancel all emergencies", Icon = "NotificationsOff", Position = 2, RequiresConfirmation = true)]
    public Task CancelAllEmergenciesAsync(CancellationToken cancellationToken) =>
        PushoverBatch.RunForEachTargetAsync(
            Targets, (key, _) => _emergencyTracker.CancelAllAsync(key, cancellationToken), "Canceling the emergencies of", cancellationToken);

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
    /// emergency notification, which is then tracked under <paramref name="targetKey"/>, otherwise the request id.
    /// </summary>
    internal async Task<string?> SendAsync(PushoverTarget target, string targetKey, Notification notification, CancellationToken cancellationToken)
    {
        PushoverMessageMapping.Validate(notification);

        var token = GetRequiredApplicationToken();
        var userKey = target.UserKey;
        if (string.IsNullOrEmpty(userKey))
        {
            throw new InvalidOperationException($"The user key of the Pushover target '{target.Title}' is not configured.");
        }

        var isEmergency = notification.Priority == NotificationPriority.Emergency;
        if (isEmergency)
        {
            _emergencyTracker.ReserveEmergency(targetKey, target.MaximumActiveEmergencies, target.Title);
        }

        try
        {
            var expire = target.EmergencyExpire;
            var fields = PushoverMessageMapping.Create(token, userKey, notification, target.EmergencyRetry, expire, targetKey, _logger);

            // Not canceled by the caller once sending an emergency started, so one that Pushover accepted is always
            // tracked and can be canceled; the HttpClient timeout still bounds the request.
            var response = await _client.SendMessageAsync(fields, isEmergency ? CancellationToken.None : cancellationToken);

            var now = TimeProvider.GetUtcNow();
            target.LastSentAt = now;
            if (response.Receipt is not { } receipt)
            {
                return response.Request;
            }

            _emergencyTracker.Add(targetKey, receipt, fields.GetValueOrDefault("title"), fields["message"], now, expire);
            Signal();
            return receipt;
        }
        finally
        {
            if (isEmergency)
            {
                _emergencyTracker.ReleaseReservation(targetKey);
            }
        }
    }

    /// <summary>
    /// Emits one event per acknowledged emergency notification of any target.
    /// </summary>
    public IDisposable Subscribe(IObserver<PushoverEmergencyAcknowledgedEvent> observer) =>
        _emergencyTracker.Subscribe(observer, targetKey: null);

    internal string GetRequiredApplicationToken() =>
        ApplicationToken is { Length: > 0 } token ? token : throw new InvalidOperationException("The Pushover application token is not configured.");

    internal void RequestValidation()
    {
        _isValidationRequested = true;
        Signal();
    }

    private void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A signal is already pending.
        }
    }

    // Assigned before Targets changes, so there is no window in which a target is reachable through Targets but its
    // service or emergencies are not yet set.
    partial void OnTargetsChanging(ref Dictionary<string, PushoverTarget> newValue, ref bool cancel)
    {
        newValue ??= new Dictionary<string, PushoverTarget>();
        _emergencyTracker.SetTargets(newValue);
    }

    partial void OnTargetsChanged(Dictionary<string, PushoverTarget> newValue)
    {
        RequestValidation();
    }
}
