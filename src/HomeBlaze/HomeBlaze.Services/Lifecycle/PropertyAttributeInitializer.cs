using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Lifecycle;

/// <summary>
/// Lifecycle handler that registers State and Configuration registry attributes
/// from [State] and [Configuration] C# attributes on first attach.
/// </summary>
public class PropertyAttributeInitializer : IPropertyLifecycleHandler
{
    public void AttachProperty(SubjectPropertyLifecycleChange change)
    {
        var property = change.Subject.TryGetRegisteredProperty(change.Property.Name);
        if (property is not null)
        {
            InitializePropertyAttributes(property);
        }
    }

    public void DetachProperty(SubjectPropertyLifecycleChange change)
    {
    }

    public void RefreshCollectionProperty(PropertyReference property, object? value)
    {
    }

    private static void InitializePropertyAttributes(RegisteredSubjectProperty property)
    {
        var hasState = false;
        ConfigurationAttribute? configurationAttribute = null;

        // Merge fields: first explicitly-set value wins (class attributes come first)
        string? title = null;
        StateUnit? unit = null;
        int? position = null;
        bool? isCumulative = null;
        bool? isDiscrete = null;
        bool? isEstimated = null;

        foreach (var reflectionAttribute in property.ReflectionAttributes)
        {
            if (reflectionAttribute is StateAttribute stateAttribute)
            {
                hasState = true;

                title ??= stateAttribute.Title;

                if (unit == null && stateAttribute.IsUnitSet)
                    unit = stateAttribute.Unit;

                if (position == null && stateAttribute.IsPositionSet)
                    position = stateAttribute.Position;

                if (isCumulative == null && stateAttribute.IsCumulativeSet)
                    isCumulative = stateAttribute.IsCumulative;

                if (isDiscrete == null && stateAttribute.IsDiscreteSet)
                    isDiscrete = stateAttribute.IsDiscrete;

                if (isEstimated == null && stateAttribute.IsEstimatedSet)
                    isEstimated = stateAttribute.IsEstimated;
            }
            else if (reflectionAttribute is ConfigurationAttribute configAttribute)
            {
                configurationAttribute = configAttribute;
                if (property.TryGetAttribute(KnownAttributes.Configuration) is null)
                {
                    var metadata = new ConfigurationMetadata
                    {
                        IsSecret = configAttribute.IsSecret
                    };
                    property.AddAttribute(KnownAttributes.Configuration, typeof(ConfigurationMetadata),
                        _ => metadata, null);
                }
            }
        }

        if (hasState && property.TryGetAttribute(KnownAttributes.State) is null)
        {
            var stateMetadata = new StateMetadata
            {
                Title = title,
                Unit = unit ?? StateUnit.Default,
                Position = position ?? int.MaxValue,
                IsCumulative = isCumulative ?? false,
                IsDiscrete = isDiscrete ?? false,
                IsEstimated = isEstimated ?? false,
            };
            property.AddAttribute(KnownAttributes.State, typeof(StateMetadata),
                _ => stateMetadata, null);
        }

        if (hasState && configurationAttribute is { IsSecret: true })
        {
            throw new InvalidOperationException(
                $"Property '{property.Name}' on '{property.Parent.Subject.GetType().Name}' " +
                $"has both [State] and [Configuration(IsSecret = true)]. " +
                $"Secret configuration properties must not be exposed as state.");
        }
    }
}
