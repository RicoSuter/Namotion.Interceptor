using System.Net.Http.Json;
using System.Text.Json;
using Namotion.Devices.Ecowitt.Models;

namespace Namotion.Devices.Ecowitt;

public class EcowittClient
{
    private readonly HttpClient _httpClient;
    private readonly string _hostAddress;

    public EcowittClient(HttpClient httpClient, string hostAddress)
    {
        _httpClient = httpClient;
        _hostAddress = hostAddress;
    }

    public async Task<EcowittSensorInfo[]> GetSensorsInfoAsync(CancellationToken cancellationToken)
    {
        // Page 1 = outdoor, rain, indoor, channel slots; Page 2 = soil, leaf, temp, PM2.5, CO2, leak, lightning
        using var page1Response = await _httpClient.GetAsync(
            $"http://{_hostAddress}/get_sensors_info?page=1", cancellationToken);
        page1Response.EnsureSuccessStatusCode();
        var page1Json = await page1Response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        using var page2Response = await _httpClient.GetAsync(
            $"http://{_hostAddress}/get_sensors_info?page=2", cancellationToken);
        page2Response.EnsureSuccessStatusCode();
        var page2Json = await page2Response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        var sensors = new List<EcowittSensorInfo>();
        sensors.AddRange(ParseSensorsInfo(page1Json));
        sensors.AddRange(ParseSensorsInfo(page2Json));
        return sensors.ToArray();
    }

    internal static EcowittSensorInfo[] ParseSensorsInfo(JsonElement array)
    {
        var sensors = new List<EcowittSensorInfo>();
        foreach (var item in array.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

            // Skip unpaired (FFFFFFFF) and disabled (FFFFFFFE) sensors
            if (id == null || id == "FFFFFFFF" || id == "FFFFFFFE")
                continue;

            var typeStr = item.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
            if (!int.TryParse(typeStr, out var typeCode))
                continue;

            var rssiStr = item.TryGetProperty("rssi", out var rssiProp) ? rssiProp.GetString() : null;
            var signalStr = item.TryGetProperty("signal", out var signalProp) ? signalProp.GetString() : null;
            var batteryStr = item.TryGetProperty("batt", out var batteryProp) ? batteryProp.GetString() : null;
            var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            var sensorType = item.TryGetProperty("img", out var imgProp) ? imgProp.GetString() : null;

            sensors.Add(new EcowittSensorInfo
            {
                SensorId = $"0x{id}",
                Name = name,
                SensorType = sensorType,
                TypeCode = typeCode,
                Rssi = int.TryParse(rssiStr, out var rssi) ? rssi : null,
                SignalLevel = int.TryParse(signalStr, out var signal) ? signal : null,
                Battery = int.TryParse(batteryStr, out var battery) ? battery : null
            });
        }
        return sensors.ToArray();
    }

    public async Task<EcowittLiveData> GetLiveDataAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            $"http://{_hostAddress}/get_livedata_info", cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ParseLiveData(json);
    }

    public async Task<EcowittVersionInfo> GetVersionAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            $"http://{_hostAddress}/get_version", cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ParseVersionInfo(json);
    }

    internal static EcowittVersionInfo ParseVersionInfo(JsonElement json)
    {
        var versionString = json.TryGetProperty("version", out var version) ? version.GetString() : null;

        // Strip "Version: " prefix if present (e.g. "Version: GW2000A_V3.3.0" → "GW2000A_V3.3.0")
        if (versionString?.StartsWith("Version: ", StringComparison.OrdinalIgnoreCase) == true)
            versionString = versionString["Version: ".Length..];

        return new EcowittVersionInfo
        {
            Version = versionString
        };
    }

    public async Task<EcowittDeviceInfo> GetDeviceInfoAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            $"http://{_hostAddress}/get_device_info", cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ParseDeviceInfo(json);
    }

    internal static EcowittDeviceInfo ParseDeviceInfo(JsonElement json)
    {
        // The device info endpoint varies by firmware. Try known field names.
        var model = json.TryGetProperty("model", out var modelProp) ? modelProp.GetString() : null;

        // Fallback: extract model from apName (e.g. "GW2000A-WIFIE157" → "GW2000A")
        if (model == null && json.TryGetProperty("apName", out var apNameProp))
        {
            var apName = apNameProp.GetString();
            if (apName != null)
            {
                var dashIndex = apName.IndexOf('-');
                model = dashIndex > 0 ? apName[..dashIndex] : apName;
            }
        }

        return new EcowittDeviceInfo
        {
            Model = model,
            StationType = json.TryGetProperty("stationtype", out var stationType) ? stationType.GetString() : null,
            Frequency = json.TryGetProperty("freq", out var frequency) ? frequency.GetString()
                : json.TryGetProperty("rf_freq", out var rfFrequency) ? rfFrequency.GetString() : null
        };
    }

    public async Task<EcowittNetworkInfo> GetNetworkInfoAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            $"http://{_hostAddress}/get_network_info", cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ParseNetworkInfo(json);
    }

    internal static EcowittNetworkInfo ParseNetworkInfo(JsonElement json)
    {
        // Detect WiFi vs Ethernet: wLanFunc indicates WiFi is active
        bool? isWireless = json.TryGetProperty("wLanFunc", out var wlanFunc)
            ? wlanFunc.ValueKind == JsonValueKind.True
            : null;

        // Network info field names differ between WiFi and Ethernet connections
        return new EcowittNetworkInfo
        {
            MacAddress = json.TryGetProperty("mac", out var macAddress) ? macAddress.GetString() : null,
            IpAddress = json.TryGetProperty("ethIP", out var ethernetIpAddress) ? ethernetIpAddress.GetString()
                : json.TryGetProperty("ip", out var ipAddress) ? ipAddress.GetString() : null,
            SubnetMask = json.TryGetProperty("ethMask", out var ethernetMask) ? ethernetMask.GetString()
                : json.TryGetProperty("mask", out var subnetMask) ? subnetMask.GetString() : null,
            Gateway = json.TryGetProperty("ethGateway", out var ethernetGateway) ? ethernetGateway.GetString()
                : json.TryGetProperty("gw", out var gateway) ? gateway.GetString() : null,
            Dns = json.TryGetProperty("ethDNS", out var ethernetDns) ? ethernetDns.GetString()
                : json.TryGetProperty("dns", out var dnsServer) ? dnsServer.GetString() : null,
            IsWireless = isWireless
        };
    }

    internal static EcowittLiveData ParseLiveData(JsonElement root)
    {
        var data = new EcowittLiveData();

        if (root.TryGetProperty("common_list", out var commonList))
            data.Outdoor = ParseOutdoorData(commonList);

        if (root.TryGetProperty("wh25", out var wh25))
            data.Indoor = ParseIndoorData(wh25);

        if (root.TryGetProperty("rain", out var rain))
            data.Rain = ParseRainData(rain);

        if (root.TryGetProperty("piezoRain", out var piezoRain))
            data.PiezoRain = ParseRainData(piezoRain);

        if (root.TryGetProperty("ch_aisle", out var channels))
            data.Channels = ParseChannelData(channels);

        if (root.TryGetProperty("ch_soil", out var soil))
            data.SoilMoisture = ParseSoilData(soil);

        if (root.TryGetProperty("ch_leaf", out var leaf))
            data.LeafWetness = ParseLeafData(leaf);

        if (root.TryGetProperty("ch_temp", out var temp))
            data.Temperatures = ParseTemperatureData(temp);

        if (root.TryGetProperty("ch_pm25", out var pm25))
            data.Pm25 = ParsePm25Data(pm25);

        if (root.TryGetProperty("ch_co2", out var co2))
            data.Co2 = ParseCo2Data(co2);

        if (root.TryGetProperty("ch_leak", out var leak))
            data.Leaks = ParseLeakData(leak);

        if (root.TryGetProperty("lightning", out var lightning))
            data.Lightning = ParseLightningData(lightning);

        return data;
    }

    private static EcowittOutdoorData ParseOutdoorData(JsonElement commonList)
    {
        var outdoor = new EcowittOutdoorData();

        foreach (var item in commonList.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            var val = item.TryGetProperty("val", out var valProp) ? valProp.GetString() : null;
            var unit = item.TryGetProperty("unit", out var unitProp) ? unitProp.GetString() : null;

            // Physical sensor readings use hex IDs; computed values use plain decimal IDs.
            // See Ecowitt.md "common_list ID mapping" for the full table.
            switch (id)
            {
                case "0x02":
                    outdoor.Temperature = EcowittValueParser.ParseTemperature(val, unit);
                    break;
                case "0x07":
                    outdoor.Humidity = EcowittValueParser.ParseHumidity(val);
                    break;
                case "0x03":
                    outdoor.DewPoint = EcowittValueParser.ParseTemperature(val, unit);
                    break;
                case "3":
                    outdoor.FeelsLikeTemperature = EcowittValueParser.ParseTemperature(val, unit);
                    break;
                case "0x0B":
                    outdoor.WindSpeed = EcowittValueParser.ParseWindSpeed(val);
                    break;
                case "0x0C":
                    outdoor.WindGust = EcowittValueParser.ParseWindSpeed(val);
                    break;
                case "0x19":
                    outdoor.MaxDailyGust = EcowittValueParser.ParseWindSpeed(val);
                    break;
                case "0x15":
                    outdoor.Illuminance = EcowittValueParser.ParseIlluminance(val);
                    break;
                case "0x17":
                    outdoor.UvIndex = EcowittValueParser.ParseDecimal(val);
                    break;
                case "0x0A":
                    outdoor.WindDirection = EcowittValueParser.ParseDecimal(val);
                    break;
                case "0x16":
                    outdoor.SolarRadiation = EcowittValueParser.ParseDecimal(val);
                    break;
                case "5":
                    // Vapor Pressure Deficit — always reported in kPa by the gateway
                    outdoor.VaporPressureDeficit = EcowittValueParser.ParseDecimal(val);
                    break;
            }
        }

        return outdoor;
    }

    private static EcowittIndoorData ParseIndoorData(JsonElement wh25)
    {
        var indoor = new EcowittIndoorData();

        // wh25 is an array with a single object
        foreach (var item in wh25.EnumerateArray())
        {
            var unit = item.TryGetProperty("unit", out var unitProp) ? unitProp.GetString() : null;
            var temp = item.TryGetProperty("intemp", out var tempProp) ? tempProp.GetString() : null;
            var humidity = item.TryGetProperty("inhumi", out var humiProp) ? humiProp.GetString() : null;
            var abs = item.TryGetProperty("abs", out var absProp) ? absProp.GetString() : null;
            var rel = item.TryGetProperty("rel", out var relProp) ? relProp.GetString() : null;

            indoor.Temperature = EcowittValueParser.ParseTemperature(temp, unit);
            indoor.Humidity = EcowittValueParser.ParseHumidity(humidity);
            indoor.AbsolutePressure = EcowittValueParser.ParsePressure(abs);
            indoor.RelativePressure = EcowittValueParser.ParsePressure(rel);
        }

        return indoor;
    }

    private static EcowittRainData ParseRainData(JsonElement rainArray)
    {
        var rain = new EcowittRainData();

        foreach (var item in rainArray.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            var val = item.TryGetProperty("val", out var valProp) ? valProp.GetString() : null;

            switch (id)
            {
                case "0x0D":
                    rain.RainEvent = EcowittValueParser.ParseRain(val);
                    break;
                case "0x0E":
                    rain.RainRate = EcowittValueParser.ParseRainRate(val);
                    break;
                case "0x10":
                    rain.HourlyRain = EcowittValueParser.ParseRain(val);
                    break;
                case "0x11":
                    rain.DailyRain = EcowittValueParser.ParseRain(val);
                    break;
                case "0x12":
                    rain.WeeklyRain = EcowittValueParser.ParseRain(val);
                    break;
                case "0x13":
                    rain.MonthlyRain = EcowittValueParser.ParseRain(val);
                    break;
                case "0x14":
                    rain.YearlyRain = EcowittValueParser.ParseRain(val);
                    break;
            }
        }

        // Battery is sometimes a separate item in the rain array
        foreach (var item in rainArray.EnumerateArray())
        {
            if (item.TryGetProperty("battery", out var battProp))
            {
                if (int.TryParse(battProp.GetString(), out var batt))
                    rain.Battery = batt;
            }
        }

        return rain;
    }

    private static EcowittChannelData[] ParseChannelData(JsonElement array)
    {
        var channels = new List<EcowittChannelData>();
        foreach (var item in array.EnumerateArray())
        {
            var channelStr = item.TryGetProperty("channel", out var chProp) ? chProp.GetString() : null;
            if (!int.TryParse(channelStr, out var channel))
                continue;

            var unit = item.TryGetProperty("unit", out var unitProp) ? unitProp.GetString() : null;
            var temp = item.TryGetProperty("temp", out var tempProp) ? tempProp.GetString() : null;
            var humidity = item.TryGetProperty("humidity", out var humiProp) ? humiProp.GetString() : null;
            var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            var battery = item.TryGetProperty("battery", out var battProp) ? battProp.GetString() : null;

            channels.Add(new EcowittChannelData
            {
                Channel = channel,
                Name = name,
                Temperature = EcowittValueParser.ParseTemperature(temp, unit),
                Humidity = EcowittValueParser.ParseHumidity(humidity),
                Battery = int.TryParse(battery, out var batteryValue) ? batteryValue : null
            });
        }
        return channels.ToArray();
    }

    private static EcowittSoilData[] ParseSoilData(JsonElement array)
    {
        var items = new List<EcowittSoilData>();
        foreach (var item in array.EnumerateArray())
        {
            var channelStr = item.TryGetProperty("channel", out var chProp) ? chProp.GetString() : null;
            if (!int.TryParse(channelStr, out var channel))
                continue;

            var humidity = item.TryGetProperty("humidity", out var humiProp) ? humiProp.GetString() : null;
            var battery = item.TryGetProperty("battery", out var battProp) ? battProp.GetString() : null;

            items.Add(new EcowittSoilData
            {
                Channel = channel,
                Moisture = EcowittValueParser.ParseHumidity(humidity),
                Battery = int.TryParse(battery, out var batteryValue) ? batteryValue : null
            });
        }
        return items.ToArray();
    }

    private static EcowittLeafData[] ParseLeafData(JsonElement array)
    {
        var items = new List<EcowittLeafData>();
        foreach (var item in array.EnumerateArray())
        {
            var channelStr = item.TryGetProperty("channel", out var chProp) ? chProp.GetString() : null;
            if (!int.TryParse(channelStr, out var channel))
                continue;

            var wetness = item.TryGetProperty("leaf_wetness", out var wetProp) ? wetProp.GetString() : null;
            var battery = item.TryGetProperty("battery", out var battProp) ? battProp.GetString() : null;

            items.Add(new EcowittLeafData
            {
                Channel = channel,
                Wetness = EcowittValueParser.ParseHumidity(wetness),
                Battery = int.TryParse(battery, out var batteryValue) ? batteryValue : null
            });
        }
        return items.ToArray();
    }

    private static EcowittTemperatureData[] ParseTemperatureData(JsonElement array)
    {
        var items = new List<EcowittTemperatureData>();
        foreach (var item in array.EnumerateArray())
        {
            var channelStr = item.TryGetProperty("channel", out var chProp) ? chProp.GetString() : null;
            if (!int.TryParse(channelStr, out var channel))
                continue;

            var unit = item.TryGetProperty("unit", out var unitProp) ? unitProp.GetString() : null;
            var temp = item.TryGetProperty("temp", out var tempProp) ? tempProp.GetString() : null;
            var battery = item.TryGetProperty("battery", out var battProp) ? battProp.GetString() : null;

            items.Add(new EcowittTemperatureData
            {
                Channel = channel,
                Temperature = EcowittValueParser.ParseTemperature(temp, unit),
                Battery = int.TryParse(battery, out var batteryValue) ? batteryValue : null
            });
        }
        return items.ToArray();
    }

    private static EcowittPm25Data[] ParsePm25Data(JsonElement array)
    {
        var items = new List<EcowittPm25Data>();
        foreach (var item in array.EnumerateArray())
        {
            var channelStr = item.TryGetProperty("channel", out var chProp) ? chProp.GetString() : null;
            if (!int.TryParse(channelStr, out var channel))
                continue;

            var pm25 = item.TryGetProperty("pm25", out var pmProp) ? pmProp.GetString() : null;
            var pm25_24h = item.TryGetProperty("pm25_24h", out var pm24Prop) ? pm24Prop.GetString() : null;
            var battery = item.TryGetProperty("battery", out var battProp) ? battProp.GetString() : null;

            items.Add(new EcowittPm25Data
            {
                Channel = channel,
                Pm25 = EcowittValueParser.ParseDecimal(pm25),
                Pm25Avg24h = EcowittValueParser.ParseDecimal(pm25_24h),
                Battery = int.TryParse(battery, out var batteryValue) ? batteryValue : null
            });
        }
        return items.ToArray();
    }

    private static EcowittCo2Data[] ParseCo2Data(JsonElement array)
    {
        var items = new List<EcowittCo2Data>();
        foreach (var item in array.EnumerateArray())
        {
            var channelStr = item.TryGetProperty("channel", out var chProp) ? chProp.GetString() : null;
            if (!int.TryParse(channelStr, out var channel))
                continue;

            var co2 = item.TryGetProperty("co2", out var co2Prop) ? co2Prop.GetString() : null;
            var co2_24h = item.TryGetProperty("co2_24h", out var co224Prop) ? co224Prop.GetString() : null;
            var unit = item.TryGetProperty("unit", out var unitProp) ? unitProp.GetString() : null;
            var temp = item.TryGetProperty("temp", out var tempProp) ? tempProp.GetString() : null;
            var humidity = item.TryGetProperty("humidity", out var humiProp) ? humiProp.GetString() : null;
            var battery = item.TryGetProperty("battery", out var battProp) ? battProp.GetString() : null;

            items.Add(new EcowittCo2Data
            {
                Channel = channel,
                Co2 = EcowittValueParser.ParseDecimal(co2),
                Co2Avg24h = EcowittValueParser.ParseDecimal(co2_24h),
                Temperature = EcowittValueParser.ParseTemperature(temp, unit),
                Humidity = EcowittValueParser.ParseHumidity(humidity),
                Battery = int.TryParse(battery, out var batteryValue) ? batteryValue : null
            });
        }
        return items.ToArray();
    }

    private static EcowittLeakData[] ParseLeakData(JsonElement array)
    {
        var items = new List<EcowittLeakData>();
        foreach (var item in array.EnumerateArray())
        {
            var channelStr = item.TryGetProperty("channel", out var chProp) ? chProp.GetString() : null;
            if (!int.TryParse(channelStr, out var channel))
                continue;

            var leak = item.TryGetProperty("leak", out var leakProp) ? leakProp.GetString() : null;
            var battery = item.TryGetProperty("battery", out var battProp) ? battProp.GetString() : null;

            items.Add(new EcowittLeakData
            {
                Channel = channel,
                IsLeaking = leak == "1",
                Battery = int.TryParse(battery, out var batteryValue) ? batteryValue : null
            });
        }
        return items.ToArray();
    }

    private static EcowittLightningData ParseLightningData(JsonElement element)
    {
        var distance = element.TryGetProperty("distance", out var distProp) ? distProp.GetString() : null;
        var count = element.TryGetProperty("count", out var countProp) ? countProp.GetString() : null;
        var timestamp = element.TryGetProperty("timestamp", out var tsProp) ? tsProp.GetString() : null;
        var battery = element.TryGetProperty("battery", out var battProp) ? battProp.GetString() : null;

        DateTimeOffset? lastStrikeTime = null;
        if (timestamp != null && timestamp != "--" && long.TryParse(timestamp, out var unixSeconds) && unixSeconds > 0)
            lastStrikeTime = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

        return new EcowittLightningData
        {
            Distance = EcowittValueParser.ParseDecimal(distance),
            StrikeCount = int.TryParse(count, out var c) ? c : null,
            LastStrikeTime = lastStrikeTime,
            Battery = int.TryParse(battery, out var batteryValue) ? batteryValue : null
        };
    }
}
