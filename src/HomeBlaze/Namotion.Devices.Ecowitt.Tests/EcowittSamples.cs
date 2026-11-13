using System.Text.Json;
using Namotion.Devices.Ecowitt.Models;

namespace Namotion.Devices.Ecowitt.Tests;

/// <summary>
/// Responses of a GW2000A with two outdoor sensors, of which the WS90 (0x9637) is offline and the WH68 (0x51B)
/// online, and a WH40 rain gauge (0x1C183). The other slots are learning (FFFFFFFF) or disabled (FFFFFFFE).
/// </summary>
internal static class EcowittSamples
{
    public const string SensorsInfoPage1Json = """
        [
            {"img":"wh85","type":"49","name":"Wind & Rain","id":"FFFFFFFF","batt":"9","rssi":"--","signal":"--","idst":"1"},
            {"img":"wh90","type":"48","name":"Temp & Humidity & Solar & Wind & Rain","id":"9637","batt":"9","rssi":"--","signal":"--","idst":"1"},
            {"img":"wh69","type":"0","name":"Temp & Humidity & Solar & Wind & Rain","id":"FFFFFFFF","batt":"9","rssi":"--","signal":"--","idst":"1"},
            {"img":"wh68","type":"1","name":"Solar & Wind","id":"51B","batt":"5","rssi":"-75","signal":"4","idst":"1"},
            {"img":"wh40","type":"3","name":"Rain","id":"1C183","batt":"2","rssi":"-69","signal":"4","idst":"1"},
            {"img":"wn20","type":"70","name":"Rain Mini","id":"FFFFFFFF","batt":"9","rssi":"--","signal":"--","idst":"1"},
            {"img":"wh25","type":"4","name":"Temp & Humidity & Pressure","id":"FFFFFFFE","batt":"9","rssi":"--","signal":"--","idst":"0"},
            {"img":"wh26","type":"5","name":"Temp & Humidity","id":"FFFFFFFE","batt":"9","rssi":"--","signal":"--","idst":"0"},
            {"img":"wn38","type":"71","name":"BGT","id":"FFFFFFFF","batt":"9","rssi":"--","signal":"--","idst":"1"},
            {"img":"wh80","type":"2","name":"Temp & Humidity & Solar & Wind","id":"FFFFFFFF","batt":"9","rssi":"--","signal":"--","idst":"1"},
            {"img":"wh57","type":"26","name":"Lightning","id":"FFFFFFFF","batt":"9","rssi":"--","signal":"--","idst":"1"},
            {"img":"wh45","type":"39","name":"PM25 & PM10 & CO2","id":"FFFFFFFF","batt":"9","rssi":"--","signal":"--","idst":"1"},
            {"img":"wh41","type":"22","name":"PM2.5 CH1","id":"FFFFFFFF","batt":"9","rssi":"--","signal":"--","idst":"1"},
            {"img":"wh41","type":"23","name":"PM2.5 CH2","id":"FFFFFFFF","batt":"9","rssi":"--","signal":"--","idst":"1"},
            {"img":"wh41","type":"24","name":"PM2.5 CH3","id":"FFFFFFFF","batt":"9","rssi":"--","signal":"--","idst":"1"},
            {"img":"wh41","type":"25","name":"PM2.5 CH4","id":"FFFFFFFF","batt":"9","rssi":"--","signal":"--","idst":"1"}
        ]
        """;

    public const string SensorsInfoPage2Json = """
        [
            {"img":"wh55","type":"27","name":"Leak CH1","id":"FFFFFFFF","batt":"9","rssi":"--","signal":"--","idst":"1"},
            {"img":"wh55","type":"28","name":"Leak CH2","id":"FFFFFFFF","batt":"9","rssi":"--","signal":"--","idst":"1"},
            {"img":"wh55","type":"29","name":"Leak CH3","id":"FFFFFFFF","batt":"9","rssi":"--","signal":"--","idst":"1"},
            {"img":"wh55","type":"30","name":"Leak CH4","id":"FFFFFFFF","batt":"9","rssi":"--","signal":"--","idst":"1"},
            {"img":"wh31","type":"6","name":"Temp & Humidity CH1","id":"FFFFFFFE","batt":"9","rssi":"--","signal":"--","idst":"0"},
            {"img":"wh31","type":"7","name":"Temp & Humidity CH2","id":"FFFFFFFE","batt":"9","rssi":"--","signal":"--","idst":"0"},
            {"img":"wh31","type":"8","name":"Temp & Humidity CH3","id":"FFFFFFFE","batt":"9","rssi":"--","signal":"--","idst":"0"},
            {"img":"wh31","type":"9","name":"Temp & Humidity CH4","id":"FFFFFFFE","batt":"9","rssi":"--","signal":"--","idst":"0"},
            {"img":"wh31","type":"10","name":"Temp & Humidity CH5","id":"FFFFFFFE","batt":"9","rssi":"--","signal":"--","idst":"0"},
            {"img":"wh31","type":"11","name":"Temp & Humidity CH6","id":"FFFFFFFE","batt":"9","rssi":"--","signal":"--","idst":"0"},
            {"img":"wh31","type":"12","name":"Temp & Humidity CH7","id":"FFFFFFFE","batt":"9","rssi":"--","signal":"--","idst":"0"},
            {"img":"wh31","type":"13","name":"Temp & Humidity CH8","id":"FFFFFFFE","batt":"9","rssi":"--","signal":"--","idst":"0"},
            {"img":"wh54","type":"66","name":"Lds CH1","id":"FFFFFFFF","batt":"9","rssi":"--","signal":"--","idst":"1"},
            {"img":"wh54","type":"67","name":"Lds CH2","id":"FFFFFFFF","batt":"9","rssi":"--","signal":"--","idst":"1"},
            {"img":"wh54","type":"68","name":"Lds CH3","id":"FFFFFFFF","batt":"9","rssi":"--","signal":"--","idst":"1"},
            {"img":"wh54","type":"69","name":"Lds CH4","id":"FFFFFFFF","batt":"9","rssi":"--","signal":"--","idst":"1"}
        ]
        """;

    // Temperature and humidity come only from the offline WS90, so the gateway reports them as "--".
    public const string LiveDataJson = """
        {
            "common_list": [
                {"id":"0x02","val":"--.-","unit":"C"},
                {"id":"0x07","val":"--"},
                {"id":"3","val":"--.-","unit":"C"},
                {"id":"5","val":"--.--"},
                {"id":"0x03","val":"--.-","unit":"C"},
                {"id":"0x0B","val":"3.3 m/s"},
                {"id":"0x0C","val":"4.1 m/s"},
                {"id":"0x19","val":"11.2 m/s"},
                {"id":"0x15","val":"0.00 Klux"},
                {"id":"0x17","val":"0"},
                {"id":"0x0A","val":"102","battery":"5","voltage":"1.68"},
                {"id":"0x6D","val":"88"}
            ],
            "rain": [
                {"id":"0x0D","val":"5.9 mm"},
                {"id":"0x0E","val":"0.0 mm/Hr"},
                {"id":"0x7D","val":"0.0 mm"},
                {"id":"0x7C","val":"6.0 mm"},
                {"id":"0x10","val":"6.0 mm"},
                {"id":"0x11","val":"6.0 mm"},
                {"id":"0x12","val":"46.8 mm"},
                {"id":"0x13","val":"532.4 mm","battery":"2","voltage":"1.3"}
            ],
            "wh25": [
                {"intemp":"23.2","unit":"C","inhumi":"41%","abs":"950.7 hPa","rel":"950.7 hPa"}
            ]
        }
        """;

    public static EcowittSensorInfo[] ParseSensorsInfo(string page1Json = SensorsInfoPage1Json, string page2Json = SensorsInfoPage2Json) =>
    [
        .. EcowittClient.ParseSensorsInfo(JsonSerializer.Deserialize<JsonElement>(page1Json)),
        .. EcowittClient.ParseSensorsInfo(JsonSerializer.Deserialize<JsonElement>(page2Json))
    ];

    public static EcowittLiveData ParseLiveData() =>
        EcowittClient.ParseLiveData(JsonSerializer.Deserialize<JsonElement>(LiveDataJson));
}
