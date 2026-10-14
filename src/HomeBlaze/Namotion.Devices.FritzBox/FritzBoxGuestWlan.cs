using HomeBlaze.Abstractions.Devices;
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
    public Task TurnOnAsync(CancellationToken cancellationToken) =>
        _router.SetGuestWlanEnabledAsync(this, true, cancellationToken);

    /// <inheritdoc />
    public Task TurnOffAsync(CancellationToken cancellationToken) =>
        _router.SetGuestWlanEnabledAsync(this, false, cancellationToken);
}
