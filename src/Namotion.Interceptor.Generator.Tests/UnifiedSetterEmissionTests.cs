using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

/// <summary>
/// Pins that generated setters preserve the scalar fast path while types that may contain subjects
/// route through the generated structural entry. Runtime classification keeps ambiguous reference
/// types conservative without paying reflection per assignment.
/// </summary>
public class UnifiedSetterEmissionTests
{
    [Fact]
    public void WhenPropertiesSpanScalarAndSubjectBearingTypes_ThenOnlyPotentiallyStructuralSettersUseGeneratedCoordination()
    {
        // Arrange: scalar allowlist samples next to every shape the old generation-time routing
        // sent through the structural helper (same-compilation subject, object, interface,
        // subject collection).
        const string source = """
            using System;
            using System.Collections.Generic;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public interface IDevice
                {
                }

                [InterceptorSubject]
                public partial class Child
                {
                    public partial string Name { get; set; }
                }

                [InterceptorSubject]
                public partial class Parent
                {
                    public partial int Count { get; set; }
                    public partial string Title { get; set; }
                    public partial DateTimeOffset? UpdatedAt { get; set; }
                    public partial Child? Node { get; set; }
                    public partial object? Payload { get; set; }
                    public partial IDevice? Device { get; set; }
                    public partial List<Child> Children { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var parent = Assert.Single(result.Sources, s => s.HintName.Contains("Repro.Parent.g.cs")).SourceText.ToString();

        // Assert
        foreach (var propertyName in new[] { "Count", "Title", "UpdatedAt" })
        {
            Assert.Contains($"&& SetPropertyValue(nameof({propertyName})", parent);
        }

        foreach (var propertyName in new[] { "Node", "Payload", "Device", "Children" })
        {
            Assert.Contains($".SetGeneratedPropertyValue(nameof({propertyName})", parent);
            Assert.Contains($": SetPropertyValue(nameof({propertyName})", parent);
            Assert.DoesNotContain($"On{propertyName}Changed(_{propertyName})", parent);
        }

        Assert.Contains("executeInterceptors: false", parent);
    }

    [Fact]
    public void WhenPropertyTypeIsUnresolved_ThenTheSetterFailsClosedToGeneratedStructuralCoordination()
    {
        // Arrange: the type does not exist, so the compilation has errors by construction, but the
        // generator still emits the same setter shape as for any other type.
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
        Assert.Contains(".SetGeneratedPropertyValue(nameof(Widget)", generated);
        Assert.Contains(": SetPropertyValue(nameof(Widget)", generated);
        Assert.Contains("executeInterceptors: false", generated);
        Assert.DoesNotContain("OnWidgetChanged(_Widget)", generated);
    }

    [Fact]
    public void WhenValueTypeCanContainSubjects_ThenItsSetterUsesGeneratedStructuralCoordination()
    {
        // Arrange
        const string source = """
            using System.Collections;
            using System.Collections.Generic;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class Child
                {
                }

                public readonly struct ChildCollection : IEnumerable<Child>
                {
                    public IEnumerator<Child> GetEnumerator() => throw new System.NotSupportedException();
                    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
                }

                [InterceptorSubject]
                public partial class Holder
                {
                    public partial ChildCollection Children { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var generated = Assert.Single(result.Sources, s => s.HintName.Contains("Repro.Holder.g.cs")).SourceText.ToString();

        // Assert
        Assert.Contains(".SetGeneratedPropertyValue(nameof(Children)", generated);
        Assert.Contains("executeInterceptors: false", generated);
    }

    [Fact]
    public void WhenConsumerNamespaceShadowsNamotion_ThenAmbiguousPropertyCompilesAndUsesStructuralRouting()
    {
        // Arrange
        const string source = """
            namespace Repro.Namotion
            {
                public sealed class NamespaceShadow
                {
                }
            }

            namespace Repro
            {
                [global::Namotion.Interceptor.Attributes.InterceptorSubject]
                public partial class Child
                {
                }

                [global::Namotion.Interceptor.Attributes.InterceptorSubject]
                public partial class Holder
                {
                    public partial object? Value { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);

        var assembly = result.LoadAssembly();
        var holderType = assembly.GetType("Repro.Holder");
        var childType = assembly.GetType("Repro.Child");
        Assert.NotNull(holderType);
        Assert.NotNull(childType);
        var holder = Activator.CreateInstance(holderType);
        var child = Activator.CreateInstance(childType);
        var property = holderType.GetProperty("Value");
        Assert.NotNull(holder);
        Assert.NotNull(child);
        Assert.NotNull(property);

        // Act
        property.SetValue(holder, child);

        // Assert
        Assert.Same(child, property.GetValue(holder));
        var executorField = holderType.GetField(
            "_executor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(executorField);
        var executor = executorField.GetValue(holder);
        Assert.NotNull(executor);
        var revisionField = executor.GetType().GetField(
            "Revision",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(revisionField);
        Assert.Equal(1L, revisionField.GetValue(executor));
    }
}
