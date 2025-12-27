using System.Text.Json;
using HomeBlaze.Abstractions.Authorization;
using HomeBlaze.Abstractions.Services;
using HomeBlaze.Authorization.Data;
using Namotion.Interceptor;

namespace HomeBlaze.Authorization.Services;

/// <summary>
/// Serialization provider for authorization permission overrides.
/// Stores and restores permission overrides in the "$authorization" JSON property.
/// </summary>
public class AuthorizationDataProvider : IAdditionalPropertiesSerializer
{
    /// <summary>
    /// Prefix for authorization data keys in Subject.Data.
    /// Format: "HomeBlaze.Authorization:{Entity}:{Action}"
    /// </summary>
    public const string DataKeyPrefix = "HomeBlaze.Authorization:";

    /// <summary>
    /// JSON property name (without $).
    /// </summary>
    private const string PropertyKey = "authorization";

    public Dictionary<string, object>? GetAdditionalProperties(IInterceptorSubject subject)
    {
        if (subject?.Data == null)
        {
            return null;
        }

        // Structure: { "path": { "Kind:Action": { inherit, roles } } }
        var permissions = new Dictionary<string, Dictionary<string, AuthorizationOverride>>();

        // Collect permissions from this subject
        CollectSubjectPermissions(subject, "", permissions);

        return permissions.Count > 0
            ? new Dictionary<string, object> { [PropertyKey] = permissions }
            : null;
    }

    public void SetAdditionalProperties(
        IInterceptorSubject subject,
        Dictionary<string, JsonElement>? properties)
    {
        if (properties == null || !properties.TryGetValue(PropertyKey, out var element))
        {
            return;
        }

        var overrides = element.Deserialize<Dictionary<string, Dictionary<string, AuthorizationOverride?>?>>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (overrides == null)
        {
            return;
        }

        foreach (var (path, overrideMap) in overrides)
        {
            if (overrideMap == null)
            {
                continue;
            }

            // "" in JSON = subject-level (maps to null property name in Subject.Data)
            string? propertyName = string.IsNullOrEmpty(path) ? null : path;

            foreach (var (kindAction, authOverride) in overrideMap)
            {
                if (authOverride != null)
                {
                    var dataKey = $"{DataKeyPrefix}{kindAction}";
                    subject.Data[(propertyName, dataKey)] = authOverride;
                }
            }
        }
    }

    private void CollectSubjectPermissions(
        IInterceptorSubject subject,
        string pathPrefix,
        Dictionary<string, Dictionary<string, AuthorizationOverride>> result)
    {
        foreach (var kvp in subject.Data)
        {
            if (kvp.Key.key == null || !kvp.Key.key.StartsWith(DataKeyPrefix))
            {
                continue;
            }

            if (kvp.Value is not AuthorizationOverride authOverride)
            {
                continue;
            }

            // Extract Entity:Action from key
            var entityAction = kvp.Key.key[DataKeyPrefix.Length..];

            // Subject.Data uses null for subject-level, convert to "" for JSON
            var propertyName = kvp.Key.property ?? "";

            // Build full path
            var fullPath = string.IsNullOrEmpty(pathPrefix)
                ? propertyName
                : $"{pathPrefix}/{propertyName}";

            if (!result.TryGetValue(fullPath, out var permDict))
            {
                permDict = new Dictionary<string, AuthorizationOverride>();
                result[fullPath] = permDict;
            }

            permDict[entityAction] = authOverride;
        }
    }

    /// <summary>
    /// Gets the data key for a specific AuthorizationEntity and Action combination.
    /// </summary>
    public static string GetDataKey(AuthorizationEntity entity, AuthorizationAction action)
    {
        return $"{DataKeyPrefix}{entity}:{action}";
    }

    /// <summary>
    /// Tries to get an authorization override from a subject's data.
    /// </summary>
    public static bool TryGetOverride(
        IInterceptorSubject subject,
        string? propertyName,
        AuthorizationEntity entity,
        AuthorizationAction action,
        out AuthorizationOverride? authOverride)
    {
        authOverride = null;
        var key = GetDataKey(entity, action);

        if (subject.Data.TryGetValue((propertyName, key), out var value) &&
            value is AuthorizationOverride ao)
        {
            authOverride = ao;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Sets an authorization override in a subject's data.
    /// </summary>
    public static void SetOverride(
        IInterceptorSubject subject,
        string? propertyName,
        AuthorizationEntity entity,
        AuthorizationAction action,
        AuthorizationOverride authOverride)
    {
        var key = GetDataKey(entity, action);
        subject.Data[(propertyName, key)] = authOverride;
    }

    /// <summary>
    /// Removes an authorization override from a subject's data.
    /// </summary>
    public static void RemoveOverride(
        IInterceptorSubject subject,
        string? propertyName,
        AuthorizationEntity entity,
        AuthorizationAction action)
    {
        var key = GetDataKey(entity, action);
        subject.Data.TryRemove((propertyName, key), out _);
    }
}
