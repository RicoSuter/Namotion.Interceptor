using System.Net;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Upnp;

namespace Namotion.Devices.FritzBox.Client;

/// <summary>
/// An authenticated HTTPS connection to the TR-064 interface of one FRITZ!Box. Owns its HTTP client. Thread-safe.
/// </summary>
internal sealed class FritzBoxConnection : IDisposable
{
    internal const int MaximumAnswerBytes = 1024 * 1024;
    internal const int MaximumListBytes = 8 * 1024 * 1024;

    private readonly FritzBoxConnectionOptions _options;
    private readonly HttpClient _httpClient;
    private readonly UpnpSoapClient _soapClient;
    private readonly UnavailableActionLog _unavailableActionLog;
    private readonly PinnedCertificateValidator _certificateValidator;

    private FritzBoxConnection(Tr64Description description, Uri baseUri, FritzBoxConnectionOptions options, ILogger logger)
    {
        Description = description;
        BaseUri = baseUri;
        _options = options;
        _unavailableActionLog = new UnavailableActionLog(logger);
        _certificateValidator = new PinnedCertificateValidator(options.CertificatePin);

        _httpClient = FritzBoxHttpClient.Create(options, _certificateValidator, MaximumAnswerBytes);
        _soapClient = new UpnpSoapClient(_httpClient, baseUri);
    }

    internal Tr64Description Description { get; }

    internal Uri BaseUri { get; }

    /// <summary>
    /// The certificate the router presented at the last handshake, null before the first.
    /// </summary>
    internal CertificateInfo? PresentedCertificate => _certificateValidator.Presented;

    /// <summary>
    /// The WAN connection service carrying the internet connection, set by <see cref="ResolveInternetServiceAsync"/>.
    /// </summary>
    internal string? InternetServiceType { get; private set; }

    /// <summary>
    /// Reads the description and the HTTPS port over HTTP without authentication, then creates the connection.
    /// Sends no authenticated request.
    /// </summary>
    internal static async Task<FritzBoxConnection> OpenAsync(FritzBoxConnectionOptions options, ILogger logger, CancellationToken cancellationToken)
    {
        var (description, baseUri) = await Tr64Discovery.DiscoverAsync(options, cancellationToken);
        return new FritzBoxConnection(description, baseUri, options, logger);
    }

    internal bool HasService(string serviceType) => Description.ControlPaths.ContainsKey(serviceType);

    /// <summary>
    /// Reads the device information, the first authenticated request. Throws <see cref="FritzBoxLoginException"/>
    /// when the login is refused and <see cref="FritzBoxCertificateException"/> when the key is not the pinned one.
    /// </summary>
    internal async Task<UpnpSoapResponse> CheckLoginAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await QueryAsync(Tr64Services.DeviceInfo, "GetInfo", null, cancellationToken);
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new FritzBoxLoginException(exception);
        }
        catch (UpnpFaultException exception) when (exception.ErrorCode == 606)
        {
            throw new FritzBoxLoginException(exception);
        }
    }

    /// <summary>
    /// Finds the WAN connection service that carries the internet connection. Both are listed on many routers, so
    /// the router is asked, and when it cannot say, the connected one is taken.
    /// </summary>
    internal async Task ResolveInternetServiceAsync(CancellationToken cancellationToken)
    {
        var answer = await ReadOrDefaultAsync(Tr64Services.Layer3Forwarding, "GetDefaultConnectionService", null, cancellationToken);
        var defaultService = answer?.GetString("NewDefaultConnectionService");
        if (defaultService?.Contains("WANPPPConnection", StringComparison.Ordinal) == true && HasService(Tr64Services.WanPppConnection))
        {
            InternetServiceType = Tr64Services.WanPppConnection;
        }
        else if (defaultService?.Contains("WANIPConnection", StringComparison.Ordinal) == true && HasService(Tr64Services.WanIpConnection))
        {
            InternetServiceType = Tr64Services.WanIpConnection;
        }
        else
        {
            InternetServiceType = await FindConnectedServiceAsync(cancellationToken);
        }
    }

    internal async Task<UpnpSoapResponse> QueryAsync(
        string serviceType, string action, (string Name, string Value)[]? arguments, CancellationToken cancellationToken)
    {
        var service = Description.TryGetService(serviceType) ?? throw new FritzBoxServiceUnavailableException(serviceType);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        try
        {
            return await _soapClient.QueryAsync(service, action, arguments ?? [], timeout.Token);
        }
        catch (Exception exception) when (IsTranslated(exception, action, timeout, cancellationToken, out var translated))
        {
            throw translated;
        }
    }

    internal async Task InvokeAsync(
        string serviceType, string action, (string Name, string Value)[]? arguments, CancellationToken cancellationToken)
    {
        var service = Description.TryGetService(serviceType) ?? throw new FritzBoxServiceUnavailableException(serviceType);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        try
        {
            await _soapClient.InvokeAsync(service, action, arguments ?? [], timeout.Token);
        }
        catch (Exception exception) when (IsTranslated(exception, action, timeout, cancellationToken, out var translated))
        {
            throw translated;
        }
    }

    /// <summary>
    /// Reads an action whose value is optional: a fault, a service the router does not offer, or a refusal of this
    /// one action returns null and is logged once. Any other failure throws.
    /// </summary>
    internal async Task<UpnpSoapResponse?> ReadOrDefaultAsync(
        string serviceType, string action, (string Name, string Value)[]? arguments, CancellationToken cancellationToken)
    {
        try
        {
            return await QueryAsync(serviceType, action, arguments, cancellationToken);
        }
        catch (Exception exception) when (exception is UpnpFaultException or FritzBoxServiceUnavailableException ||
                                          exception is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
        {
            LogUnavailable(serviceType, action, exception);
            return null;
        }
    }

    /// <summary>
    /// Downloads a list the router returned the path of. Throws <see cref="InvalidDataException"/> when the path is
    /// not on the router or the list is larger than <paramref name="maximumBytes"/>.
    /// </summary>
    internal async Task<MemoryStream> DownloadAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        if (!Tr64Description.IsLocalPath(path))
        {
            throw new InvalidDataException("The FRITZ!Box returned a list path that is not on its own host.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.DownloadTimeout);
        try
        {
            using var response = await _httpClient.GetAsync(new Uri(BaseUri, path), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            return await BoundedContent.ReadAsync(response.Content, maximumBytes, timeout.Token);
        }
        catch (Exception exception) when (IsTranslated(exception, path, timeout, cancellationToken, out var translated))
        {
            throw translated;
        }
    }

    /// <summary>
    /// Logs that an optional action is unavailable: as information the first time, as debug after that.
    /// </summary>
    internal void LogUnavailable(string serviceType, string action, Exception exception) =>
        _unavailableActionLog.Log(serviceType, action, exception);

    public void Dispose() => _httpClient.Dispose();

    private bool IsTranslated(
        Exception exception, string request, CancellationTokenSource timeout, CancellationToken cancellationToken, out Exception translated)
    {
        if (exception is HttpRequestException && _certificateValidator.IsRejected)
        {
            translated = new FritzBoxCertificateException(PresentedCertificate, exception);
            return true;
        }

        if (exception is OperationCanceledException && timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            translated = new TimeoutException($"The FRITZ!Box did not answer {request} in time.", exception);
            return true;
        }

        translated = exception;
        return false;
    }

    private async Task<string?> FindConnectedServiceAsync(CancellationToken cancellationToken)
    {
        string? fallback = null;
        foreach (var serviceType in (string[])[Tr64Services.WanPppConnection, Tr64Services.WanIpConnection])
        {
            if (!HasService(serviceType))
            {
                continue;
            }

            fallback ??= serviceType;
            var status = await ReadOrDefaultAsync(serviceType, "GetStatusInfo", null, cancellationToken);
            if (string.Equals(status?.GetString("NewConnectionStatus"), "Connected", StringComparison.Ordinal))
            {
                return serviceType;
            }
        }

        return fallback;
    }
}
