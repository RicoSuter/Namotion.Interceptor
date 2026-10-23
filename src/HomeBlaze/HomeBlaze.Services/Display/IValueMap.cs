using HomeBlaze.Components.Abstractions.TimeZones;

namespace HomeBlaze.Services.Display;

/// <summary>A map from a bound value to a text, such as a value map of a page, for the pipe <c>map</c> of an expression.</summary>
public interface IValueMap
{
    /// <summary>
    /// Gets what is wrong with the map, such as a case with a condition that cannot be parsed, or null when the map is
    /// fine. A map with an error is not applied.
    /// </summary>
    string? GetError();

    /// <summary>
    /// Gets the text of the first case that matches the binding, with its expressions evaluated, or null when no case
    /// matches or the matching case has no text.
    /// </summary>
    /// <param name="binding">The value and its subject.</param>
    /// <param name="resolver">The resolver of the paths of the cases.</param>
    /// <param name="timeZone">The time zone of the viewer for dates, or null for that of the server.</param>
    string? GetText(in ValueBinding binding, SubjectPathResolver resolver, ITimeZoneDisplay? timeZone);
}
