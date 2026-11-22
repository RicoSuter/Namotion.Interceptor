using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Devices.FritzBox.Client;

namespace Namotion.Devices.FritzBox.Tests.Testing;

/// <summary>
/// A fake FRITZ!Box on 127.0.0.1 with the services, answers and lists of the fixtures.
/// </summary>
internal sealed partial class FakeFritzBox : IAsyncDisposable
{
    internal const string Realm = "F!Box SOAP-Auth";
    internal const string HostListPath = "/devicehostlist.lua?sid=0123456789abcdef";
    internal const string MeshListPath = "/meshlist.lua?sid=0123456789abcdef";
    internal const string GuestWlanServiceType = "urn:dslforum-org:service:WLANConfiguration:3";

    private const string Nonce = "4A3B2C1D0E0F1A2B";

    // Returned by an answer to have the request answered with the "no such entry" fault, as the router does for an
    // unknown host.
    private const string NoSuchEntry = "\u0000714";

    private readonly WebApplication _application;
    private readonly ConcurrentDictionary<string, Hold> _holds = new(StringComparer.Ordinal);
    private int _failedLogins;
    private int _securityPortRequests;
    private int _secureRequests;
    private int _plainAuthorizationHeaders;
    private int _basicAuthorizationHeaders;
    private volatile bool _isGuestWlanEnabled;
    private Hold? _armedGuestWlanHold;

    private FakeFritzBox(WebApplication application)
    {
        _application = application;
    }

    internal string Username { get; set; } = "homeblaze";

    internal string Password { get; set; } = "secret";

    internal X509Certificate2 Certificate { get; set; } = CreateCertificate();

    /// <summary>
    /// The pin of <see cref="Certificate"/>.
    /// </summary>
    internal string PublicKeyPin => CertificateInfo.Create(Certificate).Pin;

    internal int HttpPort { get; private set; }

    internal int HttpsPort { get; private set; }

    /// <summary>
    /// The body served for <see cref="Tr64Description.DescriptionPath"/> over plain HTTP.
    /// </summary>
    internal string DescriptionXml { get; set; } = Fixtures.Read("tr64desc.xml");

    internal string HostListXml { get; set; } = Fixtures.Read("hostlist.xml");

    internal string MeshListJson { get; set; } = Fixtures.Read("meshlist.json");

    /// <summary>
    /// The value of <c>NewFilterProfileList</c> that <c>GetFilterProfiles</c> returns.
    /// </summary>
    internal string FilterProfilesXml { get; set; } = Fixtures.Read("filterprofiles.xml");

    /// <summary>
    /// The values of an answer by <c>serviceType#action</c>, overriding the defaults.
    /// </summary>
    internal ConcurrentDictionary<string, string> Answers { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// UPnP error codes by action name; an action listed here is answered with a fault.
    /// </summary>
    internal ConcurrentDictionary<string, int> Faults { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// HTTP status codes by action name for an authenticated action, or by path for a plain GET of a list
    /// (<see cref="HostListPath"/> or <see cref="MeshListPath"/>); a key listed here is answered with that status
    /// and no body.
    /// </summary>
    internal ConcurrentDictionary<string, int> HttpErrors { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Paths of a plain GET of a list (<see cref="HostListPath"/> or <see cref="MeshListPath"/>) that are answered
    /// by aborting the connection before any response is sent, simulating a reset rather than an HTTP status error.
    /// </summary>
    internal ConcurrentDictionary<string, bool> AbortedPaths { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The IPv4 addresses whose internet access <c>DisallowWANAccessByIP</c> blocked (true) or allowed (false). The host
    /// list and <c>GetWANAccessByIP</c> report it for the item of that address.
    /// </summary>
    internal ConcurrentDictionary<string, bool> InternetAccessBlocks { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The authenticated actions received, in order.
    /// </summary>
    internal ConcurrentQueue<string> Actions { get; } = new();

    internal int FailedLogins => Volatile.Read(ref _failedLogins);

    internal int SecurityPortRequests => Volatile.Read(ref _securityPortRequests);

    /// <summary>
    /// The requests received over HTTPS, authenticated or not.
    /// </summary>
    internal int SecureRequests => Volatile.Read(ref _secureRequests);

    /// <summary>
    /// The requests received over plain HTTP that carried an <c>Authorization</c> header.
    /// </summary>
    internal int PlainAuthorizationHeaders => Volatile.Read(ref _plainAuthorizationHeaders);

    /// <summary>
    /// The requests received over HTTPS that carried an <c>Authorization: Basic</c> header.
    /// </summary>
    internal int BasicAuthorizationHeaders => Volatile.Read(ref _basicAuthorizationHeaders);

    /// <summary>
    /// Whether an unauthenticated request is challenged with Basic instead of Digest. A Basic answer is never accepted.
    /// </summary>
    internal bool ChallengesWithBasic { get; set; }

    internal bool IsGuestWlanEnabled
    {
        get => _isGuestWlanEnabled;
        set => _isGuestWlanEnabled = value;
    }

    internal static async Task<FakeFritzBox> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.Logging.ClearProviders();

        FakeFritzBox? fake = null;
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(https =>
                https.ServerCertificateSelector = (_, _) => fake!.Certificate));
        });

        var application = builder.Build();
        fake = new FakeFritzBox(application);
        application.Run(fake.HandleAsync);
        await application.StartAsync();

        var addresses = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        foreach (var address in addresses)
        {
            var uri = new Uri(address);
            if (uri.Scheme == Uri.UriSchemeHttps)
            {
                fake.HttpsPort = uri.Port;
            }
            else
            {
                fake.HttpPort = uri.Port;
            }
        }

        return fake;
    }

    internal static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=fritz.box", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>
    /// Makes the next guest WLAN <c>GetInfo</c> capture the state when it arrives and wait for
    /// <see cref="Hold.Released"/> before answering it.
    /// </summary>
    internal Hold HoldNextGuestWlanInfo()
    {
        var hold = new Hold();
        Volatile.Write(ref _armedGuestWlanHold, hold);
        return hold;
    }

    /// <summary>
    /// Holds the next request for a key until <see cref="Hold.Released"/> completes or the client gives up. The key is
    /// the path of a plain GET, or <c>serviceType#action</c> of an authenticated action.
    /// </summary>
    /// <remarks>
    /// Holds an authenticated action before its credentials are checked and its answer is computed, so both reflect
    /// the state at release, unlike <see cref="HoldNextGuestWlanInfo"/>, which captures the state on arrival. The
    /// action is added to <see cref="Actions"/> only after release.
    /// </remarks>
    internal Hold HoldNext(string key)
    {
        var hold = new Hold();
        _holds[key] = hold;
        return hold;
    }

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync();
        await _application.DisposeAsync();
        Certificate.Dispose();
    }

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        if (!request.IsHttps)
        {
            await HandlePlainAsync(context);
            return;
        }

        Interlocked.Increment(ref _secureRequests);
        if (request.Headers.Authorization.ToString().StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _basicAuthorizationHeaders);
        }

        if (HttpMethods.IsGet(request.Method))
        {
            await HandleSecureGetAsync(context);
            return;
        }

        var soapAction = GetAction(request);
        var hasCredentials = request.Headers.Authorization.Count > 0;

        // Holds the request that carries credentials before checking them, so a password changed while it is held
        // applies to it.
        if (hasCredentials && soapAction is (var heldServiceType, var heldAction) &&
            !await TryPassHoldAsync($"{heldServiceType}#{heldAction}", context))
        {
            return;
        }

        if (!IsAuthorized(request))
        {
            if (hasCredentials)
            {
                Interlocked.Increment(ref _failedLogins);
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = ChallengesWithBasic
                ? $"Basic realm=\"{Realm}\""
                : $"Digest realm=\"{Realm}\", nonce=\"{Nonce}\", algorithm=MD5, qop=\"auth\"";
            return;
        }

        if (soapAction is not (var serviceType, var action))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        Actions.Enqueue(action);

        if (HttpErrors.TryGetValue(action, out var statusCode))
        {
            context.Response.StatusCode = statusCode;
            return;
        }

        if (Faults.TryGetValue(action, out var errorCode))
        {
            await WriteAsync(context, StatusCodes.Status500InternalServerError, "text/xml", CreateFault(errorCode));
            return;
        }

        var values = await AnswerAsync(serviceType, action, request);
        if (values == NoSuchEntry)
        {
            await WriteAsync(context, StatusCodes.Status500InternalServerError, "text/xml", CreateFault(714));
            return;
        }

        if (values is null)
        {
            await WriteAsync(context, StatusCodes.Status500InternalServerError, "text/xml", CreateFault(401));
            return;
        }

        await WriteAsync(context, StatusCodes.Status200OK, "text/xml", CreateEnvelope(serviceType, action, values));
    }

    private async Task HandleSecureGetAsync(HttpContext context)
    {
        var request = context.Request;
        var pathAndQuery = request.Path + request.QueryString.ToString();
        if (AbortedPaths.ContainsKey(pathAndQuery))
        {
            context.Abort();
            return;
        }

        (string Body, string ContentType)? list = pathAndQuery switch
        {
            HostListPath => (ApplyInternetAccessBlocks(HostListXml), "text/xml"),
            MeshListPath => (MeshListJson, "application/json"),
            _ => null
        };

        if (list is not (var body, var contentType))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
        else if (HttpErrors.TryGetValue(pathAndQuery, out var statusCode))
        {
            context.Response.StatusCode = statusCode;
        }
        else
        {
            await WriteAsync(context, StatusCodes.Status200OK, contentType, body);
        }
    }

    private async Task HandlePlainAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.Headers.Authorization.Count > 0)
        {
            Interlocked.Increment(ref _plainAuthorizationHeaders);
        }

        if (HttpMethods.IsGet(request.Method) && request.Path == Tr64Description.DescriptionPath)
        {
            if (await TryPassHoldAsync(Tr64Description.DescriptionPath, context))
            {
                await WriteAsync(context, StatusCodes.Status200OK, "text/xml", DescriptionXml);
            }
        }
        else if (HttpMethods.IsPost(request.Method) && GetAction(request) is (Tr64Services.DeviceInfo, "GetSecurityPort"))
        {
            Interlocked.Increment(ref _securityPortRequests);
            var values = Answers.TryGetValue($"{Tr64Services.DeviceInfo}#GetSecurityPort", out var answer)
                ? answer
                : $"<NewSecurityPort>{HttpsPort}</NewSecurityPort>";
            await WriteAsync(context, StatusCodes.Status200OK, "text/xml", CreateEnvelope(Tr64Services.DeviceInfo, "GetSecurityPort", values));
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
    }

    /// <returns>False when the request was held and the client gave up waiting, so nothing must be answered.</returns>
    private async Task<bool> TryPassHoldAsync(string key, HttpContext context)
    {
        if (!_holds.TryRemove(key, out var hold))
        {
            return true;
        }

        hold.Taken.TrySetResult();
        try
        {
            await hold.Released.Task.WaitAsync(context.RequestAborted);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<string?> AnswerAsync(string serviceType, string action, HttpRequest request)
    {
        if (Answers.TryGetValue($"{serviceType}#{action}", out var answer))
        {
            return answer;
        }

        if (serviceType == GuestWlanServiceType && action == "SetEnable")
        {
            using var reader = new StreamReader(request.Body);
            _isGuestWlanEnabled = NewEnableRegex().Match(await reader.ReadToEndAsync()).Groups[1].Value == "1";
            return string.Empty;
        }

        if (serviceType is Tr64Services.Hosts or Tr64Services.HostFilter &&
            await AnswerHostAsync(action, request) is { } hostAnswer)
        {
            return hostAnswer;
        }

        if (serviceType == GuestWlanServiceType && action == "GetInfo")
        {
            var isEnabled = _isGuestWlanEnabled;
            if (Interlocked.Exchange(ref _armedGuestWlanHold, null) is { } hold)
            {
                hold.Taken.TrySetResult();
                await hold.Released.Task;
            }

            return $"<NewEnable>{(isEnabled ? 1 : 0)}</NewEnable><NewSSID>Guests</NewSSID><NewChannel>6</NewChannel><NewX_AVM-DE_FrequencyBand>2400</NewX_AVM-DE_FrequencyBand>";
        }

        return (serviceType, action) switch
        {
            (Tr64Services.DeviceInfo, "GetInfo") =>
                "<NewManufacturerName>AVM</NewManufacturerName><NewModelName>FRITZ!Box 7590</NewModelName><NewSerialNumber>SERIAL0001</NewSerialNumber>" +
                "<NewSoftwareVersion>154.07.57</NewSoftwareVersion><NewHardwareVersion>FRITZ!Box 7590</NewHardwareVersion><NewUpTime>3600</NewUpTime>",
            (Tr64Services.UserInterface, "GetInfo") =>
                "<NewUpgradeAvailable>0</NewUpgradeAvailable><NewX_AVM-DE_Version></NewX_AVM-DE_Version>",
            (Tr64Services.Layer3Forwarding, "GetDefaultConnectionService") =>
                "<NewDefaultConnectionService>1.WANPPPConnection.1</NewDefaultConnectionService>",
            (Tr64Services.WanCommonInterfaceConfig, "X_AVM-DE_GetOnlineMonitor") =>
                "<NewTotalNumberSyncGroups>1</NewTotalNumberSyncGroups><Newmax_ds>31250000</Newmax_ds><Newmax_us>5000000</Newmax_us><Newus_current_bps>5000,4000,3000</Newus_current_bps><Newds_current_bps>120000,1000,0</Newds_current_bps>",
            (Tr64Services.WanPppConnection, "GetStatusInfo") =>
                "<NewConnectionStatus>Connected</NewConnectionStatus><NewLastConnectionError>ERROR_NONE</NewLastConnectionError><NewUptime>7200</NewUptime>",
            (Tr64Services.WanPppConnection, "GetExternalIPAddress") =>
                "<NewExternalIPAddress>203.0.113.7</NewExternalIPAddress>",
            (Tr64Services.WanIpConnection, "GetStatusInfo") =>
                "<NewConnectionStatus>Unconfigured</NewConnectionStatus><NewUptime>0</NewUptime>",
            (Tr64Services.AppSetup, "GetAppRemoteInfo") =>
                "<NewSubnetMask>255.255.255.0</NewSubnetMask><NewIPAddress>192.168.178.1</NewIPAddress>" +
                "<NewExternalIPAddress>203.0.113.7</NewExternalIPAddress><NewExternalIPv6Address>2001:db8::7</NewExternalIPv6Address>" +
                "<NewRemoteAccessDDNSEnabled>0</NewRemoteAccessDDNSEnabled><NewRemoteAccessDDNSDomain></NewRemoteAccessDDNSDomain>" +
                "<NewMyFritzEnabled>0</NewMyFritzEnabled><NewMyFritzDynDNSName></NewMyFritzDynDNSName>",
            ("urn:dslforum-org:service:WLANConfiguration:1", "GetInfo") =>
                "<NewEnable>1</NewEnable><NewSSID>Home</NewSSID><NewChannel>6</NewChannel><NewX_AVM-DE_FrequencyBand>2400</NewX_AVM-DE_FrequencyBand>",
            ("urn:dslforum-org:service:WLANConfiguration:2", "GetInfo") =>
                "<NewEnable>1</NewEnable><NewSSID>Home</NewSSID><NewChannel>36</NewChannel><NewX_AVM-DE_FrequencyBand>5000</NewX_AVM-DE_FrequencyBand>",
            ("urn:dslforum-org:service:WLANConfiguration:1", "GetTotalAssociations") => "<NewTotalAssociations>3</NewTotalAssociations>",
            ("urn:dslforum-org:service:WLANConfiguration:2", "GetTotalAssociations") => "<NewTotalAssociations>5</NewTotalAssociations>",
            (GuestWlanServiceType, "GetTotalAssociations") => "<NewTotalAssociations>0</NewTotalAssociations>",
            (Tr64Services.Hosts, "X_AVM-DE_GetHostListPath") => $"<NewX_AVM-DE_HostListPath>{WebUtility.HtmlEncode(HostListPath)}</NewX_AVM-DE_HostListPath>",
            (Tr64Services.HostFilter, "GetFilterProfiles") =>
                $"<NewFilterProfileList>{WebUtility.HtmlEncode(FilterProfilesXml)}</NewFilterProfileList>",
            (Tr64Services.Hosts, "X_AVM-DE_GetMeshListPath") => $"<NewX_AVM-DE_MeshListPath>{WebUtility.HtmlEncode(MeshListPath)}</NewX_AVM-DE_MeshListPath>",
            _ => null
        };
    }

    /// <returns>The answer to a host or host filter action that reads or changes the host list state, or null for any
    /// other action.</returns>
    private async Task<string?> AnswerHostAsync(string action, HttpRequest request) => action switch
    {
        "GetSpecificHostEntry" => await AnswerSpecificHostEntryAsync(request),
        "X_AVM-DE_GetSpecificHostEntryByIP" => await AnswerSpecificHostEntryByIpAsync(request),
        "DisallowWANAccessByIP" or "GetWANAccessByIP" => await AnswerInternetAccessAsync(action, request),
        _ => null
    };

    private async Task<string?> AnswerInternetAccessAsync(string action, HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync();
        var ipAddress = NewIpv4AddressRegex().Match(body).Groups[1].Value;
        if (action == "DisallowWANAccessByIP")
        {
            InternetAccessBlocks[ipAddress] = NewDisallowRegex().Match(body).Groups[1].Value == "1";
            return string.Empty;
        }

        // Reported like the host list does, so both answers agree for an address the test did not block or allow.
        var item = ApplyInternetAccessBlocks(HostListXml).Split("</Item>").FirstOrDefault(entry => IpAddressRegex().Match(entry).Groups[1].Value == ipAddress);
        if (item is null)
        {
            return null;
        }

        var wanAccess = WanAccessRegex().Match(item).Value;
        var disallow = DisallowRegex().Match(item).Value;
        return wanAccess.Replace("X_AVM-DE_WANAccess", "NewWANAccess", StringComparison.Ordinal) +
            disallow.Replace("X_AVM-DE_Disallow", "NewDisallow", StringComparison.Ordinal);
    }

    /// <returns>The IP address and activity of the host list item of the MAC address in the request, or
    /// <see cref="NoSuchEntry"/> for an unknown one.</returns>
    private async Task<string?> AnswerSpecificHostEntryAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        var macAddress = NewMacAddressRegex().Match(await reader.ReadToEndAsync()).Groups[1].Value;
        var item = HostListXml.Split("</Item>").FirstOrDefault(entry =>
            string.Equals(MacAddressRegex().Match(entry).Groups[1].Value, macAddress, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return NoSuchEntry;
        }

        return $"<NewIPAddress>{IpAddressRegex().Match(item).Groups[1].Value}</NewIPAddress><NewActive>{ActiveRegex().Match(item).Groups[1].Value}</NewActive>";
    }

    /// <returns>The MAC address of the first host list item with the IP address in the request, or
    /// <see cref="NoSuchEntry"/> for an unknown one.</returns>
    private async Task<string> AnswerSpecificHostEntryByIpAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        var ipAddress = NewIpAddressRegex().Match(await reader.ReadToEndAsync()).Groups[1].Value;
        var item = HostListXml.Split("</Item>").FirstOrDefault(entry => IpAddressRegex().Match(entry).Groups[1].Value == ipAddress);
        return item is null
            ? NoSuchEntry
            : $"<NewMACAddress>{MacAddressRegex().Match(item).Groups[1].Value}</NewMACAddress><NewActive>{ActiveRegex().Match(item).Groups[1].Value}</NewActive>";
    }

    private string ApplyInternetAccessBlocks(string hostListXml)
    {
        if (InternetAccessBlocks.IsEmpty)
        {
            return hostListXml;
        }

        return HostListItemRegex().Replace(hostListXml, item =>
        {
            var ipAddress = IpAddressRegex().Match(item.Value).Groups[1].Value;
            if (!InternetAccessBlocks.TryGetValue(ipAddress, out var isBlocked))
            {
                return item.Value;
            }

            var value = WanAccessRegex().Replace(item.Value, $"<X_AVM-DE_WANAccess>{(isBlocked ? "denied" : "granted")}</X_AVM-DE_WANAccess>");
            return DisallowRegex().Replace(value, $"<X_AVM-DE_Disallow>{(isBlocked ? 1 : 0)}</X_AVM-DE_Disallow>");
        });
    }

    private bool IsAuthorized(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var values = DigestParameterRegex().Matches(header[7..]).ToDictionary(
            match => match.Groups[1].Value,
            match => match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value,
            StringComparer.OrdinalIgnoreCase);

        if (!values.TryGetValue("username", out var username) || !values.TryGetValue("nonce", out var nonce) ||
            !values.TryGetValue("uri", out var uri) || !values.TryGetValue("response", out var response))
        {
            return false;
        }

        if (username != Username || nonce != Nonce)
        {
            return false;
        }

        var hash1 = Md5($"{username}:{Realm}:{Password}");
        var hash2 = Md5($"{request.Method}:{uri}");
        var expected = values.TryGetValue("qop", out var qop)
            ? Md5($"{hash1}:{nonce}:{values.GetValueOrDefault("nc")}:{values.GetValueOrDefault("cnonce")}:{qop}:{hash2}")
            : Md5($"{hash1}:{nonce}:{hash2}");
        return string.Equals(response, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static (string ServiceType, string Action)? GetAction(HttpRequest request)
    {
        var soapAction = request.Headers["SOAPACTION"].ToString().Trim('"');
        var separator = soapAction.IndexOf('#');
        return separator > 0 ? (soapAction[..separator], soapAction[(separator + 1)..]) : null;
    }

    private static string CreateEnvelope(string serviceType, string action, string values) =>
        "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
        $"<u:{action}Response xmlns:u=\"{serviceType}\">{values}</u:{action}Response></s:Body></s:Envelope>";

    private static string CreateFault(int errorCode) =>
        "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
        "<s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail>" +
        $"<UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>{errorCode}</errorCode><errorDescription>Error</errorDescription></UPnPError>" +
        "</detail></s:Fault></s:Body></s:Envelope>";

    private static string Md5(string value) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task WriteAsync(HttpContext context, int statusCode, string contentType, string body)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = contentType + "; charset=\"utf-8\"";
        await context.Response.WriteAsync(body, context.RequestAborted);
    }

    [GeneratedRegex("""(\w+)=(?:"([^"]*)"|([^\s,]*))""")]
    private static partial Regex DigestParameterRegex();

    [GeneratedRegex("<NewEnable>(\\d)</NewEnable>")]
    private static partial Regex NewEnableRegex();

    [GeneratedRegex("<NewIPv4Address>([^<]*)</NewIPv4Address>")]
    private static partial Regex NewIpv4AddressRegex();

    [GeneratedRegex("<NewIPAddress>([^<]*)</NewIPAddress>")]
    private static partial Regex NewIpAddressRegex();

    [GeneratedRegex("<NewMACAddress>([^<]*)</NewMACAddress>")]
    private static partial Regex NewMacAddressRegex();

    [GeneratedRegex("<MACAddress>([^<]*)</MACAddress>")]
    private static partial Regex MacAddressRegex();

    [GeneratedRegex("<Active>([^<]*)</Active>")]
    private static partial Regex ActiveRegex();

    [GeneratedRegex("<NewDisallow>(\\d)</NewDisallow>")]
    private static partial Regex NewDisallowRegex();

    [GeneratedRegex("<Item>.*?</Item>", RegexOptions.Singleline)]
    private static partial Regex HostListItemRegex();

    [GeneratedRegex("<IPAddress>([^<]*)</IPAddress>")]
    private static partial Regex IpAddressRegex();

    [GeneratedRegex("<X_AVM-DE_WANAccess>[^<]*</X_AVM-DE_WANAccess>")]
    private static partial Regex WanAccessRegex();

    [GeneratedRegex("<X_AVM-DE_Disallow>[^<]*</X_AVM-DE_Disallow>")]
    private static partial Regex DisallowRegex();

    /// <summary>
    /// A request held by the fake: <see cref="Taken"/> completes when it arrives, and it is answered once the test
    /// completes <see cref="Released"/>.
    /// </summary>
    internal sealed class Hold
    {
        internal TaskCompletionSource Taken { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
