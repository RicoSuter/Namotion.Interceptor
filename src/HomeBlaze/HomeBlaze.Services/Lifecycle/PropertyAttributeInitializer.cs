using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Lifecycle;

/// <summary>
/// Lifecycle handler that registers State and Configuration registry attributes
/// from [State] and [Configuration] C# attributes on first attach.
/// </summary>
public class PropertyAttributeInitializer : ILifecycleHandler
{
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (!change.IsContextAttach)
            return;

        var registeredSubject = change.Subject.TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("Subject not registered");

        foreach (var property in registeredSubject.Properties)
        {
            foreach (var reflectionAttribute in property.ReflectionAttributes)
            {
                if (reflectionAttribute is StateAttribute stateAttribute)
                {
                    property.AddAttribute(KnownAttributes.State, typeof(StateMetadata),
                        _ => new StateMetadata
                        {
                            Name = stateAttribute.Name,
                            Unit = stateAttribute.Unit,
                            Position = stateAttribute.Position,
                            IsCumulative = stateAttribute.IsCumulative,
                            IsDiscrete = stateAttribute.IsDiscrete,
                            IsEstimated = stateAttribute.IsEstimated,
                        },
                        null);
                }
                else if (reflectionAttribute is ConfigurationAttribute)
                {
                    property.AddAttribute(KnownAttributes.Configuration, typeof(ConfigurationMetadata),
                        _ => new ConfigurationMetadata(), null);
                }
            }
        }
    }
}
