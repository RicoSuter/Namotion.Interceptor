using System.Threading.Channels;
using Namotion.Interceptor.Testing;

namespace Namotion.Matter.Tests.Server;

/// <summary>Waits on a channel bounded by <see cref="TestTimeouts.Default"/>, so a broken test fails instead of hanging.</summary>
public static class ChannelReaderTestExtensions
{
    public static async Task<T> ReadWithTimeoutAsync<T>(this ChannelReader<T> reader)
    {
        using var timeout = new CancellationTokenSource(TestTimeouts.Default);
        return await reader.ReadAsync(timeout.Token);
    }

    public static Task WaitForCompletionAsync<T>(this ChannelReader<T> reader) => reader.Completion.WaitAsync(TestTimeouts.Default);
}
