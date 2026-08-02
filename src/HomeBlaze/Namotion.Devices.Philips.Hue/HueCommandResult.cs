using HueApi.Models;

namespace Namotion.Devices.Philips.Hue;

/// <summary>
/// Surfaces a command the bridge rejected.
///
/// The SDK does not throw for a semantic rejection: an unreachable bulb, an out-of-range parameter or
/// a light that is not powered all come back as a normally returned response carrying an error list.
/// Left unread, the operation returns as though it had worked, so the caller reports success for a
/// command that never took effect.
/// </summary>
internal static class HueCommandResult
{
    public static void ThrowOnError(HuePutResponse response)
    {
        if (response.Errors.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "The Hue bridge rejected the command: " +
            string.Join("; ", response.Errors.Select(error => error.Description)));
    }
}
