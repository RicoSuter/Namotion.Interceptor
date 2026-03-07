using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.TwinCAT;

/// <summary>
/// Handles type conversion between ADS/PLC types and .NET types.
/// Override virtual methods for custom type mappings.
/// </summary>
public class AdsValueConverter
{
    /// <summary>
    /// Converts a value received from the PLC to a .NET property value.
    /// Override for custom type mappings.
    /// </summary>
    /// <param name="adsValue">The value received from the PLC.</param>
    /// <param name="property">The target property descriptor.</param>
    /// <returns>The converted .NET property value.</returns>
    public virtual object? ConvertToPropertyValue(object? adsValue, RegisteredSubjectProperty property)
    {
        if (adsValue is null)
            return null;

        var targetType = property.Type;

        // Handle DATE_AND_TIME -> DateTimeOffset (PLC DateTime values are assumed UTC)
        if (targetType == typeof(DateTimeOffset) && adsValue is DateTime dateTime)
            return new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc), TimeSpan.Zero);

        return adsValue;
    }

    /// <summary>
    /// Converts a .NET property value to an ADS-compatible value for writing to the PLC.
    /// Override for custom type mappings.
    /// </summary>
    /// <param name="propertyValue">The .NET property value to convert.</param>
    /// <param name="property">The source property descriptor.</param>
    /// <returns>The converted ADS-compatible value.</returns>
    public virtual object? ConvertToAdsValue(object? propertyValue, RegisteredSubjectProperty property)
    {
        if (propertyValue is null)
            return null;

        // Handle DateTimeOffset -> DateTime for DATE_AND_TIME
        if (propertyValue is DateTimeOffset dateTimeOffset)
            return dateTimeOffset.UtcDateTime;

        return propertyValue;
    }
}
