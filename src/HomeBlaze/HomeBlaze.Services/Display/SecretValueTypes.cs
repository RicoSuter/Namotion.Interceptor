using System.Reflection;
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;

namespace HomeBlaze.Services.Display;

/// <summary>
/// Tells the values whose text must not be shown because it can contain a secret configuration value.
/// </summary>
internal static class SecretValueTypes
{
    // Weak, so that the types of an unloaded plugin are not kept alive; the two shared boxes avoid an allocation per type.
    private static readonly ConditionalWeakTable<Type, StrongBox<bool>> HoldsSecretByType = new();
    private static readonly StrongBox<bool> Holds = new(true);
    private static readonly StrongBox<bool> DoesNotHold = new(false);

    /// <summary>
    /// Whether the value is no subject and its type, or the declared type of one of its public properties at any depth,
    /// has a public property marked with <see cref="ConfigurationAttribute.IsSecret"/>, which the text of a record shows.
    /// </summary>
    /// <remarks>
    /// Only declared property types are followed: a property typed as <see cref="object"/>, an interface or a base type
    /// is not inspected for the runtime type it holds, and neither are fields nor <see cref="ValueTuple"/> elements.
    /// </remarks>
    public static bool HoldsSecret(object value) =>
        value is not IInterceptorSubject &&
        HoldsSecretByType.GetValue(value.GetType(), static type => HasSecretMember(type, []) ? Holds : DoesNotHold).Value;

    private static bool HasSecretMember(Type type, HashSet<Type> visited)
    {
        // The text of a subject is its type name. A type that is reached again is being checked or holds no secret.
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || typeof(IInterceptorSubject).IsAssignableFrom(type) ||
            !visited.Add(type))
        {
            return false;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            if (property.GetCustomAttribute<ConfigurationAttribute>(true) is { IsSecret: true } ||
                HasSecretMember(Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType, visited))
            {
                return true;
            }
        }

        return false;
    }
}
