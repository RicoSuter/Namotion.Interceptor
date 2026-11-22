using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Hyundai.Tests.Testing;

/// <summary>An account stand-in with an intercepted flag, to prove that the enablement of operations follows the account.</summary>
[InterceptorSubject]
internal partial class TrackedVehicleCommands : IBluelinkVehicleCommands
{
    public TrackedVehicleCommands()
    {
        CanSend = true;
    }

    public partial bool CanSend { get; set; }

    public bool CanSendCommands(BluelinkProtocol protocol) => CanSend;

    public bool CanRefresh(decimal? auxiliaryBatteryLevel) => CanSend;

    public Task RefreshAsync(BluelinkVehicle vehicle, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SetChargingAsync(BluelinkVehicle vehicle, bool start, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SetChargeLimitsAsync(BluelinkVehicle vehicle, int acPercent, int dcPercent, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartClimateAsync(BluelinkVehicle vehicle, decimal temperature, bool defrost, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopClimateAsync(BluelinkVehicle vehicle, CancellationToken cancellationToken) => Task.CompletedTask;
}
