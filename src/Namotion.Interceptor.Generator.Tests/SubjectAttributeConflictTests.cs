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
    public void WhenOverridesInheritADuplicateFromAPlainBaseClass_ThenNI0066IsReportedOnTheSubject()
    {
        // Arrange: no subject declares the attributes, so only the subject that inherits both can report them.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Registry.Attributes;

            namespace Repro
            {
                public class PlainMotorBase
                {
                    [PropertyAttribute("Forward", "IsEnabled")]
                    public virtual bool Forward_IsEnabled { get; set; }

                    [PropertyAttribute("Forward", "IsEnabled")]
                    public virtual bool Forward_Enabled { get; set; }
                }

                [InterceptorSubject]
                public partial class Motor : PlainMotorBase
                {
                    public partial bool Forward { get; set; }

                    public override partial bool Forward_IsEnabled { get; set; }

                    public override partial bool Forward_Enabled { get; set; }
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
    public void WhenAPlainBaseAttributeReachesASubjectThroughASubjectOverride_ThenNI0066IsReportedOnTheBaseSubjectOnly()
    {
        // Arrange: BaseMotor overrides the plain declaration and adds the duplicate, so BaseMotor owns it and
        // Motor, which only overrides the same property again, adds nothing.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Registry.Attributes;

            namespace Repro
            {
                public class PlainMotorBase
                {
                    [PropertyAttribute("Forward", "IsEnabled")]
                    public virtual bool Forward_IsEnabled { get; set; }
                }

                [InterceptorSubject]
                public partial class BaseMotor : PlainMotorBase
                {
                    public partial bool Forward { get; set; }

                    public override partial bool Forward_IsEnabled { get; set; }

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
    public void WhenAnOverrideRestatesTheAttributeOfABaseDuplicate_ThenNI0066IsReportedOnTheBaseOnly()
    {
        // Arrange: restating the base's attribute on the override keeps the same two providers as BaseMotor.
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
                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
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
    public void WhenASetOnlyOverrideInheritsThroughAGetOnlyOverride_ThenNI0066FollowsTheSetterLikeTheRuntime()
    {
        // Arrange: at runtime a set-only override inherits the attribute of the declaration its setter overrides,
        // which skips the get-only PlainGetOverride, so Split carries IsEnabled and nothing repeats Other.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Registry.Attributes;

            namespace Repro
            {
                public class PlainBase
                {
                    [PropertyAttribute("Forward", "IsEnabled")]
                    public virtual bool Split { get; set; }
                }

                public class PlainGetOverride : PlainBase
                {
                    [PropertyAttribute("Forward", "Other")]
                    public override bool Split { get => base.Split; }
                }

                [InterceptorSubject]
                public partial class Motor : PlainGetOverride
                {
                    public partial bool Forward { get; set; }

                    public override partial bool Split { set; }

                    [PropertyAttribute(nameof(Forward), "Other")]
                    public partial bool Forward_Other { get; set; }
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
    public void WhenASetOnlyOverrideInheritsPastAGetOnlySubjectOverride_ThenNI0066IsReportedForTheRuntimeDuplicate()
    {
        // Arrange: Split's setter overrides BaseMotor's, so at runtime Split carries IsEnabled, which
        // Forward_IsEnabled repeats.
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
                    public virtual partial bool Split { get; set; }
                }

                [InterceptorSubject]
                public partial class MiddleMotor : BaseMotor
                {
                    [PropertyAttribute(nameof(Forward), "Other")]
                    public override partial bool Split { get; }
                }

                [InterceptorSubject]
                public partial class Motor : MiddleMotor
                {
                    public override partial bool Split { set; }

                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public partial bool Forward_IsEnabled { get; set; }
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
    public void WhenASetOnlyOverrideSkipsAGetOnlySubjectOverrideIntoADuplicate_ThenNI0066IsReportedOnTheSubject()
    {
        // Arrange: at runtime Motor's Split inherits IsEnabled through its setter from BaseMotor, so it collides with
        // Forward_IsEnabled, while MiddleMotor's Split carries Other and MiddleMotor has no duplicate.
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
                    public virtual partial bool Split { get; set; }
                }

                [InterceptorSubject]
                public partial class MiddleMotor : BaseMotor
                {
                    [PropertyAttribute(nameof(Forward), "Other")]
                    public override partial bool Split { get; }

                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public partial bool Forward_IsEnabled { get; set; }
                }

                [InterceptorSubject]
                public partial class Motor : MiddleMotor
                {
                    public override partial bool Split { set; }
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
    public void WhenASetOnlyOverrideRestatesAMiddleSubjectsDuplicate_ThenNI0066IsReportedOnTheMiddleSubjectOnly()
    {
        // Arrange: Motor's Split restates IsEnabled, so Motor has the same two providers as MiddleMotor.
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

                    [PropertyAttribute(nameof(Forward), "Other")]
                    public virtual partial bool Split { get; set; }
                }

                [InterceptorSubject]
                public partial class MiddleMotor : BaseMotor
                {
                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public override partial bool Split { get; }

                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public partial bool Forward_IsEnabled { get; set; }
                }

                [InterceptorSubject]
                public partial class Motor : MiddleMotor
                {
                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public override partial bool Split { set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        var diagnostic = result.GeneratorDiagnostics.Single(d => d.Id == "NI0066");
        Assert.StartsWith("'MiddleMotor' attaches", diagnostic.GetMessage());
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void WhenACovariantOverrideDoesNotInheritTheBaseAttribute_ThenNI0066IsNotReported()
    {
        // Arrange: the runtime looks up the parent property by type, so a covariant-return override inherits nothing.
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
                    public virtual object Value => "o";
                }

                [InterceptorSubject]
                public partial class Motor : BaseMotor
                {
                    public override string Value => "s";

                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public partial bool Forward_IsEnabled { get; set; }
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
    public void WhenASubjectAddsAThirdProviderToABaseDuplicate_ThenItReportsOnlyTheProviderItAdds()
    {
        // Arrange: BaseMotor reports Forward_IsEnabled and Forward_Enabled; Motor only adds Forward_On.
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
                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public partial bool Forward_On { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        var diagnostics = result.GeneratorDiagnostics.Where(d => d.Id == "NI0066").ToList();
        Assert.Single(diagnostics, d => d.GetMessage().StartsWith("'BaseMotor' attaches", StringComparison.Ordinal));
        var motorDiagnostic = Assert.Single(diagnostics, d => d.GetMessage().StartsWith("'Motor' attaches", StringComparison.Ordinal));
        Assert.Contains("'Forward_On'", motorDiagnostic.GetMessage());
        Assert.Equal(2, diagnostics.Count);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void WhenAnExplicitImplementationDisplacesAnAncestorProvider_ThenNI0066IsNotReported()
    {
        // Arrange: Motor's IMotorFlags.Forward_IsEnabled takes that name from BaseMotor and carries no attribute in the
        // metadata, so Forward_Enabled is the only provider left. NI0065 reports the displacement itself.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Registry.Attributes;

            namespace Repro
            {
                public interface IMotorFlags
                {
                    bool Forward_IsEnabled { get; set; }
                }

                [InterceptorSubject]
                public partial class BaseMotor
                {
                    public partial bool Forward { get; set; }

                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public partial bool Forward_IsEnabled { get; set; }
                }

                [InterceptorSubject]
                public partial class Motor : BaseMotor, IMotorFlags
                {
                    bool IMotorFlags.Forward_IsEnabled { get; set; }

                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public partial bool Forward_Enabled { get; set; }
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
    public void WhenADynamicOverrideInheritsTheAttributeOfAnObjectProperty_ThenNI0066IsReported()
    {
        // Arrange: dynamic is System.Object at runtime, so the override inherits the base attribute.
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
                    public virtual object Value { get; set; }
                }

                [InterceptorSubject]
                public partial class Motor : BaseMotor
                {
                    public override dynamic Value { get => base.Value; set => base.Value = value; }

                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public partial bool Forward_IsEnabled { get; set; }
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
    public void WhenAnOverrideDiffersOnlyByANestedDynamic_ThenNI0066SeesTheInheritedAttribute()
    {
        // Arrange: List<dynamic> and List<object> are one type at runtime, so the override inherits the attribute.
        const string source = """
            using System.Collections.Generic;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Registry.Attributes;

            namespace Repro
            {
                public class PlainMotorBase
                {
                    [PropertyAttribute("Forward", "IsEnabled")]
                    public virtual List<object> Value { get; set; }
                }

                [InterceptorSubject]
                public partial class Motor : PlainMotorBase
                {
                    public partial bool Forward { get; set; }

                    public override List<dynamic> Value { get => base.Value; set => base.Value = value; }

                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public partial bool Forward_IsEnabled { get; set; }
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
    public void WhenAnOverrideDiffersOnlyByTupleElementNames_ThenNI0066SeesTheInheritedAttribute()
    {
        // Arrange: tuple element names do not exist at runtime, so the override inherits the attribute.
        const string source = """
            using System.Collections.Generic;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Registry.Attributes;

            namespace Repro
            {
                public class PlainMotorBase
                {
                    [PropertyAttribute("Forward", "IsEnabled")]
                    public virtual (int A, int B) Value { get; set; }
                }

                [InterceptorSubject]
                public partial class Motor : PlainMotorBase
                {
                    public partial bool Forward { get; set; }

                    public override (int, int) Value { get => base.Value; set => base.Value = value; }

                    [PropertyAttribute(nameof(Forward), "IsEnabled")]
                    public partial bool Forward_IsEnabled { get; set; }
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
    public void WhenABaseSubjectFromAnotherAssemblyHasAPropertyNamedProperties_ThenNI0066StillSeesIt()
    {
        // Arrange: from metadata, MiddleMotor also exposes its generated IInterceptorSubject.Properties
        // implementation, which is not part of the property set and must not hide BaseMotor.Properties.
        const string librarySource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Registry.Attributes;

            namespace Library
            {
                [InterceptorSubject]
                public partial class BaseMotor
                {
                    public partial string Name { get; set; }

                    [PropertyAttribute(nameof(Name), "Unit")]
                    public partial string Properties { get; set; }
                }

                [InterceptorSubject]
                public partial class MiddleMotor : BaseMotor
                {
                    public partial int Speed { get; set; }
                }
            }
            """;

        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Registry.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class Motor : Library.MiddleMotor
                {
                    [PropertyAttribute(nameof(Name), "Unit")]
                    public partial string Name_Unit { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunWithLibraryReference(librarySource, mainSource, runGeneratorOverLibrary: true);

        // Assert
        var diagnostic = result.GeneratorDiagnostics.Single(d => d.Id == "NI0066");
        Assert.StartsWith("'Motor' attaches", diagnostic.GetMessage());
        Assert.Contains("'Properties'", diagnostic.GetMessage());
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
