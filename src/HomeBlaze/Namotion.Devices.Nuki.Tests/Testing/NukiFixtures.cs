namespace Namotion.Devices.Nuki.Tests.Testing;

/// <summary>
/// Responses of a bridge, based on the examples of the Nuki Bridge HTTP API 1.13.2.
/// </summary>
internal static class NukiFixtures
{
    public const string Info = """
        {"bridgeType": 1, "ids": {"hardwareId": 12345678, "serverId": 87654321},
         "versions": {"firmwareVersion": "2.14.0", "wifiFirmwareVersion": "2.2.0"},
         "uptime": 120, "currentTime": "2018-04-01T12:10:11Z", "serverConnected": true, "scanResults": []}
        """;

    public const string SoftwareInfo = """
        {"bridgeType": 2, "ids": {"serverId": 87654321}, "versions": {"appVersion": "1.4.2"},
         "uptime": 60, "currentTime": "2018-04-01T12:10:11Z", "serverConnected": false}
        """;

    public const string List = """
        [{"nukiId": 1, "deviceType": 0, "name": "Home", "firmwareVersion": "2.8.15",
          "lastKnownState": {"mode": 2, "state": 1, "stateName": "locked", "batteryCritical": false,
            "batteryCharging": false, "batteryChargeState": 85, "keypadBatteryCritical": false,
            "doorsensorState": 2, "doorsensorStateName": "door closed", "timestamp": "2018-10-03T06:49:00+00:00"}},
         {"nukiId": 2, "deviceType": 2, "name": "Community door", "firmwareVersion": "1.6.4",
          "lastKnownState": {"mode": 3, "state": 3, "stateName": "rto active", "batteryCritical": false,
            "ringactionTimestamp": "2020-04-27T16:13:00+00:00", "ringactionState": false,
            "timestamp": "2018-10-03T06:49:00+00:00"}}]
        """;

    public const string ActionSucceeded = """{"success": true, "batteryCritical": false}""";

    public const string ActionFailed = """{"success": false, "batteryCritical": false}""";

    /// <summary>A list with one smart lock.</summary>
    public static string SmartLock(int state, int doorSensorState = 2, int deviceType = 0, long nukiId = 1) => $$$"""
        [{"nukiId": {{{nukiId}}}, "deviceType": {{{deviceType}}}, "name": "Home", "firmwareVersion": "3.5.2",
          "lastKnownState": {"mode": 2, "state": {{{state}}}, "batteryCritical": true, "batteryCharging": true,
            "batteryChargeState": 42, "keypadBatteryCritical": true, "doorsensorState": {{{doorSensorState}}},
            "timestamp": "2018-10-03T06:49:00+00:00"}}]
        """;

    /// <summary>A list with one smart lock that the bridge reports no state for.</summary>
    public static string SmartLockWithoutState(long nukiId = 1) => $$$"""
        [{"nukiId": {{{nukiId}}}, "deviceType": 0, "name": "Back door", "firmwareVersion": "3.5.2"}]
        """;

    /// <summary>Joins device lists into one list.</summary>
    public static string Combine(params string[] lists) =>
        "[" + string.Join(",", lists.Select(list => list.Trim().TrimStart('[').TrimEnd(']'))) + "]";

    /// <summary>A list with one Opener.</summary>
    public static string Opener(int state, int mode = 2, bool ringing = false) => $$$"""
        [{"nukiId": 2, "deviceType": 2, "name": "Community door", "firmwareVersion": "1.6.4",
          "lastKnownState": {"mode": {{{mode}}}, "state": {{{state}}}, "batteryCritical": false,
            "ringactionTimestamp": "2020-04-27T16:13:00+00:00", "ringactionState": {{{(ringing ? "true" : "false")}}},
            "timestamp": "2018-10-03T06:49:00+00:00"}}]
        """;
}
