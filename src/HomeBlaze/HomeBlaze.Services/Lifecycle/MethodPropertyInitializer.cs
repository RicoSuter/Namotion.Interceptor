using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Lifecycle;

/// <summary>
/// Lifecycle handler that creates virtual properties for [Operation] and [Query] methods.
/// This enables the [PropertyAttribute] pattern to work with methods, allowing attributes
/// like IsEnabled to be attached to method properties.
/// </summary>
public class MethodPropertyInitializer : ILifecycleHandler
{
    public void AttachSubject(SubjectLifecycleChange change)
    {
        if (change.IsFirstAttach)
        {
            var registeredSubject = change.Subject.TryGetRegisteredSubject()
                                    ?? throw new InvalidOperationException("Subject not registered");

            foreach (var method in change.Subject.GetType().GetMethods())
            {
                var operationAttribute = method.GetCustomAttribute<OperationAttribute>();
                var queryAttribute = method.GetCustomAttribute<QueryAttribute>();

                if (operationAttribute == null && queryAttribute == null)
                    continue;

                var methodName = method.Name.Replace("Async", string.Empty, StringComparison.OrdinalIgnoreCase);
                var methodMetadata = new MethodPropertyMetadata();
            
                registeredSubject.AddProperty(
                    methodName,
                    typeof(MethodPropertyMetadata),
                    _ => methodMetadata,
                    null,
                    [.. method.GetCustomAttributes<Attribute>()]);
            }
        }
    }

    public void DetachSubject(SubjectLifecycleChange change)
    {
    }
}