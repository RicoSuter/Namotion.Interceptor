using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Services.Lifecycle;

/// <summary>
/// Method initializer that creates <see cref="MethodMetadata"/> attributes
/// for methods marked with <see cref="OperationAttribute"/> or <see cref="QueryAttribute"/>.
/// Replaces the reflection-based <c>MethodPropertyInitializer</c> — methods are now
/// discovered by the source generator and this initializer adds HomeBlaze-specific metadata.
/// </summary>
public class MethodInitializer : ISubjectMethodInitializer
{
    [ThreadStatic]
    private static NullabilityInfoContext? _nullabilityContext;

    private static NullabilityInfoContext NullabilityContext => _nullabilityContext ??= new();

    public void InitializeMethod(RegisteredSubjectMethod method)
    {
        var attribute = method.ReflectionAttributes
            .OfType<HomeBlaze.Abstractions.Attributes.SubjectMethodAttribute>()
            .FirstOrDefault();

        if (attribute == null)
            return;

        MethodKind kind;
        if (attribute is OperationAttribute)
            kind = MethodKind.Operation;
        else if (attribute is QueryAttribute)
            kind = MethodKind.Query;
        else
            return;

        var propertyName = method.Name.EndsWith("Async", StringComparison.Ordinal)
            ? method.Name[..^5]
            : method.Name;

        var parameters = BuildParameters(method);
        var resultType = GetUnwrappedResultType(method.ReturnType);
        var requiresConfirmation = attribute is OperationAttribute { RequiresConfirmation: true };

        var metadata = new MethodMetadata(arguments => method.Invoke(arguments ?? []))
        {
            Kind = kind,
            MethodName = method.Name,
            PropertyName = propertyName,
            Title = attribute.Title ?? propertyName,
            Description = attribute.Description,
            Icon = attribute.Icon,
            Position = attribute.Position,
            RequiresConfirmation = requiresConfirmation,
            Parameters = parameters,
            ResultType = resultType,
        };

        method.AddAttribute("Metadata", typeof(MethodMetadata),
            _ => metadata, null);
    }

    private static MethodParameter[] BuildParameters(RegisteredSubjectMethod method)
    {
        // Use reflection to get parameter-level attributes and nullability info.
        // This runs once per method during initialization — not on the hot path.
        var methodInfo = FindMethodInfo(method);
        if (methodInfo == null)
            return BuildParametersFromMetadata(method);

        var reflectionParameters = methodInfo.GetParameters();
        var parameters = new MethodParameter[reflectionParameters.Length];

        for (var i = 0; i < reflectionParameters.Length; i++)
        {
            var parameter = reflectionParameters[i];
            var operationParameterAttribute = parameter.GetCustomAttribute<OperationParameterAttribute>();
            var nullabilityInfo = NullabilityContext.Create(parameter);
            var isNullable = Nullable.GetUnderlyingType(parameter.ParameterType) != null
                || nullabilityInfo.WriteState == NullabilityState.Nullable
                || parameter is { HasDefaultValue: true, DefaultValue: null };

            parameters[i] = new MethodParameter
            {
                Name = parameter.Name ?? $"arg{parameter.Position}",
                Type = parameter.ParameterType,
                Unit = operationParameterAttribute?.Unit,
                IsFromServices = parameter.GetCustomAttribute<FromServicesAttribute>() != null,
                IsRuntimeProvided = parameter.ParameterType == typeof(CancellationToken),
                IsNullable = isNullable,
            };
        }

        return parameters;
    }

    private static MethodParameter[] BuildParametersFromMetadata(RegisteredSubjectMethod method)
    {
        // Fallback: build from SubjectMethodParameterMetadata (no nullability/attribute info)
        return method.Parameters
            .Select(p => new MethodParameter
            {
                Name = p.Name,
                Type = p.Type,
                IsRuntimeProvided = p.Type == typeof(CancellationToken),
            })
            .ToArray();
    }

    private static MethodInfo? FindMethodInfo(RegisteredSubjectMethod method)
    {
        var subjectType = method.Parent.Subject.GetType();

        // Try concrete type first
        var methodInfo = subjectType.GetMethod(method.Name,
            BindingFlags.Public | BindingFlags.Instance);
        if (methodInfo != null)
            return methodInfo;

        // Try interfaces
        foreach (var interfaceType in subjectType.GetInterfaces())
        {
            methodInfo = interfaceType.GetMethod(method.Name,
                BindingFlags.Public | BindingFlags.Instance);
            if (methodInfo != null)
                return methodInfo;
        }

        return null;
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
