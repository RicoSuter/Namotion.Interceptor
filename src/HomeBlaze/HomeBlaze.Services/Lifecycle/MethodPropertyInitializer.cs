using System.Reflection;
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
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.IsContextAttach)
        {
            var registeredSubject = change.Subject.TryGetRegisteredSubject()
                ?? throw new InvalidOperationException("Subject not registered");

            foreach (var method in registeredSubject.GetAllMethods())
            {
                var methodName = method.MethodInfo.Name.Replace("Async", string.Empty, StringComparison.OrdinalIgnoreCase);
                var methodMetadata = new MethodPropertyMetadata();

                registeredSubject.AddProperty(
                    methodName,
                    typeof(MethodPropertyMetadata),
                    _ => methodMetadata,
                    null,
                    [.. method.MethodInfo.GetCustomAttributes<Attribute>()]);
            }
        }
    }
}