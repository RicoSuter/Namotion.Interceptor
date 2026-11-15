using Opc.Ua;

namespace Namotion.Interceptor.OpcUa;

/// <summary>
/// Names the folder that holds the certificates of a store, for messages that point at it.
/// </summary>
internal static class CertificateFolder
{
    // A directory store keeps its certificates in a certs subfolder; other store types are named by their path.
    public static string Get(string? storeType, string? storePath) =>
        storeType == CertificateStoreType.Directory
            ? Path.Combine(storePath ?? string.Empty, "certs")
            : storePath ?? string.Empty;

    public static string Get(CertificateStoreIdentifier? store) =>
        Get(store?.StoreType, store?.StorePath);
}
