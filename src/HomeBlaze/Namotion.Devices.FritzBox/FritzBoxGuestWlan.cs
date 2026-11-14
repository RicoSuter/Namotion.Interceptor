using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices;
using Namotion.Devices.FritzBox.Client;
using Namotion.Devices.Upnp;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.FritzBox;

/// <summary>
/// The guest WLAN, the only WLAN that can be switched on and off: switching a main band off can cut off the
/// connection this host itself uses. Switching it on opens the network to guests, so turning it on and toggling it
/// require confirmation in the operations panel and cannot be called by agents or over MCP; turning it off does not.
/// The switch of the dashboard widget calls them directly, as an action of the operator.
/// </summary>
[InterceptorSubject]
public partial class FritzBoxGuestWlan : FritzBoxWlan, ISwitchController
{
    // A field, not a property: a subject reference in a property would attach the router under its own WLAN.
    private readonly FritzBox _router;

    public FritzBoxGuestWlan(FritzBox router, string serviceType)
        : base(FritzBoxWlanRoles.Guest, serviceType)
    {
        _router = router;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The router is not connected.</exception>
    /// <exception cref="HttpRequestException">The request to the router failed.</exception>
    /// <exception cref="TimeoutException">The router did not answer in time.</exception>
    /// <exception cref="UpnpFaultException">The router refused the command.</exception>
    /// <exception cref="FritzBoxCertificateException">The router presented a key other than the trusted one.</exception>
    [Operation(RequiresConfirmation = true)]
    public Task TurnOnAsync(CancellationToken cancellationToken) =>
        _router.SetGuestWlanEnabledAsync(this, true, cancellationToken);

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The router is not connected.</exception>
    /// <exception cref="HttpRequestException">The request to the router failed.</exception>
    /// <exception cref="TimeoutException">The router did not answer in time.</exception>
    /// <exception cref="UpnpFaultException">The router refused the command.</exception>
    /// <exception cref="FritzBoxCertificateException">The router presented a key other than the trusted one.</exception>
    public Task TurnOffAsync(CancellationToken cancellationToken) =>
        _router.SetGuestWlanEnabledAsync(this, false, cancellationToken);

    /// <summary>Toggles the guest WLAN; does nothing while its state is unknown.</summary>
    /// <exception cref="InvalidOperationException">The router is not connected.</exception>
    /// <exception cref="HttpRequestException">The request to the router failed.</exception>
    /// <exception cref="TimeoutException">The router did not answer in time.</exception>
    /// <exception cref="UpnpFaultException">The router refused the command.</exception>
    /// <exception cref="FritzBoxCertificateException">The router presented a key other than the trusted one.</exception>
    [Operation(RequiresConfirmation = true)]
    public Task ToggleAsync(CancellationToken cancellationToken) => IsOn switch
    {
        false => TurnOnAsync(cancellationToken),
        true => TurnOffAsync(cancellationToken),
        null => Task.CompletedTask
    };
}
