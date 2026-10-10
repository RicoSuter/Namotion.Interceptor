using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Networking;
using HomeBlaze.Components;
using MudBlazor;
using Namotion.Interceptor;

namespace HomeBlaze.Host.Display;

/// <summary>
/// Extension methods for resolving MudBlazor icons from subject icon names.
/// </summary>
public static class SubjectIconExtensions
{
    private const string DefaultIconName = "Article";

    /// <summary>
    /// Gets the icon name for a subject (e.g., "Folder", "Article").
    /// Use ResolveMudBlazorIcon() to convert to MudBlazor icon.
    /// </summary>
    public static string GetIcon(this IInterceptorSubject subject)
    {
        if (subject is IIconProvider iconProvider && !string.IsNullOrEmpty(iconProvider.IconName))
            return iconProvider.IconName;

        return DefaultIconName;
    }

    /// <summary>
    /// Gets the icon color for a subject.
    /// Returns null if no color is specified.
    /// </summary>
    public static string? GetIconColor(this IInterceptorSubject subject)
    {
        if (subject is IIconProvider iconProvider && iconProvider.IconColor is { } color)
            return color;

        if (subject is IConnectionState { IsConnected: false } ||
            subject is IMonitoredService { Status: ServiceStatus.Error })
        {
            return "Error";
        }

        return null;
    }

    /// <summary>
    /// Resolves an icon name to a MudBlazor icon string via reflection.
    /// </summary>
    public static string ResolveMudBlazorIcon(string iconName) => MudBlazorIcons.Resolve(iconName);

    /// <summary>
    /// Resolves a color name to a MudBlazor Color enum value.
    /// </summary>
    public static Color ResolveMudBlazorColor(string? colorName)
    {
        return colorName switch
        {
            "Success" => Color.Success,
            "Warning" => Color.Warning,
            "Error" => Color.Error,
            "Primary" => Color.Primary,
            "Secondary" => Color.Secondary,
            "Info" => Color.Info,
            "Dark" => Color.Dark,
            "Default" => Color.Default,
            _ => Color.Default
        };
    }
}
