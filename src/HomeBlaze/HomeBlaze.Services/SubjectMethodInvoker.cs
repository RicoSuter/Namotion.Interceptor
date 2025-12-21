using System.Reflection;
using HomeBlaze.Abstractions.Services;
using Namotion.Interceptor;

namespace HomeBlaze.Services;

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

    public async Task<MethodInvocationResult> InvokeAsync(
        IInterceptorSubject subject,
        SubjectMethodInfo method,
        object?[] userParameters,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var parameters = ResolveParameters(method, userParameters);

            var result = method.MethodInfo.Invoke(subject, parameters);
            if (method.IsAsync && result is Task task)
            {
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

    private object?[] ResolveParameters(SubjectMethodInfo method, object?[] userParameters)
    {
        var parameterInfos = method.MethodInfo.GetParameters();
        var resolvedParameters = new object?[parameterInfos.Length];
        var userParameterIndex = 0;

        for (var i = 0; i < parameterInfos.Length; i++)
        {
            var parameterType = parameterInfos[i].ParameterType;

            // Try to resolve from DI first (ActivatorUtilities semantics)
            var service = _serviceProvider.GetService(parameterType);
            if (service != null)
            {
                resolvedParameters[i] = service;
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
