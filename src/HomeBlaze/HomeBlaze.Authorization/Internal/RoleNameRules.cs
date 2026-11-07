using System.Globalization;
using System.Text;
using Namotion.Interceptor.Authorization;

namespace HomeBlaze.Authorization.Internal;

/// <summary>
/// The rules a custom role's name must follow.
/// </summary>
internal static class RoleNameRules
{
    private const int ZeroWidthNonJoiner = 0x200C;
    private const int ZeroWidthJoiner = 0x200D;

    // A role's name is a segment of its path, where these separate segments and enclose indexes.
    private static readonly char[] RoleNamePathCharacters = ['/', '[', ']'];

    /// <summary>
    /// Gets why <paramref name="name"/> cannot name a custom role, or null, see <see cref="AuthorizationService.GetRoleNameError"/>.
    /// </summary>
    public static string? GetError(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "A role needs a name.";
        }

        if (BuiltInRoles.IsBuiltIn(name))
        {
            return $"The name '{name}' is reserved for a built-in role.";
        }

        if (string.Equals(name, AuthorizationPolicy.SystemRoleName, StringComparison.OrdinalIgnoreCase))
        {
            return $"The name '{name}' is reserved for the system rules.";
        }

        if (name.IndexOfAny(RoleNamePathCharacters) >= 0)
        {
            return $"The name '{name}' cannot contain '/', '[' or ']', because it is part of the role's path.";
        }

        if (HasInvisibleCharacter(name))
        {
            return $"The name '{name}' cannot contain invisible characters, such as a zero-width space or a control character.";
        }

        return null;
    }

    // Such a name looks like another one but differs from it, so that the same name typed elsewhere never matches it. Per
    // code point, as Format characters outside the BMP, such as tag characters, are surrogate pairs. The zero-width non-joiner
    // and joiner are allowed, as Persian words and emoji sequences need them.
    private static bool HasInvisibleCharacter(string name)
    {
        foreach (var rune in name.EnumerateRunes())
        {
            if (rune.Value is ZeroWidthNonJoiner or ZeroWidthJoiner)
            {
                continue;
            }

            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.Control)
            {
                return true;
            }
        }

        return false;
    }
}
