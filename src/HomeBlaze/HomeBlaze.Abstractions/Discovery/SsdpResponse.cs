namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// An answer to an SSDP search.
/// </summary>
/// <param name="Location">The absolute address of the device description.</param>
/// <param name="UniqueServiceName">The USN of the answer, or null.</param>
/// <param name="Headers">
/// The headers of the answer by name, ignoring case, with the values of a repeated header joined by commas. They are
/// capped in number and length, so a header can be missing or its value cut short.
/// </param>
public sealed record SsdpResponse(Uri Location, string? UniqueServiceName, IReadOnlyDictionary<string, string> Headers);
