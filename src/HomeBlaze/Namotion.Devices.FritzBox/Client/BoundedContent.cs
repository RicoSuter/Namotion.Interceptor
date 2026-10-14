using System.Buffers;

namespace Namotion.Devices.FritzBox.Client;

internal static class BoundedContent
{
    /// <summary>
    /// Reads the whole content into memory. Throws <see cref="InvalidDataException"/> as soon as it is known to be
    /// larger than <paramref name="maximumBytes"/>.
    /// </summary>
    /// <returns>The content, positioned at its start.</returns>
    internal static async Task<MemoryStream> ReadAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw CreateTooLargeException(maximumBytes);
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        var result = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                if (result.Length + read > maximumBytes)
                {
                    throw CreateTooLargeException(maximumBytes);
                }

                await result.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            result.Position = 0;
            return result;
        }
        catch
        {
            await result.DisposeAsync();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static InvalidDataException CreateTooLargeException(int maximumBytes) =>
        new($"The FRITZ!Box list is larger than {maximumBytes} bytes.");
}
