using Microsoft.AspNetCore.Identity;

namespace HomeBlaze.Authorization.Extensions;

/// <summary>
/// Extension methods for IdentityResult to simplify error message formatting.
/// </summary>
public static class IdentityResultExtensions
{
    /// <summary>
    /// Gets a formatted error message from an IdentityResult's errors.
    /// </summary>
    /// <param name="result">The IdentityResult to extract errors from.</param>
    /// <param name="separator">The separator between error messages (default: ", ").</param>
    /// <returns>A string containing all error descriptions joined by the separator.</returns>
    public static string GetErrorMessage(this IdentityResult result, string separator = ", ")
    {
        return string.Join(separator, result.Errors.Select(e => e.Description));
    }
}
