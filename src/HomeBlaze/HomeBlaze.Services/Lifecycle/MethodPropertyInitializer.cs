using System.Reflection;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Lifecycle;

/// <summary>
/// Lifecycle handler that creates virtual properties for [Operation] and [Query] methods.
/// This enables the [PropertyAttribute] pattern to work with methods, allowing attributes
/// like IsEnabled to be attached to method properties.
/// </summary>
[RunsAfter(typeof(SubjectRegistry))]
public class MethodPropertyInitializer : ILifecycleHandler
{
    public void OnSubjectAttached(SubjectLifecycleChange change)
    {
        // Use change.Context (invoking context) to access the registry
        // This works because SubjectRegistry has already registered the subject
        var registry = change.Context.TryGetService<ISubjectRegistry>();
        var registeredSubject = registry?.TryGetRegisteredSubject(change.Subject)
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

    public void OnSubjectDetached(SubjectLifecycleChange change)
    {
        // No cleanup needed
    }
}
