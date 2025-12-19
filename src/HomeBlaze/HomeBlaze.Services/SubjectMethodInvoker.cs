using System.Reflection;
using HomeBlaze.Abstractions.Services;
using Namotion.Interceptor;

namespace HomeBlaze.Services;

/// <summary>
/// Default implementation of ISubjectMethodInvoker.
/// </summary>
public class SubjectMethodInvoker : ISubjectMethodInvoker
{
    /// <inheritdoc />
    public async Task<MethodInvocationResult> InvokeAsync(
        IInterceptorSubject subject,
        SubjectMethodInfo method,
        object?[] parameters,
        CancellationToken cancellationToken = default)
    {
        try
        {
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
            throw; // Let cancellation propagate
        }
        catch (Exception ex)
        {
            var actualException = ex is TargetInvocationException tie
                ? tie.InnerException ?? ex
                : ex;

            return MethodInvocationResult.Failed(actualException);
        }
    }

    private static object? GetTaskResult(Task task, Type? resultType)
    {
        if (resultType == null)
            return null;

        var resultProperty = task.GetType().GetProperty("Result");
        return resultProperty?.GetValue(task);
    }
}
