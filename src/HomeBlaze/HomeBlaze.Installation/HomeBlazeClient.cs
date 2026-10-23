using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Messaging;
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
    // Not intercepted: an intercepted property holding the installation would attach it as a child of its own child.
    internal HomeBlazeInstallation? Installation { get; set; }

    private HomeBlazeInstallation RequiredInstallation =>
        Installation ?? throw new InvalidOperationException($"The client '{Title}' is not part of an installation.");

    /// <summary>
    /// The name of the client, empty while it is anonymous.
    /// </summary>
    [Configuration]
    public partial string Name { get; set; }

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
        Name = name.Trim();
        await installation.WriteClientConfigurationAsync(cancellationToken);
    }

    [Operation(Title = "Forget", Icon = "Delete", Position = 2, RequiresConfirmation = true,
        Description = "Removes the name of this client; its open tabs become anonymous clients again.")]
    public async Task ForgetAsync(CancellationToken cancellationToken)
    {
        var installation = RequiredInstallation;
        Name = string.Empty;
        await installation.WriteClientConfigurationAsync(cancellationToken);
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        Installation?.ReconcileClients();
        return Task.CompletedTask;
    }

    // Also a name set in the configuration editor reaches the tabs of the client.
    partial void OnNameChanged(string newValue) => Installation?.ReconcileClients();
}
