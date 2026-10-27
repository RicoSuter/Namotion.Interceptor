using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

public class SubjectAttributeConflictTests
{
    [Fact]
    public void WhenTwoPropertiesAttachTheSameAttributeToOneProperty_ThenNI0066IsReported()
    {
        // Arrange
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Registry.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class Motor
                {
                    public partial bool Forward { get; set; }

                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public partial bool Forward_IsEnabled { get; set; }

                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public partial bool Forward_Enabled { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert: the shape compiles, which is why only the generator can report it.
        var diagnostic = result.GeneratorDiagnostics.Single(d => d.Id == "NI0066");
        var message = diagnostic.GetMessage();
        Assert.Contains("'Motor' attaches the attribute 'IsEnabled' to property 'Forward'", message);
        Assert.Contains("'Forward_IsEnabled'", message);
        Assert.Contains("'Forward_Enabled'", message);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void WhenAPropertyCollidesWithAnAncestorSubjectsProperty_ThenNI0066IsReportedOnce()
    {
        // Arrange
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Registry.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class BaseMotor
                {
                    public partial bool Forward { get; set; }

                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public partial bool Forward_IsEnabled { get; set; }
                }

                [InterceptorSubject]
                public partial class Motor : BaseMotor
                {
                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public partial bool Forward_Enabled { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        var diagnostic = result.GeneratorDiagnostics.Single(d => d.Id == "NI0066");
        Assert.StartsWith("'Motor' attaches", diagnostic.GetMessage());
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void WhenADuplicateLivesEntirelyInTheBaseSubject_ThenNI0066IsReportedOnTheBaseOnly()
    {
        // Arrange
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Registry.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class BaseMotor
                {
                    public partial bool Forward { get; set; }

                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public partial bool Forward_IsEnabled { get; set; }

                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public partial bool Forward_Enabled { get; set; }
                }

                [InterceptorSubject]
                public partial class Motor : BaseMotor
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        var diagnostic = result.GeneratorDiagnostics.Single(d => d.Id == "NI0066");
        Assert.StartsWith("'BaseMotor' attaches", diagnostic.GetMessage());
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void WhenAnExplicitInterfaceImplementationCarriesTheSameAttribute_ThenNI0066IsNotReported()
    {
        // Arrange
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Registry.Attributes;

            namespace Repro
            {
                public interface IMotorFlags
                {
                    bool IsEnabled { get; }
                }

                [InterceptorSubject]
                public partial class Motor : IMotorFlags
                {
                    public partial bool Forward { get; set; }

                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public partial bool Forward_IsEnabled { get; set; }

                    [PropertyAttribute("Forward", "IsEnabled")]
                    bool IMotorFlags.IsEnabled => true;
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0066");
    }

    [Fact]
    public void WhenAnOverrideRepeatsTheAttributeOfItsBaseProperty_ThenNI0066IsNotReported()
    {
        // Arrange: an override shares the property key with the base declaration, so there is one attribute, not two.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Registry.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class BaseMotor
                {
                    public partial bool Forward { get; set; }

                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public virtual partial bool Forward_IsEnabled { get; set; }
                }

                [InterceptorSubject]
                public partial class Motor : BaseMotor
                {
                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public override partial bool Forward_IsEnabled { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0066");
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void WhenAnOverrideInheritsTheAttributeOfItsBaseProperty_ThenNI0066IsReportedForANewDuplicate()
    {
        // Arrange: at runtime the override inherits the base declaration's attribute, so Forward_Enabled repeats its pair.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Registry.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class BaseMotor
                {
                    public partial bool Forward { get; set; }

                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public virtual partial bool Forward_IsEnabled { get; set; }
                }

                [InterceptorSubject]
                public partial class Motor : BaseMotor
                {
                    public override partial bool Forward_IsEnabled { get; set; }

                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public partial bool Forward_Enabled { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        var diagnostic = result.GeneratorDiagnostics.Single(d => d.Id == "NI0066");
        Assert.StartsWith("'Motor' attaches", diagnostic.GetMessage());
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void WhenAnOverrideOnlyInheritsOneOfABaseDuplicate_ThenNI0066IsReportedOnTheBaseOnly()
    {
        // Arrange: both attributes are declared in BaseMotor; Motor only overrides one of the two providers.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Registry.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class BaseMotor
                {
                    public partial bool Forward { get; set; }

                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public virtual partial bool Forward_IsEnabled { get; set; }

                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public partial bool Forward_Enabled { get; set; }
                }

                [InterceptorSubject]
                public partial class Motor : BaseMotor
                {
                    public override partial bool Forward_IsEnabled { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        var diagnostic = result.GeneratorDiagnostics.Single(d => d.Id == "NI0066");
        Assert.StartsWith("'BaseMotor' attaches", diagnostic.GetMessage());
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void WhenTwoPropertiesAttachDifferentAttributesToOneProperty_ThenNI0066IsNotReported()
    {
        // Arrange
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Registry.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class Tire
                {
                    public partial decimal Pressure { get; set; }

                    [PropertyAttribute(nameof(Pressure), "Minimum")]
                    public partial decimal Pressure_Minimum { get; set; }

                    [PropertyAttribute(nameof(Pressure), "Maximum")]
                    public partial decimal Pressure_Maximum { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0066");
        Assert.Empty(result.CompilationErrors);
    }
}
