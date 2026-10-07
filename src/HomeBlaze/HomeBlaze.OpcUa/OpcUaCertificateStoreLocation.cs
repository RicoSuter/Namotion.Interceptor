using HomeBlaze.Abstractions;
using Namotion.Interceptor;

namespace HomeBlaze.OpcUa;

/// <summary>
/// Resolves the OPC UA certificate store folder for a role inside the instance data directory.
/// </summary>
internal static class OpcUaCertificateStoreLocation
{
    /// <summary>
    /// Returns <c>&lt;data&gt;/OpcUa/&lt;role&gt;/Pki</c>, or null when no data directory is available so the
    /// library default applies.
    /// </summary>
    public static string? Resolve(IInterceptorSubject subject, string role)
    {
        var dataDirectory = subject.Context.TryGetService<IDataDirectoryProvider>()?.DataDirectory;
        return dataDirectory is null ? null : Path.Combine(dataDirectory, "OpcUa", role, "Pki");
    }
}
