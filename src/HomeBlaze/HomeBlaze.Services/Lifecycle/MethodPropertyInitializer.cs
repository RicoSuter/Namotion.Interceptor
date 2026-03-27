using System.Reflection;
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
        // Skip if already discovered from the type itself
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

        var parameters = method.GetParameters()
            .Select(parameter =>
            {
                var operationParameterAttribute = parameter.GetCustomAttribute<OperationParameterAttribute>();
                return new MethodParameter
                {
                    Name = parameter.Name ?? $"arg{parameter.Position}",
                    Type = parameter.ParameterType,
                    Unit = operationParameterAttribute?.Unit,
                    IsFromServices = parameter.GetCustomAttribute<FromServicesAttribute>() != null,
                    IsRuntimeProvided = parameter.ParameterType == typeof(CancellationToken)
                };
            })
            .ToArray();

        var resultType = GetUnwrappedResultType(method.ReturnType);
        var requiresConfirmation = attribute is OperationAttribute { RequiresConfirmation: true };

        var capturedMethod = method;
        var metadata = new MethodMetadata
        {
            Kind = kind,
            Title = attribute.Title ?? propertyName,
            Description = attribute.Description,
            Icon = attribute.Icon,
            Position = attribute.Position,
            RequiresConfirmation = requiresConfirmation,
            Parameters = parameters,
            ResultType = resultType,
            InvokeAsync = async (target, arguments) =>
            {
                var result = capturedMethod.Invoke(target, arguments);
                if (result is Task task)
                {
                    await task.ConfigureAwait(false);
                    // Extract result from Task<T>
                    var taskType = task.GetType();
                    if (taskType.IsGenericType && taskType.GetGenericTypeDefinition() == typeof(Task<>))
                    {
                        return taskType.GetProperty("Result")?.GetValue(task);
                    }
                    return null;
                }
                return result;
            }
        };

        registeredSubject.AddProperty(
            propertyName,
            typeof(MethodMetadata),
            _ => metadata,
            null,
            [.. capturedMethod.GetCustomAttributes<Attribute>()]);
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
