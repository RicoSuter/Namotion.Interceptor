using MudBlazor;

namespace HomeBlaze.Authorization.Blazor.Utilities;

/// <summary>
/// Utility class for calculating and displaying password strength indicators.
/// </summary>
public static class PasswordStrengthUtility
{
    /// <summary>
    /// Minimum required password length.
    /// This should match the server-side configuration in IdentityServiceExtensions.
    /// </summary>
    public const int MinimumLength = 8;

    /// <summary>
    /// Validates if a password meets the minimum length requirement.
    /// </summary>
    /// <param name="password">The password to validate.</param>
    /// <returns>True if the password is valid, false otherwise.</returns>
    public static bool IsValid(string? password)
    {
        return !string.IsNullOrEmpty(password) && password.Length >= MinimumLength;
    }

    /// <summary>
    /// Gets helper text for password input fields.
    /// </summary>
    /// <param name="password">The current password value.</param>
    /// <returns>Helper text describing requirements or strength.</returns>
    public static string GetHelperText(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return $"Minimum {MinimumLength} characters";
        }

        if (password.Length < MinimumLength)
        {
            return $"Need {MinimumLength - password.Length} more character(s)";
        }

        return GetStrengthText(password);
    }

    /// <summary>
    /// Calculates the strength score of a password based on various criteria.
    /// </summary>
    /// <param name="password">The password to evaluate.</param>
    /// <returns>A score from 0 (empty/weak) to 6 (very strong).</returns>
    public static int CalculateStrength(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return 0;
        }

        var strength = 0;
        if (password.Length >= 8) strength++;
        if (password.Length >= 12) strength++;
        if (password.Any(char.IsUpper)) strength++;
        if (password.Any(char.IsLower)) strength++;
        if (password.Any(char.IsDigit)) strength++;
        if (password.Any(c => !char.IsLetterOrDigit(c))) strength++;
        return strength;
    }

    /// <summary>
    /// Gets a human-readable description of the password strength.
    /// </summary>
    /// <param name="password">The password to evaluate.</param>
    /// <returns>A string describing the password strength.</returns>
    public static string GetStrengthText(string? password)
    {
        var strength = CalculateStrength(password);
        if (strength < 2) return "Weak - add numbers or symbols";
        if (strength < 3) return "Fair - add uppercase letters";
        if (strength < 4) return "Good";
        return "Strong";
    }

    /// <summary>
    /// Gets a MudBlazor Color representing the password strength.
    /// </summary>
    /// <param name="password">The password to evaluate.</param>
    /// <returns>A MudBlazor Color indicating strength level.</returns>
    public static Color GetStrengthColor(string? password)
    {
        var strength = CalculateStrength(password);
        if (strength < 2) return Color.Error;
        if (strength < 3) return Color.Warning;
        if (strength < 4) return Color.Info;
        return Color.Success;
    }

    /// <summary>
    /// Gets a percentage value (0-100) for progress bar display.
    /// </summary>
    /// <param name="password">The password to evaluate.</param>
    /// <returns>A percentage from 0 to 100.</returns>
    public static int GetStrengthPercentage(string? password)
    {
        var strength = CalculateStrength(password);
        return Math.Min(100, strength * 17); // 6 * 17 ≈ 102, capped at 100
    }
}
