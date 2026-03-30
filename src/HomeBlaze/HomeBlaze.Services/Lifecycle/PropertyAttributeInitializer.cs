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
            var hasState = false;
            ConfigurationAttribute? configurationAttribute = null;

            foreach (var reflectionAttribute in property.ReflectionAttributes)
            {
                if (reflectionAttribute is StateAttribute stateAttribute)
                {
                    hasState = true;
                    var metadata = new StateMetadata
                    {
                        Title = stateAttribute.Title,
                        Unit = stateAttribute.Unit,
                        Position = stateAttribute.Position,
                        IsCumulative = stateAttribute.IsCumulative,
                        IsDiscrete = stateAttribute.IsDiscrete,
                        IsEstimated = stateAttribute.IsEstimated,
                    };
                    property.AddAttribute(KnownAttributes.State, typeof(StateMetadata),
                        _ => metadata, null);
                }
                else if (reflectionAttribute is ConfigurationAttribute configAttribute)
                {
                    configurationAttribute = configAttribute;
                    var metadata = new ConfigurationMetadata
                    {
                        IsSecret = configAttribute.IsSecret
                    };
                    property.AddAttribute(KnownAttributes.Configuration, typeof(ConfigurationMetadata),
                        _ => metadata, null);
                }
            }

            if (hasState && configurationAttribute is { IsSecret: true })
            {
                throw new InvalidOperationException(
                    $"Property '{property.Name}' on '{registeredSubject.Subject.GetType().Name}' " +
                    $"has both [State] and [Configuration(IsSecret = true)]. " +
                    $"Secret configuration properties must not be exposed as state.");
            }
        }
    }
}
