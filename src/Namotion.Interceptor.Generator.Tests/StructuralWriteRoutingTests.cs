using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

/// <summary>
/// Pins the generation-time routing between the scalar SetPropertyValue accessor helper and the
/// attachment-guarded SetStructuralPropertyValue one. The classification fails closed (see
/// PropertyWriteRouting): the scalar route is emitted only for declared types that provably cannot
/// hold a subject, and everything else, a same-compilation subject included, routes structurally.
/// </summary>
public class StructuralWriteRoutingTests
{
    [Fact]
    public void WhenPropertyTypesAreOnTheScalarAllowlist_ThenEverySetterRoutesThroughTheScalarHelper()
    {
        // Arrange: one property per allowlist family, plus the Nullable forms.
        const string source = """
            using System;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public enum Level { Low, High }

                [InterceptorSubject]
                public partial class ScalarSubject
                {
                    public partial int Count { get; set; }
                    public partial string Name { get; set; }
                    public partial decimal Price { get; set; }
                    public partial DateTime CreatedAt { get; set; }
                    public partial DateTimeOffset UpdatedAt { get; set; }
                    public partial TimeSpan Duration { get; set; }
                    public partial Guid Identifier { get; set; }
                    public partial Level Severity { get; set; }
                    public partial int? OptionalCount { get; set; }
                    public partial Level? OptionalSeverity { get; set; }
                }
            }
            """;

        // Act
        var generated = GeneratorTestHost.RunExpectingNoWarnings(source).SingleSource();

        // Assert: every setter takes the scalar route, and no setter in the whole subject carries
        // the structural helper call, so the scalar path gains no attachment-revision check.
        foreach (var propertyName in new[]
                 {
                     "Count", "Name", "Price", "CreatedAt", "UpdatedAt", "Duration",
                     "Identifier", "Severity", "OptionalCount", "OptionalSeverity"
                 })
        {
            Assert.Contains($"&& SetPropertyValue(nameof({propertyName})", generated);
        }

        Assert.DoesNotContain("SetStructuralPropertyValue(nameof(", generated);
    }

    [Fact]
    public void WhenPropertyTypeIsASameCompilationSubject_ThenTheSetterRoutesThroughTheStructuralHelper()
    {
        // Arrange: the case symbol inspection cannot answer from the interface list, because the
        // generator emits the IInterceptorSubject base-list entry itself and the symbol shows none
        // of it. Fail-closed routing must not depend on that.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class Child
                {
                    public partial string Name { get; set; }
                }

                [InterceptorSubject]
                public partial class Parent
                {
                    public partial Child? Node { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var parent = Assert.Single(result.Sources, s => s.HintName.Contains("Repro.Parent.g.cs")).SourceText.ToString();

        // Assert
        Assert.Contains("&& SetStructuralPropertyValue(nameof(Node)", parent);
    }

    [Fact]
    public void WhenPropertyTypeIsObject_ThenTheSetterRoutesThroughTheStructuralHelper()
    {
        // Arrange
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class Holder
                {
                    public partial object? Payload { get; set; }
                }
            }
            """;

        // Act
        var generated = GeneratorTestHost.RunExpectingNoWarnings(source).SingleSource();

        // Assert
        Assert.Contains("&& SetStructuralPropertyValue(nameof(Payload)", generated);
    }

    [Fact]
    public void WhenPropertyTypeIsAnInterface_ThenTheSetterRoutesThroughTheStructuralHelper()
    {
        // Arrange
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public interface IDevice
                {
                }

                [InterceptorSubject]
                public partial class Holder
                {
                    public partial IDevice? Device { get; set; }
                }
            }
            """;

        // Act
        var generated = GeneratorTestHost.RunExpectingNoWarnings(source).SingleSource();

        // Assert
        Assert.Contains("&& SetStructuralPropertyValue(nameof(Device)", generated);
    }

    [Fact]
    public void WhenPropertyTypeIsASubjectCollection_ThenTheSetterRoutesThroughTheStructuralHelper()
    {
        // Arrange
        const string source = """
            using System.Collections.Generic;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class Child
                {
                    public partial string Name { get; set; }
                }

                [InterceptorSubject]
                public partial class Parent
                {
                    public partial List<Child> Children { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var parent = Assert.Single(result.Sources, s => s.HintName.Contains("Repro.Parent.g.cs")).SourceText.ToString();

        // Assert
        Assert.Contains("&& SetStructuralPropertyValue(nameof(Children)", parent);
    }

    [Fact]
    public void WhenPropertyTypeIsUnresolved_ThenTheSetterRoutesThroughTheStructuralHelper()
    {
        // Arrange: the type does not exist, so the compilation has errors by construction, but the
        // generator still emits and the classification must fail closed rather than guess scalar.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class Holder
                {
                    public partial UndefinedWidget Widget { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);
        var generated = result.SingleSource();

        // Assert
        Assert.Contains("&& SetStructuralPropertyValue(nameof(Widget)", generated);
    }
}
