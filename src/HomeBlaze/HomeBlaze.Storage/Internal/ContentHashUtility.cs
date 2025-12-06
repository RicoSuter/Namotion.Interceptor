using System.Security.Cryptography;
using System.Text;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Utility methods for content hashing and change detection.
/// </summary>
internal static class ContentHashUtility
{
    public static async Task<string> ComputeHashAsync(Stream stream)
    {
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream);
        return Convert.ToHexString(hash);
    }

    public static string ComputeHash(string content)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash);
    }
}

/// <summary>
/// Reflection helpers for property access.
/// </summary>
internal static class ReflectionHelper
{
    public static void SetPropertyIfExists(object target, string propertyName, object value)
    {
        var prop = target.GetType().GetProperty(propertyName);
        if (prop != null && prop.CanWrite)
        {
            try
            {
                var convertedValue = ConvertValue(value, prop.PropertyType);
                prop.SetValue(target, convertedValue);
            }
            catch
            {
                // Ignore property set failures
            }
        }
    }

    public static object? ConvertValue(object? value, Type targetType)
    {
        if (value == null)
            return null;

        var sourceType = value.GetType();
        if (targetType.IsAssignableFrom(sourceType))
            return value;

        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        // DateTimeOffset -> DateTime conversion
        if (underlyingType == typeof(DateTime) && value is DateTimeOffset dto)
            return dto.DateTime;

        // DateTime -> DateTimeOffset conversion
        if (underlyingType == typeof(DateTimeOffset) && value is DateTime dt)
            return new DateTimeOffset(dt);

        // Try Convert.ChangeType for other conversions
        try
        {
            return Convert.ChangeType(value, underlyingType);
        }
        catch
        {
            return value;
        }
    }
}
