using Namotion.Devices.Hyundai.Client;

namespace Namotion.Devices.Hyundai.Tests.Testing;

/// <summary>Paths and bodies of the Bluelink hosts for the fake handler. All tokens are made up.</summary>
internal static class BluelinkResponses
{
    public const string AuthorizePath = "/auth/api/v2/user/oauth2/authorize";
    public const string CertsPath = "/auth/api/v1/accounts/certs";
    public const string SignInPath = "/auth/account/signin";
    public const string TokenPath = "/auth/v1/token";
    public const string RefreshPath = "/auth/v2/token-refresh";
    public const string ExchangePath = "/auth/v1/token-exchange";
    public const string RegisterPath = "/api/v1/spa/notifications/register";
    public const string VehiclesPath = "/api/v1/spa/vehicles";
    public const string PinPath = "/api/v1/user/pin";

    public static string StatusPath(string vehicleId) => $"/api/v1/spa/vehicles/{vehicleId}/ccs2/carstatus/latest";

    public static string ParkLocationPath(string vehicleId) => $"/api/v1/spa/vehicles/{vehicleId}/location/park";

    public static string LegacyStatusRequestPath(string vehicleId) => $"/api/v1/spa/vehicles/{vehicleId}/status";

    public static string Ccs2StatusRequestPath(string vehicleId) => $"/api/v1/spa/vehicles/{vehicleId}/ccs2/carstatus";

    public static string LegacyChargePath(string vehicleId) => $"/api/v1/spa/vehicles/{vehicleId}/control/charge";

    public static string Ccs2ChargePath(string vehicleId) => $"/api/v2/spa/vehicles/{vehicleId}/ccs2/control/charge";

    public static string LegacyTemperaturePath(string vehicleId) => $"/api/v1/spa/vehicles/{vehicleId}/control/temperature";

    public static string Ccs2TemperaturePath(string vehicleId) => $"/api/v2/spa/vehicles/{vehicleId}/ccs2/control/temperature";

    public static string ChargeTargetPath(string vehicleId) => $"/api/v1/spa/vehicles/{vehicleId}/charge/target";

    public static string RecordsPath(string vehicleId) => $"/api/v1/spa/notifications/{vehicleId}/records";

    public static string Tokens(int generation, int expiresIn = 3599) =>
        $$"""{"accessToken":"cci-access-{{generation}}","refreshToken":"refresh-{{generation}}","exchangeableAccessToken":"exchangeable-{{generation}}","exchangeableRefreshToken":"exchangeable-refresh-{{generation}}","nonCcsToken":"non-ccs-{{generation}}","nonCcsRefreshToken":"non-ccs-refresh-{{generation}}","idToken":"id-{{generation}}","expiresIn":{{expiresIn}}}""";

    public static BluelinkTokenSet TokenSet(int generation) => BluelinkTokenSet.Parse(Tokens(generation));

    public static string Ccs(int generation, int expiresTime = 3600) =>
        $$"""{"accessToken":"ccs-{{generation}}","expiresTime":{{expiresTime}}}""";

    public static string Success(string resultJson = "{}") =>
        $$"""{"retCode":"S","resCode":"0000","resMsg":{{resultJson}},"msgId":"MSG-1"}""";

    public static string Failure(string resCode, string message = "error") =>
        $$"""{"retCode":"F","resCode":"{{resCode}}","resMsg":"{{message}}"}""";

    public static string Command(string messageId) =>
        $$"""{"retCode":"S","resCode":"0000","resMsg":{},"msgId":"{{messageId}}"}""";

    public static string Records(string messageId, string? result) =>
        $$"""{"retCode":"S","resCode":"0000","resMsg":[{"recordId":"OTHER","result":"fail"},{"recordId":"{{messageId}}","result":{{(result is null ? "null" : $"\"{result}\"")}}}]}""";

    public static string CciError(int code) => $$"""{"code":{{code}},"message":"rejected"}""";
}
