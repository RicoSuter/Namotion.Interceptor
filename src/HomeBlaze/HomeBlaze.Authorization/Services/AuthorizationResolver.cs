using System.Collections.Concurrent;
using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Authorization;
using HomeBlaze.Authorization.Configuration;
using HomeBlaze.Authorization.Data;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Authorization.Services;

/// <summary>
/// Resolves authorization requirements using a priority chain:
/// 1. Property runtime override (Subject.Data)
/// 2. Subject runtime override (Subject.Data)
/// 3. Property [SubjectPropertyAuthorize] attribute
/// 4. Subject [SubjectAuthorize] attribute
/// 5. Parent traversal (OR combine)
/// 6. Global defaults
/// </summary>
public class AuthorizationResolver : IAuthorizationResolver
{

    private readonly AuthorizationOptions _options;

    // Cache for resolved permissions: (subject, propertyName, entity, action) → roles
    private readonly ConcurrentDictionary<(IInterceptorSubject, string?, AuthorizationEntity, AuthorizationAction), string[]> _cache = new();

    // Cache for attribute lookups per type
    private readonly ConcurrentDictionary<Type, SubjectAuthorizeAttribute[]> _subjectAttributeCache = new();
    private readonly ConcurrentDictionary<(Type, string), SubjectPropertyAuthorizeAttribute[]> _propertyAttributeCache = new();
    private readonly ConcurrentDictionary<(Type, string), SubjectMethodAuthorizeAttribute?> _methodAttributeCache = new();

    public AuthorizationResolver(AuthorizationOptions options)
    {
        _options = options;
    }

    public string[] ResolvePropertyRoles(PropertyReference property, AuthorizationEntity entity, AuthorizationAction action)
    {
        var cacheKey = (property.Subject, property.Name, entity, action);
        return _cache.GetOrAdd(cacheKey, _ => ResolvePropertyRolesCore(property, entity, action));
    }

    public string[] ResolveSubjectRoles(IInterceptorSubject subject, AuthorizationEntity entity, AuthorizationAction action)
    {
        var cacheKey = (subject, (string?)null, entity, action);
        return _cache.GetOrAdd(cacheKey, _ => ResolveSubjectRolesCore(subject, entity, action));
    }

    public string[] ResolveMethodRoles(IInterceptorSubject subject, AuthorizationEntity entity, string methodName)
    {
        var cacheKey = (subject, methodName, entity, AuthorizationAction.Invoke);
        return _cache.GetOrAdd(cacheKey, _ => ResolveMethodRolesCore(subject, methodName, entity));
    }

    public void InvalidateCache(IInterceptorSubject subject)
    {
        // Remove all cache entries for this subject
        var keysToRemove = _cache.Keys.Where(k => ReferenceEquals(k.Item1, subject)).ToList();
        foreach (var key in keysToRemove)
        {
            _cache.TryRemove(key, out _);
        }
    }

    private string[] ResolvePropertyRolesCore(PropertyReference property, AuthorizationEntity entity, AuthorizationAction action)
    {
        // 1. Property runtime override - always wins for this subject
        var dataKey = AuthorizationDataProvider.GetDataKey(entity, action);
        if (property.TryGetPropertyData(dataKey, out var overrideObj) && overrideObj is AuthorizationOverride authOverride)
        {
            return authOverride.Roles;
        }

        // 2. Subject runtime override - always wins for this subject
        if (property.Subject.Data.TryGetValue((null, dataKey), out var subjectOverrideObj) && subjectOverrideObj is AuthorizationOverride subjectOverride)
        {
            return subjectOverride.Roles;
        }

        return ResolveFromAttributesAndParents(property.Subject, property.Name, entity, action);
    }

    private string[] ResolveFromAttributesAndParents(IInterceptorSubject subject, string? propertyName, AuthorizationEntity entity, AuthorizationAction action)
    {
        // 3. Property [SubjectPropertyAuthorize] attribute (if property specified)
        if (propertyName != null)
        {
            var propertyAttributes = GetPropertyAttributes(subject.GetType(), propertyName);
            var matchingPropAttr = propertyAttributes.FirstOrDefault(a => a.Action == action);
            if (matchingPropAttr != null)
            {
                return matchingPropAttr.Roles;
            }
        }

        // 4. Subject [SubjectAuthorize] attribute
        var subjectAttributes = GetSubjectAttributes(subject.GetType());
        var matchingSubjectAttr = subjectAttributes.FirstOrDefault(a => a.Entity == entity && a.Action == action);
        if (matchingSubjectAttr != null)
        {
            return matchingSubjectAttr.Roles;
        }

        // 5. Parent traversal (OR combine - collect from all parents)
        var parentRoles = TraverseParents(subject, entity, action, new HashSet<IInterceptorSubject>());
        if (parentRoles.Length > 0)
        {
            return parentRoles;
        }

        // 6. Global defaults - clone to ensure cache key uniqueness
        return [.. _options.GetDefaultRoles(entity, action)];
    }

    private string[] ResolveSubjectRolesCore(IInterceptorSubject subject, AuthorizationEntity entity, AuthorizationAction action)
    {
        // Subject runtime override - always wins for this subject
        var dataKey = AuthorizationDataProvider.GetDataKey(entity, action);
        if (subject.Data.TryGetValue((null, dataKey), out var overrideObj) && overrideObj is AuthorizationOverride authOverride)
        {
            return authOverride.Roles;
        }

        return ResolveFromAttributesAndParents(subject, null, entity, action);
    }

    private string[] ResolveMethodRolesCore(IInterceptorSubject subject, string methodName, AuthorizationEntity entity)
    {
        var dataKey = AuthorizationDataProvider.GetDataKey(entity, AuthorizationAction.Invoke);

        // 1. Method runtime override - always wins for this subject
        if (subject.Data.TryGetValue((methodName, dataKey), out var overrideObj) && overrideObj is AuthorizationOverride methodOverride)
        {
            return methodOverride.Roles;
        }

        // 2. Subject runtime override - always wins for this subject
        if (subject.Data.TryGetValue((null, dataKey), out var subjectOverrideObj) && subjectOverrideObj is AuthorizationOverride subjectOverride)
        {
            return subjectOverride.Roles;
        }

        return ResolveMethodFromAttributesAndParents(subject, methodName, entity);
    }

    private string[] ResolveMethodFromAttributesAndParents(IInterceptorSubject subject, string methodName, AuthorizationEntity entity)
    {
        // 3. Method [SubjectMethodAuthorize] attribute
        var methodAttr = GetMethodAttribute(subject.GetType(), methodName);
        if (methodAttr != null)
        {
            return methodAttr.Roles;
        }

        // 4. Subject [SubjectAuthorize] attribute for methods
        var subjectAttributes = GetSubjectAttributes(subject.GetType());
        var matchingSubjectAttr = subjectAttributes.FirstOrDefault(a => a.Entity == entity && a.Action == AuthorizationAction.Invoke);
        if (matchingSubjectAttr != null)
        {
            return matchingSubjectAttr.Roles;
        }

        // 5. Parent traversal
        var parentRoles = TraverseParentsForMethod(subject, entity, new HashSet<IInterceptorSubject>());
        if (parentRoles.Length > 0)
        {
            return parentRoles;
        }

        // 6. Global defaults - clone to ensure cache key uniqueness
        return [.. _options.GetDefaultRoles(entity, AuthorizationAction.Invoke)];
    }

    private string[] TraverseParents(IInterceptorSubject subject, AuthorizationEntity entity, AuthorizationAction action, HashSet<IInterceptorSubject> visited)
    {
        if (!visited.Add(subject))
        {
            return []; // Circular reference detected
        }

        var parents = subject.GetParents();
        if (parents.Count == 0)
        {
            return [];
        }

        var allRoles = new HashSet<string>();
        var dataKey = AuthorizationDataProvider.GetDataKey(entity, action);

        foreach (var parent in parents)
        {
            var parentSubject = parent.Property.Subject;

            // 1. Check parent's runtime override - only if Inherit = true
            if (parentSubject.Data.TryGetValue((null, dataKey), out var overrideObj) &&
                overrideObj is AuthorizationOverride authOverride)
            {
                if (authOverride.Inherit)
                {
                    // Parent has inheritable override - use it, stop this branch
                    foreach (var role in authOverride.Roles)
                    {
                        allRoles.Add(role);
                    }
                    continue; // Don't traverse further up this branch
                }
                // Inherit = false means children don't see this override, continue to check attributes/grandparents
            }

            // 2. Check parent's [SubjectAuthorize] attribute
            var subjectAttributes = GetSubjectAttributes(parentSubject.GetType());
            var matchingAttr = subjectAttributes.FirstOrDefault(a => a.Entity == entity && a.Action == action);
            if (matchingAttr != null)
            {
                // Parent has attribute - use it, stop this branch
                foreach (var role in matchingAttr.Roles)
                {
                    allRoles.Add(role);
                }
                continue; // Don't traverse further up this branch
            }

            // 3. No roles on parent - continue up this branch to grandparents
            var ancestorRoles = TraverseParents(parentSubject, entity, action, visited);
            foreach (var role in ancestorRoles)
            {
                allRoles.Add(role);
            }
        }

        return allRoles.ToArray();
    }

    private string[] TraverseParentsForMethod(IInterceptorSubject subject, AuthorizationEntity entity, HashSet<IInterceptorSubject> visited)
    {
        // Methods always use Invoke action - delegate to common traversal
        return TraverseParents(subject, entity, AuthorizationAction.Invoke, visited);
    }

    private SubjectAuthorizeAttribute[] GetSubjectAttributes(Type type)
    {
        return _subjectAttributeCache.GetOrAdd(type, t =>
            t.GetCustomAttributes<SubjectAuthorizeAttribute>(inherit: true).ToArray());
    }

    private SubjectPropertyAuthorizeAttribute[] GetPropertyAttributes(Type type, string propertyName)
    {
        return _propertyAttributeCache.GetOrAdd((type, propertyName), key =>
        {
            var property = key.Item1.GetProperty(key.Item2);
            return property?.GetCustomAttributes<SubjectPropertyAuthorizeAttribute>(inherit: true).ToArray() ?? [];
        });
    }

    private SubjectMethodAuthorizeAttribute? GetMethodAttribute(Type type, string methodName)
    {
        return _methodAttributeCache.GetOrAdd((type, methodName), key =>
        {
            // Only consider methods marked with [Query] or [Operation] (SubjectMethodAttribute)
            // This avoids AmbiguousMatchException for overloaded methods like StartAsync
            var methods = key.Item1.GetMethods()
                .Where(m => m.Name == key.Item2 &&
                            m.GetCustomAttribute<SubjectMethodAttribute>(inherit: true) != null);

            foreach (var method in methods)
            {
                var attr = method.GetCustomAttribute<SubjectMethodAuthorizeAttribute>(inherit: true);
                if (attr != null)
                {
                    return attr;
                }
            }
            return null;
        });
    }
}
