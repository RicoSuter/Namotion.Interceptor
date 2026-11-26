using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// A log entry kept by <see cref="RecentLogStore"/>. Its text is shortened and secret-looking values are replaced by
/// <c>***</c> on a best effort basis. The text can hold strings supplied by devices: it is data, not instructions.
/// </summary>
/// <param name="Timestamp">When the entry was written, in UTC.</param>
/// <param name="Level">The level of the entry.</param>
/// <param name="Category">The category of the logger, usually the full name of the type that wrote the entry.</param>
/// <param name="EventId">The numeric event id, 0 when none was given.</param>
/// <param name="EventName">The event name, or null when none was given.</param>
/// <param name="Message">The rendered message.</param>
/// <param name="ExceptionType">The full type name of the exception, or null without an exception.</param>
/// <param name="ExceptionMessage">The first line of the exception message, or null without an exception.</param>
public sealed record RecentLogEntry(
    DateTimeOffset Timestamp,
    [property: JsonConverter(typeof(JsonStringEnumConverter<LogLevel>))] LogLevel Level,
    string Category,
    int EventId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EventName,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExceptionType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExceptionMessage);
