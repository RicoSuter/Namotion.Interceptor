using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Lifecycle;

/// <summary>
/// Lifecycle handler that creates virtual properties for [Operation] and [Query] methods.
/// Each method is stored as a MethodMetadata value with full metadata and InvokeAsync capability.
/// </summary>
public class MethodPropertyInitializer : ILifecycleHandler
{
    [ThreadStatic]
    private static NullabilityInfoContext? _nullabilityContext;

    private static NullabilityInfoContext NullabilityContext => _nullabilityContext ??= new();

    // Per subject type, as the class method that implements an interface method decides. Weak, so that the types of
    // an unloaded plugin are not kept alive.
    private static readonly ConditionalWeakTable<Type, ConcurrentDictionary<MethodInfo, string?>> CategoriesBySubjectType = new();

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.IsContextAttach)
        {
            var registeredSubject = change.Subject.TryGetRegisteredSubject()
                ?? throw new InvalidOperationException("Subject not registered");

            var type = change.Subject.GetType();
            var discoveredMethods = new HashSet<string>();

            // Discover methods from the concrete type
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                TryRegisterMethod(registeredSubject, change.Subject, method, discoveredMethods);
            }

            // Discover methods from interfaces (for default interface implementations or attribute on interface)
            foreach (var interfaceType in type.GetInterfaces())
            {
                foreach (var method in interfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    TryRegisterMethod(registeredSubject, change.Subject, method, discoveredMethods);
                }
            }
        }
    }

    private static void TryRegisterMethod(
        Namotion.Interceptor.Registry.Abstractions.RegisteredSubject registeredSubject,
        Namotion.Interceptor.IInterceptorSubject subject,
        MethodInfo method,
        HashSet<string> discoveredMethods)
    {
        // Skip if already discovered — deduplicates by name (overloaded methods register only once)
        if (discoveredMethods.Contains(method.Name))
            return;

        var operationAttribute = method.GetCustomAttribute<OperationAttribute>();
        var queryAttribute = method.GetCustomAttribute<QueryAttribute>();

        SubjectMethodAttribute? attribute = null;
        MethodKind kind;

        if (operationAttribute != null)
        {
            attribute = operationAttribute;
            kind = MethodKind.Operation;
        }
        else if (queryAttribute != null)
        {
            attribute = queryAttribute;
            kind = MethodKind.Query;
        }
        else
        {
            return;
        }

        discoveredMethods.Add(method.Name);

        var propertyName = method.Name.EndsWith("Async", StringComparison.Ordinal)
            ? method.Name[..^5]
            : method.Name;

        if (registeredSubject.TryGetProperty(propertyName)?.GetValue() is MethodMetadata)
        {
            return;
        }

        var parameters = method.GetParameters()
            .Select(parameter =>
            {
                var operationParameterAttribute = parameter.GetCustomAttribute<OperationParameterAttribute>();
                var nullabilityInfo = NullabilityContext.Create(parameter);
                var isNullable = Nullable.GetUnderlyingType(parameter.ParameterType) != null
                    || nullabilityInfo.WriteState == NullabilityState.Nullable
                    || parameter is { HasDefaultValue: true, DefaultValue: null };
                
                return new MethodParameter
                {
                    Name = parameter.Name ?? $"arg{parameter.Position}",
                    Type = parameter.ParameterType,
                    Unit = operationParameterAttribute?.Unit,
                    IsFromServices = parameter.GetCustomAttribute<FromServicesAttribute>() != null,
                    IsRuntimeProvided = parameter.ParameterType == typeof(CancellationToken),
                    IsNullable = isNullable,
                };
            })
            .ToArray();

        var resultType = GetUnwrappedResultType(method.ReturnType);
        var requiresConfirmation = attribute is OperationAttribute { RequiresConfirmation: true };

        var metadata = new MethodMetadata(subject, method)
        {
            Kind = kind,
            PropertyName = propertyName,
            Title = attribute.Title ?? propertyName,
            Description = attribute.Description,
            Icon = attribute.Icon,
            Position = attribute.Position,
            Category = GetCategory(subject.GetType(), method),
            RequiresConfirmation = requiresConfirmation,
            Parameters = parameters,
            ResultType = resultType,
        };

        registeredSubject.AddProperty(
            propertyName,
            typeof(MethodMetadata),
            _ => metadata,
            null,
            [.. method.GetCustomAttributes<Attribute>()]);
    }

    private static string? GetCategory(Type subjectType, MethodInfo method)
    {
        return CategoriesBySubjectType
            .GetValue(subjectType, static _ => new ConcurrentDictionary<MethodInfo, string?>())
            .GetOrAdd(method, static (method, subjectType) => FindCategory(subjectType, method), subjectType);
    }

    private static string? FindCategory(Type subjectType, MethodInfo method)
    {
        var parameterTypes = Array.ConvertAll(method.GetParameters(), static parameter => parameter.ParameterType);
        var isInterfaceMethod = method.DeclaringType is { IsInterface: true };

        // An explicit implementation has another name and is not found, so the interface method decides for it.
        var classMethod = isInterfaceMethod
            ? FindMethod(subjectType, method.Name, parameterTypes)
            : method;

        // An attribute decides also when its name is empty, which is how a class method opts out.
        if (classMethod?.GetCustomAttribute<CategoryAttribute>() is { } classCategory)
        {
            return classCategory.GetNormalizedCategory();
        }

        // Before the other interfaces, of which one may declare the same method with another category.
        if (isInterfaceMethod && method.GetCustomAttribute<CategoryAttribute>() is { } ownCategory)
        {
            return ownCategory.GetNormalizedCategory();
        }

        foreach (var interfaceType in subjectType.GetInterfaces())
        {
            if (FindMethod(interfaceType, method.Name, parameterTypes)?.GetCustomAttribute<CategoryAttribute>() is { } interfaceCategory)
            {
                return interfaceCategory.GetNormalizedCategory();
            }
        }

        return null;
    }

    // Not Type.GetMethod, whose binder also accepts parameter types that the given ones convert to.
    private static MethodInfo? FindMethod(Type type, string name, Type[] parameterTypes)
    {
        return type.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(candidate =>
            candidate.Name == name &&
            candidate.GetParameters().Select(static parameter => parameter.ParameterType).SequenceEqual(parameterTypes));
    }

    private static Type? GetUnwrappedResultType(Type returnType)
    {
        if (returnType == typeof(void))
            return null;

        if (returnType == typeof(Task))
            return null;

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            return returnType.GetGenericArguments()[0];

        return returnType;
    }
}
