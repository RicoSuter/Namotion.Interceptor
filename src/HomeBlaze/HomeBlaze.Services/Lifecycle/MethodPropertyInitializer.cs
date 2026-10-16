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
    private static readonly ConditionalWeakTable<Type, SubjectTypeMethods> MethodsBySubjectType = new();

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.IsContextAttach)
        {
            var registeredSubject = change.Subject.TryGetRegisteredSubject()
                ?? throw new InvalidOperationException("Subject not registered");

            var type = change.Subject.GetType();
            var discoveredMethods = new HashSet<string>();
            var typeMethods = MethodsBySubjectType.GetValue(type, static type => new SubjectTypeMethods(type));

            // Discover methods from the concrete type
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                TryRegisterMethod(registeredSubject, change.Subject, method, discoveredMethods, typeMethods);
            }

            // Discover methods from interfaces (for default interface implementations or attribute on interface)
            foreach (var interfaceType in type.GetInterfaces())
            {
                foreach (var method in interfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    TryRegisterMethod(registeredSubject, change.Subject, method, discoveredMethods, typeMethods);
                }
            }
        }
    }

    private static void TryRegisterMethod(
        Namotion.Interceptor.Registry.Abstractions.RegisteredSubject registeredSubject,
        Namotion.Interceptor.IInterceptorSubject subject,
        MethodInfo method,
        HashSet<string> discoveredMethods,
        SubjectTypeMethods typeMethods)
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
        var traits = typeMethods.GetTraits(method);

        var metadata = new MethodMetadata(subject, method)
        {
            // A query that implements or overrides an operation requiring confirmation runs as that operation, so it
            // cannot be invoked as a query either.
            Kind = traits.RequiresConfirmation ? MethodKind.Operation : kind,
            PropertyName = propertyName,
            Title = attribute.Title ?? propertyName,
            Description = attribute.Description,
            Icon = attribute.Icon,
            Position = attribute.Position,
            Category = traits.Category,
            RequiresConfirmation = traits.RequiresConfirmation,
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

    /// <summary>The category and the confirmation of a discovered method of one subject type.</summary>
    private readonly record struct MethodTraits(string? Category, bool RequiresConfirmation);

    /// <summary>The interface maps of a subject type and the traits of its methods, computed once per method.</summary>
    private sealed class SubjectTypeMethods
    {
        private readonly InterfaceMapping[] _interfaceMaps;
        private readonly ConcurrentDictionary<MethodInfo, MethodTraits> _traits = new();

        public SubjectTypeMethods(Type subjectType)
        {
            _interfaceMaps = Array.ConvertAll(subjectType.GetInterfaces(), subjectType.GetInterfaceMap);
        }

        public MethodTraits GetTraits(MethodInfo method) =>
            _traits.GetOrAdd(method, static (method, self) => self.FindTraits(method), this);

        private MethodTraits FindTraits(MethodInfo method)
        {
            var isInterfaceMethod = method.DeclaringType is { IsInterface: true };

            // The method that runs: the method itself, or what implements the interface method, which may be the
            // interface method itself, an explicit implementation or a class method.
            var implementation = isInterfaceMethod ? FindImplementation(method) ?? method : method;
            var implementedMethods = GetImplementedInterfaceMethods(implementation);

            // An explicit implementation is not a public class method, so the interface method decides for it.
            var classMethod = implementation is { IsPublic: true, DeclaringType.IsInterface: false } ? implementation : null;

            return new MethodTraits(
                FindCategory(method, isInterfaceMethod, classMethod, implementedMethods),
                RequiresConfirmation(method) ||
                (classMethod is not null && AnyOverriddenMethodRequiresConfirmation(classMethod)) ||
                implementedMethods.Exists(RequiresConfirmation));
        }

        private static string? FindCategory(MethodInfo method, bool isInterfaceMethod, MethodInfo? classMethod, List<MethodInfo> implementedMethods)
        {
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

            foreach (var implementedMethod in implementedMethods)
            {
                if (implementedMethod.GetCustomAttribute<CategoryAttribute>() is { } interfaceCategory)
                {
                    return interfaceCategory.GetNormalizedCategory();
                }
            }

            return null;
        }

        private static bool RequiresConfirmation(MethodInfo method) =>
            method.GetCustomAttribute<OperationAttribute>() is { RequiresConfirmation: true };

        // Every level, not only the base definition: an intermediate override may add the requirement, and an own
        // attribute on a further override hides the inherited one.
        private static bool AnyOverriddenMethodRequiresConfirmation(MethodInfo method)
        {
            var baseDefinition = method.GetBaseDefinition();
            var current = method;
            while (!IsSameMethod(current, baseDefinition) && current.DeclaringType?.BaseType is { } baseType)
            {
                var overridden = FindOverriddenMethod(baseType, current, baseDefinition);
                if (overridden is null)
                {
                    return RequiresConfirmation(baseDefinition);
                }

                if (RequiresConfirmation(overridden))
                {
                    return true;
                }

                current = overridden;
            }

            return false;
        }

        private static MethodInfo? FindOverriddenMethod(Type baseType, MethodInfo method, MethodInfo baseDefinition)
        {
            var parameterTypes = Array.ConvertAll(method.GetParameters(), static parameter => parameter.ParameterType);
            foreach (var candidate in baseType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (candidate.Name == method.Name &&
                    candidate.IsVirtual &&
                    IsSameMethod(candidate.GetBaseDefinition(), baseDefinition) &&
                    candidate.GetParameters().Select(static parameter => parameter.ParameterType).SequenceEqual(parameterTypes))
                {
                    return candidate;
                }
            }

            return null;
        }

        private MethodInfo? FindImplementation(MethodInfo interfaceMethod)
        {
            foreach (var map in _interfaceMaps)
            {
                if (map.InterfaceType == interfaceMethod.DeclaringType)
                {
                    var index = Array.FindIndex(map.InterfaceMethods, candidate => IsSameMethod(candidate, interfaceMethod));
                    return index >= 0 ? map.TargetMethods[index] : null;
                }
            }

            return null;
        }

        /// <summary>The interface methods that <paramref name="implementation"/> implements, in the order of the interfaces.</summary>
        private List<MethodInfo> GetImplementedInterfaceMethods(MethodInfo implementation)
        {
            var implementedMethods = new List<MethodInfo>();
            foreach (var map in _interfaceMaps)
            {
                for (var index = 0; index < map.TargetMethods.Length; index++)
                {
                    if (IsSameMethod(map.TargetMethods[index], implementation))
                    {
                        implementedMethods.Add(map.InterfaceMethods[index]);
                    }
                }
            }

            return implementedMethods;
        }

        // Not Equals, which also compares the type the method was read from.
        private static bool IsSameMethod(MethodInfo left, MethodInfo right) =>
            left.MethodHandle == right.MethodHandle && left.DeclaringType == right.DeclaringType;
    }
}
