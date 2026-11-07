using System.Buffers;
using System.Text;
using System.Text.Json;
using HomeBlaze.Components.Preferences;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Game.Components;

/// <summary>
/// The graphics settings of a browser as game-render.js applies them; that module defines the presets and the values each
/// setting takes.
/// </summary>
/// <param name="Preset"><c>auto</c>, <c>low</c>, <c>medium</c>, <c>high</c> or <see cref="Custom"/>.</param>
/// <param name="Level">The preset in effect, which is Auto's current level, or <see cref="Custom"/>.</param>
/// <param name="Resolution">Device pixels per CSS pixel, capped at the device's ratio; 0 renders at the device's ratio.</param>
/// <param name="FrameCap">The frames per second while focused; 0 renders every animation frame.</param>
/// <param name="Shadows"><c>off</c>, <c>hard</c> or <c>soft</c>.</param>
/// <param name="Outlines">Whether outlines draw.</param>
/// <param name="AmbientOcclusion">The resolution of the corner shading relative to the render; 0 turns it off.</param>
/// <param name="Multisampling">Whether the scene renders with multisampling.</param>
/// <param name="Sharpen">Whether the light sharpen runs.</param>
/// <param name="Wind">Whether trees sway.</param>
/// <param name="Fade">Whether objects between the camera and the own player fade.</param>
internal sealed record GameGraphics(
    string Preset,
    string Level,
    double Resolution,
    int FrameCap,
    string Shadows,
    bool Outlines,
    double AmbientOcclusion,
    bool Multisampling,
    bool Sharpen,
    bool Wind,
    bool Fade)
{
    /// <summary>
    /// The browser preference that stores the choice.
    /// </summary>
    /// <remarks>
    /// Needs <c>BrowserPreferences</c> registered, which the host does. The host's own keys live in its
    /// <c>BrowserPreferenceKeys</c>; each is part of a cookie name, so renaming one, this included, loses what browsers stored.
    /// </remarks>
    public const string PreferenceKey = "game-graphics";

    /// <summary>
    /// The preset of settings that match no preset.
    /// </summary>
    public const string Custom = "custom";

    /// <summary>
    /// The preset the browser uses when it stored none.
    /// </summary>
    public const string Auto = "auto";

    /// <summary>
    /// The graphics choice the browser stored, as <see cref="ToPreference"/> writes it, or null.
    /// </summary>
    /// <remarks>Call it during the first render of a circuit, which resolves its browser preferences while they still hold
    /// the values of the prerender.</remarks>
    /// <param name="services">The services of the circuit.</param>
    public static string? ReadPreference(IServiceProvider services) =>
        services.GetRequiredService<BrowserPreferences>().Get(PreferenceKey);

    /// <summary>
    /// The choice as the browser preference stores it: the preset alone, or all settings for <see cref="Custom"/>.
    /// </summary>
    public string ToPreference()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("preset", Preset);
            if (Preset == Custom)
            {
                writer.WriteNumber("resolution", Resolution);
                writer.WriteNumber("frameCap", FrameCap);
                writer.WriteString("shadows", Shadows);
                writer.WriteBoolean("outlines", Outlines);
                writer.WriteNumber("ambientOcclusion", AmbientOcclusion);
                writer.WriteBoolean("multisampling", Multisampling);
                writer.WriteBoolean("sharpen", Sharpen);
                writer.WriteBoolean("wind", Wind);
                writer.WriteBoolean("fade", Fade);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
