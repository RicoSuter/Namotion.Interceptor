using System.Reflection;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components;
using Namotion.Devices.Wallbox;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// Checks the subject types of the assemblies that the application references, by reflection and without
/// starting it. Not an integration test: it runs with the unit tests.
/// </summary>
public class OperationEnabledAttributeTests
{
    [Fact]
    public void WhenTypeDeclaresAnEnabledAttribute_ThenItNamesAPropertyOrAnOperationOfTheType()
    {
        // Arrange
        var assemblies = GetApplicationAssemblies();

        // Act
        var declarations = assemblies
            .SelectMany(GetLoadableTypes)
            .SelectMany(FindEnabledAttributes)
            .ToList();
        var unknownTargets = declarations
            .Where(declaration => !GetTargetNames(declaration.Type).Contains(declaration.Target))
            .Select(declaration => $"{declaration.Type.FullName}.{declaration.Property} -> {declaration.Target}")
            .ToList();

        // Assert
        Assert.Contains(typeof(WallboxCharger).Assembly, assemblies);
        Assert.Contains(declarations, declaration => declaration.Type == typeof(WallboxCharger));
        // Assert.Empty would cut each entry short.
        Assert.True(unknownTargets.Count == 0,
            $"Enabled attributes that name no property or operation:{Environment.NewLine}{string.Join(Environment.NewLine, unknownTargets)}");
    }

    // Program.cs names a type of every assembly it registers, so each one is a reference of the application.
    private static List<Assembly> GetApplicationAssemblies()
    {
        var application = typeof(App).Assembly;
        return application.GetReferencedAssemblies()
            .Where(name => name.Name is { } assemblyName &&
                (assemblyName.StartsWith("HomeBlaze", StringComparison.Ordinal) ||
                 assemblyName.StartsWith("Namotion.Devices", StringComparison.Ordinal)))
            .Select(Assembly.Load)
            .Prepend(application)
            .ToList();
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }

    private static IEnumerable<(Type Type, string Property, string Target)> FindEnabledAttributes(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(property => (Property: property, Attribute: property.GetCustomAttribute<PropertyAttributeAttribute>()))
            .Where(pair => pair.Attribute?.AttributeName == KnownAttributes.IsEnabled)
            .Select(pair => (type, pair.Property.Name, pair.Attribute!.PropertyName));

    private static HashSet<string> GetTargetNames(Type type)
    {
        var names = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Concat(type.GetInterfaces().SelectMany(interfaceType => interfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)));
        foreach (var method in methods)
        {
            if (method.GetCustomAttribute<OperationAttribute>() is not null || method.GetCustomAttribute<QueryAttribute>() is not null)
            {
                // The name under which MethodPropertyInitializer registers the method.
                names.Add(method.Name.EndsWith("Async", StringComparison.Ordinal) ? method.Name[..^5] : method.Name);
            }
        }

        return names;
    }
}
