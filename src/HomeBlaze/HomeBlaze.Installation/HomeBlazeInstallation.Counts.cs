using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Installation;

public partial class HomeBlazeInstallation
{
    private void CountSubjects(IInterceptorSubjectContext context)
    {
        var registry = context.TryGetService<ISubjectRegistry>();
        if (registry is null)
        {
            return;
        }

        int subjects = 0, properties = 0, states = 0, configurations = 0, operations = 0, queries = 0;
        foreach (var (_, registered) in registry.KnownSubjects)
        {
            subjects++;
            var subjectProperties = registered.Properties;
            properties += subjectProperties.Length;
            foreach (var property in subjectProperties)
            {
                // GetStateMetadata and IsConfigurationProperty scan all properties per call; counting the attributes
                // themselves keeps the pass linear.
                if (property.IsAttribute)
                {
                    var attributeName = property.AttributeMetadata.AttributeName;
                    if (attributeName == KnownAttributes.State)
                    {
                        states++;
                    }
                    else if (attributeName == KnownAttributes.Configuration)
                    {
                        configurations++;
                    }
                }
                else if (TryGetMethod(property) is { } method)
                {
                    if (method.Kind == MethodKind.Query)
                    {
                        queries++;
                    }
                    else
                    {
                        operations++;
                    }
                }
            }
        }

        SubjectCount = subjects;
        PropertyCount = properties;
        StatePropertyCount = states;
        ConfigurationPropertyCount = configurations;
        OperationCount = operations;
        QueryCount = queries;
    }

    // Reads the value of interface and object typed properties, so the getter of any subject runs here; one that throws
    // holds no method and must not stop the poll.
    private static MethodMetadata? TryGetMethod(RegisteredSubjectProperty property)
    {
        try
        {
            return property.TryGetMethodMetadata();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
