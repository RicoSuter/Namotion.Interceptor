using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Services;
using Namotion.Interceptor;

namespace HomeBlaze.Services;

// TODO: Add unit tests for SubjectMethodInvoker - sync/async methods, [FromServices] injection, CancellationToken handling, error scenarios

/// <summary>
/// Invokes operations and queries on subjects, resolving DI services for parameters.
/// </summary>
public class SubjectMethodInvoker : ISubjectMethodInvoker
{
    private readonly IServiceProvider _serviceProvider;

    public SubjectMethodInvoker(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Returns true if the parameter type is automatically injected by the invoker
    /// (e.g., CancellationToken). These parameters don't need UI input or DI registration.
    /// </summary>
    public static bool IsAutoInjectedParameter(Type parameterType)
    {
        return parameterType == typeof(CancellationToken);
    }

    public async Task<MethodInvocationResult> InvokeAsync(
        IInterceptorSubject subject,
        SubjectMethodInfo method,
        object?[] userParameters,
        CancellationToken cancellationToken)
    {
        try
        {
            var parameters = ResolveParameters(method, userParameters, cancellationToken);

            var result = method.MethodInfo.Invoke(subject, parameters);
            if (method.IsAsync)
            {
                if (result is not Task task)
                {
                    throw new InvalidOperationException(
                        $"Async method '{method.Title}' returned null instead of Task");
                }

                await task.WaitAsync(cancellationToken);
                result = GetTaskResult(task, method.ResultType);
            }

            return MethodInvocationResult.Succeeded(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var actualException = exception is TargetInvocationException tie
                ? tie.InnerException ?? exception
                : exception;

            return MethodInvocationResult.Failed(actualException);
        }
    }

    private object?[] ResolveParameters(SubjectMethodInfo method, object?[] userParameters, CancellationToken cancellationToken)
    {
        var parameterInfos = method.MethodInfo.GetParameters();
        var resolvedParameters = new object?[parameterInfos.Length];
        var userParameterIndex = 0;

        for (var i = 0; i < parameterInfos.Length; i++)
        {
            var parameterInfo = parameterInfos[i];
            var parameterType = parameterInfo.ParameterType;

            if (parameterType == typeof(CancellationToken))
            {
                resolvedParameters[i] = cancellationToken;
                continue;
            }

            // Only resolve from DI if explicitly marked with [FromServices]
            if (parameterInfo.GetCustomAttribute<FromServicesAttribute>() != null)
            {
                resolvedParameters[i] = _serviceProvider.GetService(parameterType)
                    ?? throw new InvalidOperationException(
                        $"Service '{parameterType.Name}' not found for parameter '{parameterInfo.Name}'");
            }
            else
            {
                // Use user-provided parameter
                resolvedParameters[i] = userParameterIndex < userParameters.Length
                    ? userParameters[userParameterIndex++]
                    : null;
            }
        }

        return resolvedParameters;
    }

    private static object? GetTaskResult(Task task, Type? resultType)
    {
        if (resultType == null)
            return null;

        var resultProperty = task.GetType().GetProperty("Result");
        return resultProperty?.GetValue(task);
    }
}
