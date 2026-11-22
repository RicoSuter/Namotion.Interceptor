namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// An answer to an SSDP search.
/// </summary>
/// <param name="Location">The absolute address of the device description.</param>
/// <param name="UniqueServiceName">The USN of the answer, or null.</param>
public sealed record SsdpResponse(Uri Location, string? UniqueServiceName);
