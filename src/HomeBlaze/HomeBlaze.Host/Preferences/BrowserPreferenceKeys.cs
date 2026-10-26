namespace HomeBlaze.Host.Preferences;

/// <summary>
/// Keys of the preferences a browser remembers. Each is part of a cookie name, so renaming one loses what browsers stored.
/// </summary>
internal static class BrowserPreferenceKeys
{
    /// <summary>Whether the navigation drawer is open.</summary>
    public const string Drawer = "drawer";

    /// <summary>The row layout of the property panel.</summary>
    public const string RowLayout = "rowlayout";

    /// <summary>The display time zone.</summary>
    public const string TimeZone = "timezone";

    /// <summary>The secret token of the named client this browser belongs to.</summary>
    public const string ClientToken = "client-token";
}
