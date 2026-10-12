using System.Security.Cryptography;

namespace HomeBlaze.Storage.Internal;

internal static class StorageHash
{
    /// <summary>
    /// Hashes the bytes of a file. Always computed from bytes, so a write and a later read of the same file agree.
    /// </summary>
    public static string Compute(ReadOnlySpan<byte> content)
        => Convert.ToHexString(SHA256.HashData(content));

    /// <summary>
    /// Hashes the rest of a stream without holding its content in memory. Equal to <see cref="Compute"/> of the
    /// same bytes.
    /// </summary>
    public static async Task<string> ComputeAsync(Stream content, CancellationToken cancellationToken)
        => Convert.ToHexString(await SHA256.HashDataAsync(content, cancellationToken));
}
