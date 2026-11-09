using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Services.Clients;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Installation;

/// <summary>
/// A browser connected to the installation: one tab while it is anonymous, every tab of the browser once it is named.
/// It shows a notification as a dialog in its open tabs.
/// </summary>
[Description("Browser connected to this installation, which shows notifications as a dialog")]
[DisplayName("Client")]
[SubjectIcon("Devices")]
[ExcludeFromCreation]
[InterceptorSubject]
public partial class HomeBlazeClient :
    IConfigurable,
    INotificationPublisher,
    ITitleProvider,
    IIconProvider
{
    private readonly Lock _tokenLock = new();

    // The raw token is never persisted, so only the process that issued it can hand it to the open tabs of the client.
    private volatile IssuedToken? _issuedToken;

    // Not intercepted: an intercepted property holding the installation would attach it as a child of its own child.
    internal HomeBlazeInstallation? Installation { get; set; }

    private HomeBlazeInstallation RequiredInstallation =>
        Installation ?? throw new InvalidOperationException($"The client '{Title}' is not part of an installation.");

    /// <summary>
    /// The name of the client, empty while it is anonymous.
    /// </summary>
    [Configuration]
    public partial string Name { get; set; }

    /// <summary>
    /// The SHA-256 hash of the secret token that the browsers of the named client store, or null while it has none.
    /// </summary>
    [Configuration(IsSecret = true)]
    public partial string? TokenHash { get; set; }

    [State(Position = 1)]
    public partial string Id { get; internal set; }

    [Derived]
    [State(IsDiscrete = true, Position = 2)]
    public bool IsNamed => !string.IsNullOrEmpty(Name);

    [State(IsDiscrete = true, Position = 3)]
    public partial bool IsConnected { get; internal set; }

    /// <summary>
    /// The open tabs of the client.
    /// </summary>
    [State(Position = 4)]
    public partial int ConnectionCount { get; internal set; }

    [State(Position = 5)]
    public partial DateTimeOffset? LastConnected { get; internal set; }

    [Derived]
    public string? Title => IsNamed ? Name : $"Anonymous client ({Id[..Math.Min(4, Id.Length)]}…)";

    public string IconName => "Devices";

    [Derived]
    public string? IconColor => IsConnected ? "Success" : null;

    public HomeBlazeClient()
    {
        Name = string.Empty;
        TokenHash = null;
        Id = string.Empty;
        IsConnected = false;
        ConnectionCount = 0;
        LastConnected = null;
    }

    public Task<NotificationResult> SendNotificationAsync(Notification notification, CancellationToken cancellationToken) =>
        RequiredInstallation.SendNotificationAsync(this, notification, cancellationToken);

    [Operation(Title = "Rename", Icon = "Edit", Position = 1, Description = "Names this client, which keeps it when its browser closes.")]
    public async Task RenameAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var installation = RequiredInstallation;
        installation.ThrowIfNotPartOfInstallation(this);
        Name = name.Trim();
        await installation.WriteClientConfigurationAsync(cancellationToken);
    }

    [Operation(Title = "Forget", Icon = "Delete", Position = 2, RequiresConfirmation = true,
        Description = "Removes the name of this client; its open tabs become anonymous clients again.")]
    public async Task ForgetAsync(CancellationToken cancellationToken)
    {
        var installation = RequiredInstallation;
        installation.ThrowIfNotPartOfInstallation(this);
        Name = string.Empty;
        await installation.WriteClientConfigurationAsync(cancellationToken);
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        Installation?.ReconcileClients();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the token of the client while this instance issued the token of <paramref name="tokenHash"/>, otherwise null.
    /// </summary>
    internal string? GetIssuedToken(string tokenHash) =>
        _issuedToken is { } issued && issued.Hash == tokenHash ? issued.Token : null;

    // Also a name set in the configuration editor reaches the tabs of the client.
    partial void OnNameChanged(string newValue)
    {
        var installation = Installation;
        if (installation is null)
        {
            // Deserialization sets the name before the token hash and before the client joins an installation.
            return;
        }

        UpdateToken();
        installation.ReconcileClients();
    }

    private void UpdateToken()
    {
        // Reads the current name rather than the changed value, so concurrent renames settle on the last name.
        lock (_tokenLock)
        {
            if (!IsNamed)
            {
                _issuedToken = null;
                TokenHash = null;
            }
            else if (TokenHash is null)
            {
                var token = ClientConnectionService.CreateClientToken();
                var issued = new IssuedToken(token, ClientConnectionService.HashClientToken(token));
                _issuedToken = issued;
                TokenHash = issued.Hash;
            }
        }
    }

    private sealed class IssuedToken(string token, string hash)
    {
        public string Token { get; } = token;

        public string Hash { get; } = hash;
    }
}
