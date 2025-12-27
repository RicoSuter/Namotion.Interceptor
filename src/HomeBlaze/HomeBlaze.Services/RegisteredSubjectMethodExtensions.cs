using System.Collections.Concurrent;
using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Services;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Services;

/// <summary>
/// Extension methods for discovering [Operation] and [Query] methods on subjects.
/// </summary>
public static class RegisteredSubjectMethodExtensions
{
    private static readonly ConcurrentDictionary<Type, SubjectMethodInfo[]> MethodCache = new();

    /// <summary>
    /// Gets all methods (operations and queries) for the subject, ordered by Position.
    /// </summary>
    public static IReadOnlyList<SubjectMethodInfo> GetAllMethods(this RegisteredSubject subject)
    {
        return GetMethods(subject.Subject.GetType())
            .OrderBy(m => m.Position)
            .ToList();
    }

    /// <summary>
    /// Gets all operation methods for the subject, ordered by Position.
    /// </summary>
    public static IReadOnlyList<SubjectMethodInfo> GetOperationMethods(this RegisteredSubject subject)
    {
        return GetMethods(subject.Subject.GetType())
            .Where(m => m.Kind == SubjectMethodKind.Operation)
            .OrderBy(m => m.Position)
            .ToList();
    }

    /// <summary>
    /// Gets all query methods for the subject, ordered by Position.
    /// </summary>
    public static IReadOnlyList<SubjectMethodInfo> GetQueryMethods(this RegisteredSubject subject)
    {
        return GetMethods(subject.Subject.GetType())
            .Where(m => m.Kind == SubjectMethodKind.Query)
            .OrderBy(m => m.Position)
            .ToList();
    }

    private static SubjectMethodInfo[] GetMethods(Type type)
    {
        return MethodCache.GetOrAdd(type, DiscoverMethods);
    }

    private static SubjectMethodInfo[] DiscoverMethods(Type type)
    {
        var methods = new List<SubjectMethodInfo>();
        var discoveredMethods = new HashSet<string>();

        // Discover methods from the type itself
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var methodInfo = TryCreateMethodInfo(method);
            if (methodInfo != null)
            {
                methods.Add(methodInfo);
                discoveredMethods.Add(method.Name);
            }
        }

        // Discover methods from interfaces (for default interface implementations)
        foreach (var interfaceType in type.GetInterfaces())
        {
            foreach (var method in interfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                // Skip if already discovered from the type itself
                if (discoveredMethods.Contains(method.Name))
                    continue;

                var methodInfo = TryCreateMethodInfo(method);
                if (methodInfo != null)
                {
                    methods.Add(methodInfo);
                    discoveredMethods.Add(method.Name);
                }
            }
        }

        return methods.ToArray();
    }

    private static SubjectMethodInfo? TryCreateMethodInfo(MethodInfo method)
    {
        var operationAttribute = method.GetCustomAttribute<OperationAttribute>();
        var queryAttribute = method.GetCustomAttribute<QueryAttribute>();

        SubjectMethodAttribute? attribute = null;
        SubjectMethodKind kind = SubjectMethodKind.Operation;

        if (operationAttribute != null)
        {
            attribute = operationAttribute;
            kind = SubjectMethodKind.Operation;
        }
        else if (queryAttribute != null)
        {
            attribute = queryAttribute;
            kind = SubjectMethodKind.Query;
        }

        if (attribute == null)
            return null;

        var parameters = method.GetParameters()
            .Select(p => new SubjectMethodParameter(p))
            .ToArray();

        var resultType = GetUnwrappedResultType(method.ReturnType);
        return new SubjectMethodInfo(method, attribute, kind, parameters, resultType);
    }

    private static Type? GetUnwrappedResultType(Type returnType)
    {
        // void
        if (returnType == typeof(void))
            return null;

        // Task (no result)
        if (returnType == typeof(Task))
            return null;

        // Task<T> - unwrap to T
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            return returnType.GetGenericArguments()[0];

        // Synchronous return type
        return returnType;
    }
}
