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
    /// <summary>
    /// Opens a connection to the router and logs in. Never throws for a failed check: a failure is reported in the
    /// result. Cancellation still propagates.
    /// </summary>
    /// <param name="hostAddress">The host name or IP address of the router.</param>
    /// <param name="username">The user to log in as, or null for none.</param>
    /// <param name="password">The password of the user, or null for none.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public static Task<FritzBoxConnectionCheckResult> CheckAsync(
        string hostAddress, string? username, string? password, CancellationToken cancellationToken) =>
        CheckAsync(new FritzBoxConnectionOptions(hostAddress.Trim(), username, password, null), cancellationToken);

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

    /// <returns>Whether an exception from opening the connection or checking the login is a reportable failure, as opposed to a programming error.</returns>
    private static bool IsHandledFailure(Exception exception) =>
        IsLoginFailure(exception) || IsCommunicationFailure(exception);

    private static bool IsLoginFailure(Exception exception) =>
        exception is FritzBoxLoginException or FritzBoxFirmwareException or UpnpFaultException;

    private static bool IsCommunicationFailure(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or TimeoutException or XmlException;
}
