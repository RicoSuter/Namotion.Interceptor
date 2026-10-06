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

        [ModbusRegister(5, ModbusDataType.U16, AddressSpace = ModbusAddressSpace.InputRegister)]
        public partial int? Value { get; set; }
    }

    [InterceptorSubject]
    public partial class ResolverUnit : IModbusUnitIdProvider
    {
        public byte UnitId => 7;

        [ModbusRegister(0, ModbusDataType.U16)]
        public partial int? Value { get; set; }

        public partial ResolverChild? Nested { get; set; }
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
        [ModbusRegister(0, ModbusDataType.U16, AddressSpace = ModbusAddressSpace.Coil)]
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

    [AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
    public sealed class PresetRegisterAttribute : ModbusRegisterAttribute
    {
        public PresetRegisterAttribute(int address)
            : base(address, ModbusDataType.S16)
        {
            AddressSpace = ModbusAddressSpace.InputRegister;
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

    [InterceptorSubject]
    public partial class HugeAddressSubject
    {
        [ModbusRegister(int.MaxValue, ModbusDataType.U32)]
        public partial long? Value { get; set; }
    }

    [InterceptorSubject]
    public partial class HugeBaseAddressSubject : IModbusBaseAddressProvider
    {
        public int BaseAddress => int.MaxValue - 1;

        [ModbusRegister(1, ModbusDataType.U32)]
        public partial long? Value { get; set; }
    }

    [InterceptorSubject]
    public partial class UndefinedDataTypeSubject
    {
        [ModbusRegister(0, (ModbusDataType)99)]
        public partial int? Value { get; set; }
    }

    [InterceptorSubject]
    public partial class UndefinedSpaceSubject
    {
        [ModbusRegister(0, ModbusDataType.U16, AddressSpace = (ModbusAddressSpace)99)]
        public partial int? Value { get; set; }
    }

    [InterceptorSubject]
    public partial class UndefinedWordOrderSubject
    {
        [ModbusRegister(0, ModbusDataType.U32, WordOrder = (ModbusWordOrder)99)]
        public partial long? Value { get; set; }
    }

    [InterceptorSubject]
    public partial class UndefinedNotAvailableValueSubject
    {
        [ModbusRegister(0, ModbusDataType.U16, NotAvailableValue = (ModbusNotAvailableValue)99)]
        public partial int? Value { get; set; }
    }

    [InterceptorSubject]
    public partial class FloatScaleFactorSubject
    {
        [ModbusRegister(0, ModbusDataType.S16, ScaleFactorProperty = nameof(Factor))]
        public partial decimal? Value { get; set; }

        [ModbusRegister(1, ModbusDataType.F32)]
        public partial float? Factor { get; set; }
    }

    [InterceptorSubject]
    public partial class U16ScaleFactorSubject
    {
        [ModbusRegister(0, ModbusDataType.S16, ScaleFactorProperty = nameof(Factor))]
        public partial decimal? Value { get; set; }

        [ModbusRegister(1, ModbusDataType.U16)]
        public partial int? Factor { get; set; }
    }

    [InterceptorSubject]
    public partial class U32ScaleFactorSubject
    {
        [ModbusRegister(0, ModbusDataType.S16, ScaleFactorProperty = nameof(Factor))]
        public partial decimal? Value { get; set; }

        [ModbusRegister(1, ModbusDataType.U32)]
        public partial long? Factor { get; set; }
    }

    [InterceptorSubject]
    public partial class ContainerSubject
    {
        public partial List<ResolverChild> Items { get; set; }

        public partial Dictionary<string, ResolverChild> ItemsByName { get; set; }

        public ContainerSubject()
        {
            Items = [];
            ItemsByName = [];
        }
    }

    [InterceptorSubject]
    public partial class SharedChildSubject
    {
        public partial ResolverChild? First { get; set; }

        public partial ResolverChild? Second { get; set; }
    }

    [InterceptorSubject]
    public partial class CycleSubject
    {
        [ModbusRegister(0, ModbusDataType.U16)]
        public partial int? Value { get; set; }

        public partial CycleSubject? Next { get; set; }
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
        Assert.Equal(ModbusAddressSpace.InputRegister, childBinding.AddressSpace);
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
    public void WhenScaleIsCombinedWithScaleFactorProperty_ThenBindingIsLinked()
    {
        // Arrange
        var subject = new ScaleAndScaleFactorSubject(CreateContext());

        // Act
        var bindings = ModbusRegisterResolver.Resolve(subject, 1, new HashSet<PropertyReference>());

        // Assert
        var value = Find(bindings, subject, nameof(ScaleAndScaleFactorSubject.Value));
        Assert.Same(Find(bindings, subject, nameof(ScaleAndScaleFactorSubject.Factor)), value.ScaleFactor);
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
    public void WhenAttributeIsDerivedWithPresets_ThenPresetsApply()
    {
        // Arrange
        var subject = new PresetSubject(CreateContext());

        // Act
        var bindings = ModbusRegisterResolver.Resolve(subject, 1, new HashSet<PropertyReference>());

        // Assert
        var binding = Assert.Single(bindings);
        Assert.Equal(4, binding.Address);
        Assert.Equal(ModbusAddressSpace.InputRegister, binding.AddressSpace);
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
        Assert.StartsWith("Invalid Modbus mapping on Value:", exception.Message);
    }

    [Fact]
    public void WhenSubjectsAreCollectionAndDictionaryItems_ThenEachItemIsResolvedWithItsOwnPath()
    {
        // Arrange
        var root = new ContainerSubject(CreateContext());
        var first = new ResolverChild { BaseAddress = 100 };
        var second = new ResolverChild { BaseAddress = 200 };
        var named = new ResolverChild { BaseAddress = 300 };
        root.Items = [first, second];
        root.ItemsByName = new Dictionary<string, ResolverChild> { ["pump"] = named };

        // Act
        var bindings = ModbusRegisterResolver.Resolve(root, 1, new HashSet<PropertyReference>());

        // Assert
        Assert.Equal(3, bindings.Count);
        Assert.Equal("Items[0].Value", Find(bindings, first, nameof(ResolverChild.Value)).Path);
        Assert.Equal("Items[1].Value", Find(bindings, second, nameof(ResolverChild.Value)).Path);
        Assert.Equal("ItemsByName[pump].Value", Find(bindings, named, nameof(ResolverChild.Value)).Path);
        Assert.Equal(305, Find(bindings, named, nameof(ResolverChild.Value)).Address);
    }

    [Fact]
    public void WhenChildSubjectIsReferencedTwice_ThenItIsResolvedOnce()
    {
        // Arrange
        var root = new SharedChildSubject(CreateContext());
        var shared = new ResolverChild { BaseAddress = 100 };
        root.First = shared;
        root.Second = shared;

        // Act
        var bindings = ModbusRegisterResolver.Resolve(root, 1, new HashSet<PropertyReference>());

        // Assert
        Assert.Equal(105, Assert.Single(bindings).Address);
    }

    [Fact]
    public void WhenSubjectGraphHasCycle_ThenEachSubjectIsResolvedOnce()
    {
        // Arrange
        var root = new CycleSubject(CreateContext());
        var next = new CycleSubject();
        root.Next = next;
        next.Next = root;

        // Act
        var bindings = ModbusRegisterResolver.Resolve(root, 1, new HashSet<PropertyReference>());

        // Assert
        Assert.Equal(2, bindings.Count);
        Assert.Equal("Value", Find(bindings, root, nameof(CycleSubject.Value)).Path);
        Assert.Equal("Next.Value", Find(bindings, next, nameof(CycleSubject.Value)).Path);
    }

    [Fact]
    public void WhenScaleFactorPropertyIsExcluded_ThenConfigurationExceptionIsThrown()
    {
        // Arrange
        var root = new ResolverRoot(CreateContext());
        var excluded = new HashSet<PropertyReference> { new(root, nameof(ResolverRoot.PowerScaleFactor)) };

        // Act
        var exception = Assert.Throws<ModbusConfigurationException>(() => ModbusRegisterResolver.Resolve(root, 1, excluded));

        // Assert
        Assert.StartsWith("Invalid Modbus mapping on Power:", exception.Message);
    }

    public static TheoryData<Func<IInterceptorSubjectContext, IInterceptorSubject>> InvalidSubjects => new()
    {
        context => new MissingScaleFactorSubject(context),
        context => new FloatScaleFactorSubject(context),
        context => new U16ScaleFactorSubject(context),
        context => new U32ScaleFactorSubject(context),
        context => new StringWithoutLengthSubject(context),
        context => new LengthOnIntegerSubject(context),
        context => new BooleanInRegisterSubject(context),
        context => new IntegerInCoilSubject(context),
        context => new AddressOverflowSubject(context),
        context => new NegativeAddressSubject(context),
        context => new HugeAddressSubject(context),
        context => new HugeBaseAddressSubject(context),
        context => new UndefinedDataTypeSubject(context),
        context => new UndefinedSpaceSubject(context),
        context => new UndefinedWordOrderSubject(context),
        context => new UndefinedNotAvailableValueSubject(context),
    };

    [Theory]
    [MemberData(nameof(InvalidSubjects))]
    public void WhenMappingIsInvalid_ThenConfigurationExceptionNamesProperty(Func<IInterceptorSubjectContext, IInterceptorSubject> createSubject)
    {
        // Arrange
        var subject = createSubject(CreateContext());

        // Act
        var exception = Assert.Throws<ModbusConfigurationException>(
            () => ModbusRegisterResolver.Resolve(subject, 1, new HashSet<PropertyReference>()));

        // Assert
        Assert.StartsWith("Invalid Modbus mapping on Value:", exception.Message);
    }
}
