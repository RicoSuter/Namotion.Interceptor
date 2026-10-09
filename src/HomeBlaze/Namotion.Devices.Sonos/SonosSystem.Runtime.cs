namespace Namotion.Devices.Sonos;

public partial class SonosSystem
{
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Delay(Timeout.Infinite, stoppingToken);
}
