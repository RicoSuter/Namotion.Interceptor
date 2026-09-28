using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus.Attributes;
using Namotion.Interceptor.Modbus.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Modbus.Tests.Mapping;

public partial class ModbusRegisterResolverTests
{
    [InterceptorSubject]
    public partial class ResolverRoot
    {
        [ModbusRegister(0, ModbusDataType.U16)]
        public partial int? Value { get; set; }

        [ModbusRegister(1, ModbusDataType.S16, ScaleFactorProperty = nameof(PowerScaleFactor))]
        public partial decimal? Power { get; set; }

        [ModbusRegister(2, ModbusDataType.S16)]
        public partial short? PowerScaleFactor { get; set; }

        public partial ResolverChild? Child { get; set; }

        public partial ResolverUnit? Unit { get; set; }
    }

    [InterceptorSubject]
    public partial class ResolverChild : IModbusBaseAddressProvider
    {
        public int BaseAddress { get; init; }

        [ModbusRegister(5, ModbusDataType.U16, Space = ModbusAddressSpace.InputRegister)]
        public partial int? Value { get; set; }
    }

    [ModbusUnitId(7)]
    [InterceptorSubject]
    public partial class ResolverUnit
    {
        [ModbusRegister(0, ModbusDataType.U16)]
        public partial int? Value { get; set; }

        public partial ResolverChild? Nested { get; set; }
    }

    [ModbusUnitId(3)]
    [InterceptorSubject]
    public partial class ProviderWinsSubject : IModbusUnitIdProvider
    {
        public byte UnitId => 9;

        [ModbusRegister(0, ModbusDataType.U16)]
        public partial int? Value { get; set; }
    }

    [InterceptorSubject]
    public partial class ScaleAndScaleFactorSubject
    {
        [ModbusRegister(0, ModbusDataType.U16, Scale = 0.1, ScaleFactorProperty = nameof(Factor))]
        public partial decimal? Value { get; set; }

        [ModbusRegister(1, ModbusDataType.S16)]
        public partial short? Factor { get; set; }
    }

    [InterceptorSubject]
    public partial class MissingScaleFactorSubject
    {
        [ModbusRegister(0, ModbusDataType.U16, ScaleFactorProperty = "Missing")]
        public partial decimal? Value { get; set; }
    }

    [InterceptorSubject]
    public partial class StringWithoutLengthSubject
    {
        [ModbusRegister(0, ModbusDataType.String)]
        public partial string? Value { get; set; }
    }

    [InterceptorSubject]
    public partial class LengthOnIntegerSubject
    {
        [ModbusRegister(0, ModbusDataType.U16, Length = 2)]
        public partial int? Value { get; set; }
    }

    [InterceptorSubject]
    public partial class BooleanInRegisterSubject
    {
        [ModbusRegister(0, ModbusDataType.Boolean)]
        public partial bool? Value { get; set; }
    }

    [InterceptorSubject]
    public partial class IntegerInCoilSubject
    {
        [ModbusRegister(0, ModbusDataType.U16, Space = ModbusAddressSpace.Coil)]
        public partial int? Value { get; set; }
    }

    [InterceptorSubject]
    public partial class AddressOverflowSubject
    {
        [ModbusRegister(65535, ModbusDataType.U32)]
        public partial long? Value { get; set; }
    }

    [InterceptorSubject]
    public partial class NegativeAddressSubject
    {
        [ModbusRegister(-1, ModbusDataType.U16)]
        public partial int? Value { get; set; }
    }

    public sealed class PresetRegisterAttribute : ModbusRegisterAttribute
    {
        public PresetRegisterAttribute(int address)
            : base(address, ModbusDataType.S16)
        {
            Space = ModbusAddressSpace.InputRegister;
            NotAvailableValue = ModbusNotAvailableValue.SignedMaximum;
        }
    }

    [InterceptorSubject]
    public partial class PresetSubject
    {
        [PresetRegister(4)]
        public partial decimal? Value { get; set; }
    }

    [InterceptorSubject]
    public partial class DuplicateRegisterSubject
    {
        [ModbusRegister(0, ModbusDataType.S16)]
        [PresetRegister(1)]
        public partial int? Value { get; set; }
    }

    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithLifecycle();

    private static ModbusRegisterBinding Find(IEnumerable<ModbusRegisterBinding> bindings, IInterceptorSubject subject, string propertyName)
        => bindings.Single(binding => ReferenceEquals(binding.Property.Subject, subject) && binding.Property.Name == propertyName);

    [Fact]
    public void WhenSubjectTreeHasRegisters_ThenBindingsHaveAbsoluteAddressesAndUnitIds()
    {
        // Arrange
        var root = new ResolverRoot(CreateContext());
        var child = new ResolverChild { BaseAddress = 100 };
        var nested = new ResolverChild { BaseAddress = 200 };
        var unit = new ResolverUnit { Nested = nested };
        root.Child = child;
        root.Unit = unit;

        // Act
        var bindings = ModbusRegisterResolver.Resolve(root, defaultUnitId: 1, excludedProperties: new HashSet<PropertyReference>());

        // Assert
        Assert.Equal(6, bindings.Count);
        var rootBinding = Find(bindings, root, nameof(ResolverRoot.Value));
        Assert.Equal((byte)1, rootBinding.UnitId);
        Assert.Equal(0, rootBinding.Address);
        var childBinding = Find(bindings, child, nameof(ResolverChild.Value));
        Assert.Equal((byte)1, childBinding.UnitId);
        Assert.Equal(105, childBinding.Address);
        Assert.Equal(ModbusAddressSpace.InputRegister, childBinding.Space);
        Assert.Equal((byte)7, Find(bindings, unit, nameof(ResolverUnit.Value)).UnitId);
        var nestedBinding = Find(bindings, nested, nameof(ResolverChild.Value));
        Assert.Equal((byte)7, nestedBinding.UnitId);
        Assert.Equal(205, nestedBinding.Address);
    }

    [Fact]
    public void WhenScaleFactorPropertyIsSet_ThenBindingIsLinked()
    {
        // Arrange
        var root = new ResolverRoot(CreateContext());

        // Act
        var bindings = ModbusRegisterResolver.Resolve(root, 1, new HashSet<PropertyReference>());

        // Assert
        var power = Find(bindings, root, nameof(ResolverRoot.Power));
        Assert.Same(Find(bindings, root, nameof(ResolverRoot.PowerScaleFactor)), power.ScaleFactor);
    }

    [Fact]
    public void WhenPropertyIsExcluded_ThenNoBindingIsCreated()
    {
        // Arrange
        var root = new ResolverRoot(CreateContext());
        var excluded = new HashSet<PropertyReference> { new(root, nameof(ResolverRoot.Value)) };

        // Act
        var bindings = ModbusRegisterResolver.Resolve(root, 1, excluded);

        // Assert
        Assert.DoesNotContain(bindings, binding => binding.Property.Name == nameof(ResolverRoot.Value));
    }

    [Fact]
    public void WhenUnitIdProviderAndAttributeArePresent_ThenProviderWins()
    {
        // Arrange
        var subject = new ProviderWinsSubject(CreateContext());

        // Act
        var bindings = ModbusRegisterResolver.Resolve(subject, 1, new HashSet<PropertyReference>());

        // Assert
        Assert.Equal((byte)9, Assert.Single(bindings).UnitId);
    }

    [Fact]
    public void WhenAttributeIsDerivedWithPresets_ThenPresetsApply()
    {
        // Arrange
        var subject = new PresetSubject(CreateContext());

        // Act
        var bindings = ModbusRegisterResolver.Resolve(subject, 1, new HashSet<PropertyReference>());

        // Assert
        var binding = Assert.Single(bindings);
        Assert.Equal(4, binding.Address);
        Assert.Equal(ModbusAddressSpace.InputRegister, binding.Space);
        Assert.Null(binding.Reader(new byte[] { 0x7F, 0xFF }, 0));
    }

    [Fact]
    public void WhenPropertyHasMultipleRegisterAttributes_ThenConfigurationExceptionNamesProperty()
    {
        // Arrange
        var subject = new DuplicateRegisterSubject(CreateContext());

        // Act
        var exception = Assert.Throws<ModbusConfigurationException>(
            () => ModbusRegisterResolver.Resolve(subject, 1, new HashSet<PropertyReference>()));

        // Assert
        Assert.Contains($"{nameof(DuplicateRegisterSubject)}.{nameof(DuplicateRegisterSubject.Value)}", exception.Message);
    }

    public static TheoryData<Func<IInterceptorSubjectContext, IInterceptorSubject>> InvalidSubjects => new()
    {
        context => new ScaleAndScaleFactorSubject(context),
        context => new MissingScaleFactorSubject(context),
        context => new StringWithoutLengthSubject(context),
        context => new LengthOnIntegerSubject(context),
        context => new BooleanInRegisterSubject(context),
        context => new IntegerInCoilSubject(context),
        context => new AddressOverflowSubject(context),
        context => new NegativeAddressSubject(context),
    };

    [Theory]
    [MemberData(nameof(InvalidSubjects))]
    public void WhenMappingIsInvalid_ThenConfigurationExceptionIsThrown(Func<IInterceptorSubjectContext, IInterceptorSubject> createSubject)
    {
        // Arrange
        var subject = createSubject(CreateContext());

        // Act & Assert
        Assert.Throws<ModbusConfigurationException>(() => ModbusRegisterResolver.Resolve(subject, 1, new HashSet<PropertyReference>()));
    }
}
