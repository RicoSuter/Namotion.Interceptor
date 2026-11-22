namespace Namotion.Devices.Hyundai.Client;

/// <summary>
/// The constants of the Hyundai Bluelink and OneApp Android apps that the unofficial API checks. Hyundai changes them
/// with app releases; porting a login fix from hyundai_kia_connect_api (KiaUvoApiEU.py, cci_fingerprint.py) usually
/// touches only this class.
/// </summary>
internal static class BluelinkAppConstants
{
    public const string LoginHost = "https://idpconnect-eu.hyundai.com";
    public const string CciHost = "https://cci-api-eu.hyundai.com";
    public const string VehicleApiHost = "https://prd.eu-ccapi.hyundai.com:8080";

    public const string OneAppClientId = "4f4953b5-02e1-4dbc-8599-87e983ee1be5";
    public const string OneAppRedirectAddress = "https://oneapp.hyundai.com/redirect";
    public const string LoginUserAgent =
        "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36_CCS_APP_AOS";

    public const string CciPackageId = "com.hyundai.oneapp.eu";
    public const string CciClientName = "HyundaiEU";
    public const string CciClientVersion = "1.4.0";
    public const string CciClientOsCode = "android";
    public const string CciClientOsVersion = "14";
    public const string CciDeviceModel = "SM-S918B";
    public const string CciNotificationProvider = "FCM";
    public const string CciUserAgent = "okhttp/4.12.0";
    public const string FingerprintKeyPieces = "OsZDfDuCyVX" + "yfqjCoA2JbX" + "2MrLpGwNBC";

    public const string ServiceId = "6d477c38-3ca4-4cf3-9557-2a1929a94654";
    public const string ApplicationId = "014d2225-8495-4735-812d-2616334fd15d";
    public const string StampCipher = "RFtoRq/vDXJmRndoZaZQyfOot7OrIqGVFj96iY2WL3yyH5Z/pUvlUhqmCxD2t+D65SQ=";
    public const string VehicleApiUserAgent = "okhttp/3.12.0";
    public const string PushType = "GCM";

    public const string Language = "en";
    public const string Brand = "Hyundai";
    public const string Region = "eu";
}
