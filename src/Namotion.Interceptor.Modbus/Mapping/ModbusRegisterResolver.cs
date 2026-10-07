using Namotion.Interceptor.Modbus.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Modbus.Mapping;

internal static class ModbusRegisterResolver
{
    public static List<ModbusRegisterBinding> Resolve(
        IInterceptorSubject root, byte defaultUnitId, IReadOnlySet<PropertyReference> excludedProperties)
    {
        var registeredRoot = root.TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("The root subject is not registered. Add WithRegistry() to the subject context.");

        var bindings = new List<ModbusRegisterBinding>();
        Walk(root, registeredRoot, defaultUnitId, excludedProperties, bindings, []);
        LinkScaleFactors(bindings);
        return bindings;
    }

    private static void Walk(
        IInterceptorSubject root, RegisteredSubject subject, byte inheritedUnitId,
        IReadOnlySet<PropertyReference> excludedProperties,
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
            var attribute = GetRegisterAttribute(property, root);
            if (attribute is not null && !excludedProperties.Contains(property.Reference))
            {
                bindings.Add(CreateBinding(property, root, attribute, unitId, baseAddress));
            }

            foreach (var child in property.Children)
            {
                if (child.Subject.TryGetRegisteredSubject() is { } registeredChild)
                {
                    Walk(root, registeredChild, unitId, excludedProperties, bindings, visited);
                }
            }
        }
    }

    private static byte? GetUnitId(IInterceptorSubject subject)
        => subject is IModbusUnitIdProvider provider ? provider.UnitId : null;

    private static ModbusRegisterAttribute? GetRegisterAttribute(RegisteredSubjectProperty property, IInterceptorSubject root)
    {
        ModbusRegisterAttribute? result = null;
        foreach (var attribute in property.ReflectionAttributes)
        {
            if (attribute is ModbusRegisterAttribute registerAttribute)
            {
                // AllowMultiple only applies per concrete type, so a derived preset attribute can sit next to the base one.
                if (result is not null)
                {
                    throw ModbusConfigurationException.ForMapping(GetPath(property, root), "Only one ModbusRegisterAttribute (including derived attributes) is allowed per property.");
                }

                result = registerAttribute;
            }
        }

        return result;
    }

    private static ModbusRegisterBinding CreateBinding(
        RegisteredSubjectProperty property, IInterceptorSubject root, ModbusRegisterAttribute attribute, byte unitId, int baseAddress)
    {
        var path = GetPath(property, root);
        ValidateEnums(path, attribute);
        ValidateDataType(path, attribute);
        ValidateScale(path, attribute);

        // Summed in 64 bits so a large base address cannot overflow into the valid range.
        var address = (long)baseAddress + attribute.Address;
        var count = ModbusRegisterCodec.GetRegisterCount(attribute.DataType, attribute.Length);
        if (attribute.Address < 0 || address < 0 || address > ModbusAddressSpaceExtensions.AddressCount - count)
        {
            throw ModbusConfigurationException.ForMapping(path, $"Address {address} with {count} register(s) is outside 0 to {ModbusAddressSpaceExtensions.AddressCount - 1}.");
        }

        var scaleFactorReference = GetScaleFactorReference(property, attribute, path);
        var reader = ModbusValueConverters.Create(attribute, property.Type, path, hasDynamicScale: scaleFactorReference is not null);
        return new ModbusRegisterBinding(property.Reference, path, unitId, (int)address, attribute, reader)
        {
            ScaleFactorReference = scaleFactorReference
        };
    }

    private static PropertyReference? GetScaleFactorReference(RegisteredSubjectProperty property, ModbusRegisterAttribute attribute, string path)
    {
        var subject = property.Reference.Subject;
        if (subject is IModbusScaleFactorProvider provider && provider.TryGetScaleFactorProperty(property.Name) is { } provided)
        {
            if (attribute.ScaleFactorProperty is not null)
            {
                throw ModbusConfigurationException.ForMapping(path, "ScaleFactorProperty and IModbusScaleFactorProvider are mutually exclusive.");
            }

            return provided;
        }

        return attribute.ScaleFactorProperty is { } name ? new PropertyReference(subject, name) : null;
    }

    private static void ValidateEnums(string path, ModbusRegisterAttribute attribute)
    {
        if (!Enum.IsDefined(attribute.DataType))
        {
            throw ModbusConfigurationException.ForMapping(path, $"Data type {attribute.DataType} is not defined.");
        }

        if (!Enum.IsDefined(attribute.AddressSpace))
        {
            throw ModbusConfigurationException.ForMapping(path, $"Address space {attribute.AddressSpace} is not defined.");
        }

        if (!Enum.IsDefined(attribute.WordOrder))
        {
            throw ModbusConfigurationException.ForMapping(path, $"Word order {attribute.WordOrder} is not defined.");
        }

        if (!Enum.IsDefined(attribute.NotAvailableValue))
        {
            throw ModbusConfigurationException.ForMapping(path, $"Not-available value {attribute.NotAvailableValue} is not defined.");
        }
    }

    private static void ValidateDataType(string path, ModbusRegisterAttribute attribute)
    {
        var dataType = attribute.DataType;
        var isBitSpace = attribute.AddressSpace.IsBitSpace();
        if (isBitSpace && dataType != ModbusDataType.Boolean)
        {
            throw ModbusConfigurationException.ForMapping(path, $"{attribute.AddressSpace} requires the Boolean data type.");
        }

        if (!isBitSpace && dataType == ModbusDataType.Boolean)
        {
            throw ModbusConfigurationException.ForMapping(path, "Boolean requires the Coil or DiscreteInput space.");
        }

        if (dataType == ModbusDataType.String)
        {
            // The address check in CreateBinding narrows this down to the registers left after the address.
            if (attribute.Length is < 1 or > ModbusAddressSpaceExtensions.AddressCount)
            {
                throw ModbusConfigurationException.ForMapping(path, $"String requires a Length between 1 and {ModbusAddressSpaceExtensions.AddressCount} registers.");
            }
        }
        else if (attribute.Length != 0)
        {
            throw ModbusConfigurationException.ForMapping(path, "Length is only valid for String.");
        }
    }

    private static void ValidateScale(string path, ModbusRegisterAttribute attribute)
    {
        if (!double.IsFinite(attribute.Scale) || attribute.Scale == 0)
        {
            throw ModbusConfigurationException.ForMapping(path, "Scale must be a finite, non-zero number.");
        }
    }

    private static void LinkScaleFactors(List<ModbusRegisterBinding> bindings)
    {
        Dictionary<PropertyReference, ModbusRegisterBinding>? bindingsByProperty = null;
        foreach (var binding in bindings)
        {
            if (binding.ScaleFactorReference is not { } reference)
            {
                continue;
            }

            bindingsByProperty ??= bindings.ToDictionary(candidate => candidate.Property);

            // S16 only: a scale factor is a signed exponent, which a U16 register cannot hold.
            if (!bindingsByProperty.TryGetValue(reference, out var scaleFactor) || scaleFactor.Attribute.DataType is not ModbusDataType.S16)
            {
                throw ModbusConfigurationException.ForMapping(binding.Path,
                    $"Scale factor property '{reference.Name}' of {reference.Subject.GetType().Name} must be an S16 register property that is mapped and not excluded.");
            }

            binding.ScaleFactor = scaleFactor;
        }
    }

    private static string GetPath(RegisteredSubjectProperty property, IInterceptorSubject root)
        => property.TryGetPath(root) ?? $"{property.Subject.GetType().Name}.{property.Name}";
}
