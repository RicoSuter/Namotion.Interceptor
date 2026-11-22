namespace Namotion.Devices.Hyundai.Tests.Testing;

internal sealed class FakeVehicleCommands : IBluelinkVehicleCommands
{
    public bool CanSend { get; set; } = true;
    public List<string> Calls { get; } = [];

    public bool CanSendCommands(BluelinkProtocol protocol) => CanSend;

    public bool CanRefresh(decimal? auxiliaryBatteryLevel) => CanSend && auxiliaryBatteryLevel >= 0.65m;

    public Task RefreshAsync(BluelinkVehicle vehicle, CancellationToken cancellationToken) => Record("Refresh");

    public Task SetChargingAsync(BluelinkVehicle vehicle, bool start, CancellationToken cancellationToken) => Record($"Charging {start}");

    public Task SetChargeLimitsAsync(BluelinkVehicle vehicle, int acPercent, int dcPercent, CancellationToken cancellationToken) => Record($"Limits {acPercent} {dcPercent}");

    public Task StartClimateAsync(BluelinkVehicle vehicle, decimal temperature, bool defrost, CancellationToken cancellationToken) => Record($"Climate {temperature} {defrost}");

    public Task StopClimateAsync(BluelinkVehicle vehicle, CancellationToken cancellationToken) => Record("Climate stop");

    private Task Record(string call)
    {
        Calls.Add(call);
        return Task.CompletedTask;
    }
}
