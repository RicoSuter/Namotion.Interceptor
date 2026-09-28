using System.Reflection;
using Namotion.Interceptor.Modbus.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Modbus.Mapping;

internal static class ModbusRegisterResolver
{
    public static List<ModbusRegisterBinding> Resolve(
        IInterceptorSubject root, byte defaultUnitId, IReadOnlySet<PropertyReference> excludedProperties)
    {
        var registeredRoot = root.TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("The root subject is not registered. Add WithRegistry() to the subject context.");

        var bindings = new List<ModbusRegisterBinding>();
        Walk(registeredRoot, defaultUnitId, excludedProperties, bindings, []);
        LinkScaleFactors(bindings);
        return bindings;
    }

    private static void Walk(
        RegisteredSubject subject, byte inheritedUnitId, IReadOnlySet<PropertyReference> excludedProperties,
        List<ModbusRegisterBinding> bindings, HashSet<RegisteredSubject> visited)
    {
        if (!visited.Add(subject))
        {
            return;
        }

        var unitId = GetUnitId(subject.Subject) ?? inheritedUnitId;
        var baseAddress = subject.Subject is IModbusBaseAddressProvider provider ? provider.BaseAddress : 0;

        foreach (var property in subject.Properties)
        {
            var attribute = GetRegisterAttribute(property);
            if (attribute is not null && !excludedProperties.Contains(property.Reference))
            {
                bindings.Add(CreateBinding(property, attribute, unitId, baseAddress));
            }

            foreach (var child in property.Children)
            {
                if (child.Subject.TryGetRegisteredSubject() is { } registeredChild)
                {
                    Walk(registeredChild, unitId, excludedProperties, bindings, visited);
                }
            }
        }
    }

    private static byte? GetUnitId(IInterceptorSubject subject)
    {
        if (subject is IModbusUnitIdProvider provider)
        {
            return provider.UnitId;
        }

        return subject.GetType().GetCustomAttribute<ModbusUnitIdAttribute>(inherit: true)?.UnitId;
    }

    private static ModbusRegisterAttribute? GetRegisterAttribute(RegisteredSubjectProperty property)
    {
        ModbusRegisterAttribute? result = null;
        foreach (var attribute in property.ReflectionAttributes)
        {
            if (attribute is ModbusRegisterAttribute registerAttribute)
            {
                // AllowMultiple only applies per concrete type, so a derived preset attribute can sit next to the base one.
                if (result is not null)
                {
                    throw Error(GetPath(property), "Only one ModbusRegisterAttribute (including derived attributes) is allowed per property.");
                }

                result = registerAttribute;
            }
        }

        return result;
    }

    private static ModbusRegisterBinding CreateBinding(
        RegisteredSubjectProperty property, ModbusRegisterAttribute attribute, byte unitId, int baseAddress)
    {
        var path = GetPath(property);
        var dataType = attribute.DataType;

        var isBitSpace = attribute.Space is ModbusAddressSpace.Coil or ModbusAddressSpace.DiscreteInput;
        if (isBitSpace && dataType != ModbusDataType.Boolean)
        {
            throw Error(path, $"{attribute.Space} requires the Boolean data type.");
        }

        if (!isBitSpace && dataType == ModbusDataType.Boolean)
        {
            throw Error(path, "Boolean requires the Coil or DiscreteInput space.");
        }

        if (dataType == ModbusDataType.String)
        {
            if (attribute.Length is < 1 or > 125)
            {
                throw Error(path, "String requires a Length between 1 and 125 registers.");
            }
        }
        else if (attribute.Length != 0)
        {
            throw Error(path, "Length is only valid for String.");
        }

        if (attribute.ScaleFactorProperty is not null && attribute.Scale != 1.0)
        {
            throw Error(path, "Scale and ScaleFactorProperty are mutually exclusive.");
        }

        if (!double.IsFinite(attribute.Scale) || attribute.Scale == 0)
        {
            throw Error(path, "Scale must be a finite, non-zero number.");
        }

        var address = baseAddress + attribute.Address;
        var count = ModbusRegisterCodec.GetRegisterCount(dataType, attribute.Length);
        if (attribute.Address < 0 || address < 0 || address + count - 1 > 65535)
        {
            throw Error(path, $"Address {address} with {count} register(s) is outside 0 to 65535.");
        }

        var reader = ModbusValueConverters.Create(attribute, property.Type, path);
        return new ModbusRegisterBinding(property.Reference, path, unitId, address, attribute, reader);
    }

    private static void LinkScaleFactors(List<ModbusRegisterBinding> bindings)
    {
        foreach (var binding in bindings)
        {
            var name = binding.Attribute.ScaleFactorProperty;
            if (name is null)
            {
                continue;
            }

            ModbusRegisterBinding? scaleFactor = null;
            foreach (var candidate in bindings)
            {
                if (ReferenceEquals(candidate.Property.Subject, binding.Property.Subject) && candidate.Property.Name == name)
                {
                    scaleFactor = candidate;
                    break;
                }
            }

            if (scaleFactor is null || scaleFactor.Attribute.DataType is not (ModbusDataType.U16 or ModbusDataType.S16))
            {
                throw Error(binding.Path,
                    $"ScaleFactorProperty '{name}' must name a U16 or S16 register property on the same subject that is not excluded.");
            }

            binding.ScaleFactor = scaleFactor;
        }
    }

    private static string GetPath(RegisteredSubjectProperty property)
        => $"{property.Subject.GetType().Name}.{property.Name}";

    private static ModbusConfigurationException Error(string path, string message)
        => new($"Invalid Modbus mapping on {path}: {message}");
}
