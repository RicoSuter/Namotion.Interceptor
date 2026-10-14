using System.Xml;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.FritzBox.Client;
using Namotion.Devices.Upnp;

namespace Namotion.Devices.FritzBox;

/// <summary>
/// The result of a connection check.
/// </summary>
/// <param name="IsSuccess">Whether the login succeeded.</param>
/// <param name="ErrorMessage">Why the check failed, or null on success.</param>
/// <param name="Model">The model name the router reported, or null on failure.</param>
/// <param name="CertificatePin">The key the router presented, to store in <see cref="FritzBox.CertificatePin"/>.</param>
/// <param name="CertificateFingerprint">The SHA-256 fingerprint of the router's certificate, the form browsers show, for the user to compare.</param>
public sealed record FritzBoxConnectionCheckResult(
    bool IsSuccess,
    string? ErrorMessage,
    string? Model,
    string? CertificatePin,
    string? CertificateFingerprint);

/// <summary>
/// Logs in to a router once, for a setup dialog or a standalone host to confirm the address, the login and the
/// certificate before saving them.
/// </summary>
public static class FritzBoxConnectionCheck
{
    private const string InvalidHostAddressMessage = "Enter the router's IP address or host name without http:// or a port.";

    /// <summary>
    /// Opens a connection to the router and logs in. Never throws for a failed check, including a malformed host
    /// address: a failure is reported in the result. Cancellation still propagates.
    /// </summary>
    /// <param name="hostAddress">The host name or IP address of the router, without a scheme or a port.</param>
    /// <param name="username">The user to log in as, or null for none.</param>
    /// <param name="password">The password of the user, or null for none.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public static Task<FritzBoxConnectionCheckResult> CheckAsync(
        string hostAddress, string? username, string? password, CancellationToken cancellationToken)
    {
        if (!TryNormalizeHostAddress(hostAddress, out var host))
        {
            return Task.FromResult(new FritzBoxConnectionCheckResult(false, InvalidHostAddressMessage, null, null, null));
        }

        return CheckAsync(new FritzBoxConnectionOptions(host, username, password, null), cancellationToken);
    }

    internal static async Task<FritzBoxConnectionCheckResult> CheckAsync(FritzBoxConnectionOptions options, CancellationToken cancellationToken)
    {
        try
        {
            using var connection = await FritzBoxConnection.OpenAsync(options, NullLogger.Instance, cancellationToken);
            var info = await connection.CheckLoginAsync(cancellationToken);
            var pin = connection.TrustedCertificatePin;
            var fingerprint = connection.PresentedCertificate?.Fingerprint;
            return new FritzBoxConnectionCheckResult(
                true, null, StringValues.NullIfBlank(info.GetString("NewModelName")), pin, fingerprint);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && IsHandledFailure(exception))
        {
            return new FritzBoxConnectionCheckResult(false, exception.Message, null, null, null);
        }
    }

    /// <summary>
    /// Accepts a bare host name or IP address, trimmed; rejects anything with a scheme, a path, embedded
    /// whitespace or a port, since the TR-064 description port is fixed, not something the user supplies.
    /// </summary>
    private static bool TryNormalizeHostAddress(string hostAddress, out string host)
    {
        var value = hostAddress.Trim();
        if (value.Length == 0 || value.Any(char.IsWhiteSpace) || value.Contains("://", StringComparison.Ordinal) || value.Contains('/'))
        {
            host = string.Empty;
            return false;
        }

        var (hostName, port) = SplitHostAndPort(value);
        if (port is not null || Uri.CheckHostName(hostName) == UriHostNameType.Unknown)
        {
            host = string.Empty;
            return false;
        }

        host = hostName;
        return true;
    }

    /// <returns>The host and, when present, the port after a single colon or after a bracketed IPv6 literal.</returns>
    private static (string HostName, string? Port) SplitHostAndPort(string value)
    {
        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']');
            if (end < 0)
            {
                return (value, string.Empty);
            }

            var afterBracket = end + 1 < value.Length ? value[(end + 1)..] : null;
            return (value[1..end], afterBracket);
        }

        if (value.Count(character => character == ':') != 1)
        {
            return (value, null);
        }

        var separator = value.IndexOf(':');
        return (value[..separator], value[(separator + 1)..]);
    }

    /// <returns>Whether an exception from opening the connection or checking the login is a reportable failure, as opposed to a programming error.</returns>
    private static bool IsHandledFailure(Exception exception) =>
        IsRouterFailure(exception) || IsCommunicationFailure(exception) || IsFormatFailure(exception);

    private static bool IsRouterFailure(Exception exception) =>
        exception is FritzBoxLoginException or FritzBoxFirmwareException or FritzBoxCertificateException or UpnpFaultException;

    private static bool IsCommunicationFailure(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or TimeoutException;

    private static bool IsFormatFailure(Exception exception) =>
        exception is XmlException or UriFormatException or InvalidDataException;
}
