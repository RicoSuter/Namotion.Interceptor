using System.Xml;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.FritzBox.Client;
using Namotion.Devices.Upnp;

namespace Namotion.Devices.FritzBox;

/// <summary>
/// The result of a connection check.
/// </summary>
/// <param name="IsSuccess">Whether the login succeeded.</param>
/// <param name="HostAddress">
/// The host address exactly as checked, normalized (trimmed, brackets stripped for IPv6), for a setup dialog to save
/// alongside the other settings. Null when the input was invalid, since no address was ever checked.
/// </param>
/// <param name="ErrorMessage">Why the check failed, or null on success.</param>
/// <param name="Model">The model name the router reported, or null on failure.</param>
/// <param name="CertificatePin">The key the router presented, to store in <see cref="FritzBox.CertificatePin"/>.</param>
/// <param name="CertificateFingerprint">The SHA-256 fingerprint of the router's certificate, the form browsers show, for the user to compare.</param>
public sealed record FritzBoxConnectionCheckResult(
    bool IsSuccess,
    string? HostAddress,
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
    /// <summary>
    /// The error a check reports for a host address <see cref="TryNormalizeHostAddress"/> rejects.
    /// </summary>
    public const string InvalidHostAddressMessage = "Enter the router's IP address or host name without http:// or a port.";

    /// <summary>
    /// Opens a connection to the router and logs in. Never throws for a failed check, including a malformed host
    /// address: a failure is reported in the result. Cancellation still propagates.
    /// </summary>
    /// <param name="hostAddress">
    /// The host name or IP address of the router, without a scheme or a port: an IPv4 address, an IPv6 address with
    /// or without brackets, or a host name, including an internationalized one.
    /// </param>
    /// <param name="username">The user to log in as, or null for none.</param>
    /// <param name="password">The password of the user, or null for none.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public static Task<FritzBoxConnectionCheckResult> CheckAsync(
        string hostAddress, string? username, string? password, CancellationToken cancellationToken)
    {
        if (!TryNormalizeHostAddress(hostAddress, out var host))
        {
            return Task.FromResult(new FritzBoxConnectionCheckResult(false, null, InvalidHostAddressMessage, null, null, null));
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
                true, options.HostAddress, null, StringValues.NullIfBlank(info.GetString("NewModelName")), pin, fingerprint);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && IsHandledFailure(exception))
        {
            return new FritzBoxConnectionCheckResult(false, options.HostAddress, exception.Message, null, null, null);
        }
    }

    /// <summary>
    /// Validates a host address with the rules of <see cref="CheckAsync(string, string?, string?, CancellationToken)"/>:
    /// an IPv4 address, an IPv6 address with or without brackets, or a host name, including an internationalized one.
    /// Anything with a scheme, a path, embedded whitespace or a port is rejected.
    /// </summary>
    /// <param name="input">The host address as entered.</param>
    /// <param name="normalized">
    /// The address trimmed and, for IPv6, without brackets, as a router's <see cref="FritzBox.HostAddress"/> stores
    /// it; empty when the input is rejected.
    /// </param>
    /// <returns>Whether the input is a valid host address.</returns>
    public static bool TryNormalizeHostAddress(string? input, out string normalized)
    {
        var value = input?.Trim() ?? string.Empty;
        if (!IsBareHost(value))
        {
            normalized = string.Empty;
            return false;
        }

        var (hostName, port, wasBracketed) = SplitHostAndPort(value);
        var hostNameType = Uri.CheckHostName(hostName);
        if (port is not null || hostNameType == UriHostNameType.Unknown || (wasBracketed && hostNameType != UriHostNameType.IPv6))
        {
            normalized = string.Empty;
            return false;
        }

        normalized = hostName;
        return true;
    }

    /// <returns>Whether the value is not empty and has no whitespace, scheme or path.</returns>
    private static bool IsBareHost(string value) =>
        value.Length > 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('/');

    /// <returns>
    /// The host, the port when present after a single colon or after a closing bracket, and whether the host came
    /// from inside brackets (valid only for an IPv6 literal, never for a host name).
    /// </returns>
    private static (string HostName, string? Port, bool WasBracketed) SplitHostAndPort(string value)
    {
        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']');
            if (end < 0)
            {
                return (value, string.Empty, true);
            }

            var afterBracket = end + 1 < value.Length ? value[(end + 1)..] : null;
            return (value[1..end], afterBracket, true);
        }

        if (value.Count(character => character == ':') != 1)
        {
            return (value, null, false);
        }

        var separator = value.IndexOf(':');
        return (value[..separator], value[(separator + 1)..], false);
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
