using MudBlazor;

namespace Namotion.Devices.Sonos.HomeBlaze;

internal static class SonosCommands
{
    /// <summary>
    /// Runs a command a widget started and shows its failure in a snackbar.
    /// </summary>
    internal static async Task RunAsync(ISnackbar snackbar, Func<Task> command)
    {
        try
        {
            await command();
        }
        catch (Exception exception)
        {
            snackbar.Add($"Sonos: {exception.Message}", Severity.Error);
        }
    }
}
