using HomeBlaze.Abstractions.Devices;
using Namotion.Devices.FritzBox.Client;
using Namotion.Devices.Upnp;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.FritzBox;

/// <summary>
/// The guest WLAN, the only WLAN that can be switched on and off: switching a main band off can cut off the
/// connection this host itself uses.
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
}
