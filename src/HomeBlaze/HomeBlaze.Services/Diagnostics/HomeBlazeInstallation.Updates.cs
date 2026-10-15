namespace HomeBlaze.Services.Diagnostics;

public partial class HomeBlazeInstallation
{
    private readonly SemaphoreSlim _updateSignal = new(0, 1);

    private string? GetUpdateCheckUnavailableReason() => "Not available for development builds";

    private Task RunUpdateChecksAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
