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

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var operationAttr = method.GetCustomAttribute<OperationAttribute>();
            var queryAttr = method.GetCustomAttribute<QueryAttribute>();

            SubjectMethodAttribute? attribute = null;
            SubjectMethodKind kind = SubjectMethodKind.Operation;

            if (operationAttr != null)
            {
                attribute = operationAttr;
                kind = SubjectMethodKind.Operation;
            }
            else if (queryAttr != null)
            {
                attribute = queryAttr;
                kind = SubjectMethodKind.Query;
            }

            if (attribute == null)
                continue;

            var parameters = method.GetParameters()
                .Select(p => new SubjectMethodParameter(p, ParameterConverter.IsSupported(p.ParameterType)))
                .ToArray();

            var resultType = GetUnwrappedResultType(method.ReturnType);

            methods.Add(new SubjectMethodInfo(method, attribute, kind, parameters, resultType));
        }

        return methods.ToArray();
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
