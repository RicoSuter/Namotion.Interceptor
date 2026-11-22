using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Namotion.Devices.Hyundai.Tests.Testing;

/// <summary>An account with a fake handler that answers a full legacy poll, and a fake clock.</summary>
internal static class TestAccount
{
    public static readonly DateTimeOffset Start = new(2026, 11, 22, 10, 30, 0, TimeSpan.Zero);

    public static FakeHttpMessageHandler CreateHandler() => new FakeHttpMessageHandler()
        .Respond(BluelinkResponses.RefreshPath, HttpStatusCode.OK, BluelinkResponses.Tokens(1))
        .Respond(BluelinkResponses.ExchangePath, HttpStatusCode.OK, BluelinkResponses.Ccs(1))
        .Respond(BluelinkResponses.VehiclesPath, HttpStatusCode.OK, FixtureLoader.Load("vehicles.json"))
        .Respond(BluelinkResponses.StatusPath("VEHICLE-1"), HttpStatusCode.OK, FixtureLoader.Load("carstatus-legacy-ev.json"))
        .Respond(BluelinkResponses.StatusPath("VEHICLE-2"), HttpStatusCode.OK, FixtureLoader.Load("carstatus-legacy-ev.json"))
        .Respond(BluelinkResponses.ParkLocationPath("VEHICLE-1"), HttpStatusCode.OK, FixtureLoader.Load("location-park.json"))
        .Respond(BluelinkResponses.ParkLocationPath("VEHICLE-2"), HttpStatusCode.OK, FixtureLoader.Load("location-park.json"));

    /// <summary>Adds the answers of a successful sign in that returns the token set of the generation.</summary>
    public static FakeHttpMessageHandler RespondLogin(FakeHttpMessageHandler handler, int generation)
    {
        using var key = RSA.Create(2048);
        var parameters = key.ExportParameters(false);
        var certs = $$$"""{"retValue":{"kid":"key-1","n":"{{{Base64Url.EncodeToString(parameters.Modulus)}}}","e":"{{{Base64Url.EncodeToString(parameters.Exponent)}}}"}}""";
        return handler
            .Respond(BluelinkResponses.AuthorizePath, HttpStatusCode.OK, "<html>login</html>", ("Set-Cookie", "session=abc; Path=/"))
            .Respond(BluelinkResponses.CertsPath, HttpStatusCode.OK, certs)
            .Respond(BluelinkResponses.SignInPath, HttpStatusCode.Found, "", ("Location", "https://oneapp.hyundai.com/redirect?code=CODE-1&state=ccsp"))
            .Respond(BluelinkResponses.TokenPath, HttpStatusCode.OK, BluelinkResponses.Tokens(generation));
    }

    public static (BluelinkAccount Account, FakeHttpMessageHandler Handler, FakeTimeProvider Time) Create(
        FakeHttpMessageHandler? handler = null, string password = "", string pin = "")
    {
        handler ??= CreateHandler();
        var time = new FakeTimeProvider(Start);
        var account = new BluelinkAccount(time, NullLoggerFactory.Instance, () => new HttpClient(handler))
        {
            Email = "user@example.invalid",
            Password = password,
            Pin = pin,
            Tokens = BluelinkResponses.TokenSet(0).Serialize(),
            DeviceId = "device-1"
        };
        return (account, handler, time);
    }
}
