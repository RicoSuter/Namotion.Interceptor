using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Namotion.Interceptor;

/// <summary>
/// Extension methods for <see cref="PropertyInfo"/> that provide enhanced attribute retrieval.
/// </summary>
public static class PropertyInfoExtensions
{
    private static readonly ConcurrentDictionary<PropertyInfo, Attribute[]> Cache = new();

    /// <summary>
    /// Gets custom attributes including inherited attributes from base classes and interfaces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method extends .NET's attribute inheritance to also include interface property attributes.
    /// Attributes are collected in the following order:
    /// </para>
    /// <list type="number">
    ///   <item>Attributes from the class property (including base class inheritance via .NET default behavior)</item>
    ///   <item>Attributes from implemented interface properties (matched by name, in interface declaration order)</item>
    /// </list>
    /// <para>
    /// Deduplication rules mirror .NET's class inheritance behavior:
    /// </para>
    /// <list type="bullet">
    ///   <item>If an attribute type has <c>AllowMultiple=false</c> and already exists, later occurrences are skipped</item>
    ///   <item>If an attribute type has <c>AllowMultiple=true</c>, all occurrences are included</item>
    /// </list>
    /// </remarks>
    /// <param name="property">The property to get attributes from.</param>
    /// <returns>An array of attributes from the property, its base classes, and implemented interfaces.</returns>
    public static Attribute[] GetCustomAttributesWithInterfaceInheritance(this PropertyInfo property)
    {
        return Cache.GetOrAdd(property, static p => GetCustomAttributesWithInterfaceInheritanceCore(p));
    }

    private static Attribute[] GetCustomAttributesWithInterfaceInheritanceCore(PropertyInfo property)
    {
        // 1. Get class attributes WITH class inheritance
        //    Note: Use parameterless GetCustomAttributes() which correctly includes
        //    inherited attributes from base classes
        var classAttributes = property.GetCustomAttributes().Cast<Attribute>().ToList();

        // 2. Get the declaring type to find interfaces
        var declaringType = property.DeclaringType;
        if (declaringType is null)
        {
            return classAttributes.ToArray();
        }

        // 3. Collect interface property attributes
        var interfaceAttributes = new List<Attribute>();
        foreach (var interfaceType in declaringType.GetInterfaces())
        {
            var interfaceProperty = interfaceType.GetProperty(property.Name);
            if (interfaceProperty is null || interfaceProperty.PropertyType != property.PropertyType)
            {
                continue;
            }

            foreach (var attribute in interfaceProperty.GetCustomAttributes().Cast<Attribute>())
            {
                interfaceAttributes.Add(attribute);
            }
        }

        // 4. Merge with AllowMultiple deduplication
        var result = new List<Attribute>(classAttributes);
        var seenTypes = new HashSet<Type>(
            classAttributes
                .Where(attribute => !IsAllowMultiple(attribute.GetType()))
                .Select(attribute => attribute.GetType()));

        foreach (var attribute in interfaceAttributes)
        {
            var attributeType = attribute.GetType();
            if (IsAllowMultiple(attributeType) || seenTypes.Add(attributeType))
            {
                result.Add(attribute);
            }
        }

        return result.ToArray();
    }

    private static readonly ConcurrentDictionary<Type, bool> AllowMultipleCache = new();

    private static bool IsAllowMultiple(Type attributeType)
    {
        return AllowMultipleCache.GetOrAdd(attributeType, static type =>
        {
            var usage = type.GetCustomAttribute<AttributeUsageAttribute>();
            return usage?.AllowMultiple ?? false;
        });
    }
}
