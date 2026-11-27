using System.Globalization;
using System.Text.Json;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Converts dictionary keys to their target types.
/// Handles JsonElement deserialization, enum parsing, and general type conversion.
/// </summary>
public static class DictionaryKeyConverter
{
    /// <summary>
    /// Converts a dictionary key to the specified target type.
    /// </summary>
    /// <param name="key">The key to convert.</param>
    /// <param name="targetKeyType">The target type for the key.</param>
    /// <returns>The converted key.</returns>
    /// <exception cref="FormatException">The key does not convert to <paramref name="targetKeyType"/>.</exception>
    /// <exception cref="ArgumentException">The key names no member of the enum <paramref name="targetKeyType"/>.</exception>
    public static object Convert(object key, Type targetKeyType)
    {
        if (targetKeyType.IsInstanceOfType(key))
            return key;

        if (key is JsonElement jsonElement)
            return jsonElement.Deserialize(targetKeyType)
                ?? throw new InvalidOperationException($"Cannot convert null JSON element to dictionary key type '{targetKeyType.Name}'.");

        if (targetKeyType.IsEnum)
            return Enum.Parse(targetKeyType, System.Convert.ToString(key, CultureInfo.InvariantCulture)!, ignoreCase: true);

        return System.Convert.ChangeType(key, targetKeyType, CultureInfo.InvariantCulture);
    }
}
