using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

public class SubjectReplayInterfaceTests
{
    [Theory]
    [InlineData("public void ReplayValue(int value, ref global::Namotion.Interceptor.PropertyReplayOutcome outcome) => throw new System.InvalidOperationException();")]
    [InlineData("public int ReplayValue => 7;")]
    [InlineData("public class ReplayValue { }")]
    [InlineData("public int ReplayValueWithoutInterceptor(int value) => value + 1;")]
    public void WhenUserMemberUsesReplayHelperName_ThenReplayReachesTheProperty(string declaration)
    {
        // Arrange
        var source = $$"""
            [Namotion.Interceptor.Attributes.InterceptorSubject]
            public partial class ReplaySubject
            {
                public partial int Value { get; set; }
                {{declaration}}
            }
            """;
        var result = GeneratorTestHost.RunForExecution(source);
        var subject = (IInterceptorSubject)result.CreateInstance("ReplaySubject");
        var outcome = default(PropertyReplayOutcome);

        // Act
        var canReplay = ((ISubjectPropertyReplay)subject).CanReplayProperty("Value");
        ((ISubjectPropertyReplay)subject).ReplayProperty("Value", 2, ref outcome);

        // Assert
        Assert.True(canReplay);
        Assert.Equal(2, subject.Properties["Value"].GetValue!(subject));
        Assert.True(outcome.Accepted);
        Assert.True(outcome.Mutated);
        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Theory]
    [InlineData("PropertyReplayOutcome")]
    [InlineData("ISubjectPropertyReplay")]
    [InlineData("NotSupportedException")]
    public void WhenUserTypeShadowsReplayDependency_ThenReplayUsesTheLibraryContract(string typeName)
    {
        // Arrange
        var source = $$"""
            namespace Repro
            {
                public class {{typeName}} { }

                [Namotion.Interceptor.Attributes.InterceptorSubject]
                public partial class ReplaySubject
                {
                    public partial int Value { get; set; }
                }
            }
            """;
        var result = GeneratorTestHost.RunForExecution(source);
        var subject = (IInterceptorSubject)result.CreateInstance("Repro.ReplaySubject");
        var outcome = default(PropertyReplayOutcome);

        // Act
        var canReplay = ((ISubjectPropertyReplay)subject).CanReplayProperty("Value");
        ((ISubjectPropertyReplay)subject).ReplayProperty("Value", 2, ref outcome);

        // Assert
        Assert.True(canReplay);
        Assert.Equal(2, subject.Properties["Value"].GetValue!(subject));
        Assert.True(outcome.Accepted);
        Assert.True(outcome.Mutated);
        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Theory]
    [InlineData("void global::Namotion.Interceptor.ISubjectPropertyReplay.ReplayProperty")]
    [InlineData("public void ReplayProperty")]
    public void WhenSubjectImplementsReplayContract_ThenUserReplayAndGeneratedSetterRemainActive(string methodDeclaration)
    {
        // Arrange
        var source = $$"""
            [Namotion.Interceptor.Attributes.InterceptorSubject]
            public partial class ReplaySubject : Namotion.Interceptor.ISubjectPropertyReplay
            {
                public partial int Value { get; set; }
                public bool CanReplayProperty(string propertyName) => propertyName == "Value";
                {{methodDeclaration}}(string propertyName, object value, ref Namotion.Interceptor.PropertyReplayOutcome outcome)
                {
                    Value = (int)value + 1;
                    outcome.Accepted = true;
                    outcome.Mutated = true;
                }
            }
            """;
        var result = GeneratorTestHost.RunForExecution(source);
        var subject = (IInterceptorSubject)result.CreateInstance("ReplaySubject");
        var interceptor = new RecordingWriteInterceptor();
        subject.Context.AddFallbackContext(InterceptorSubjectContext.Create().WithService(() => interceptor));
        var outcome = default(PropertyReplayOutcome);

        // Act
        var canReplay = ((ISubjectPropertyReplay)subject).CanReplayProperty("Value");
        ((ISubjectPropertyReplay)subject).ReplayProperty("Value", 2, ref outcome);

        // Assert
        Assert.True(canReplay);
        Assert.Equal(3, subject.Properties["Value"].GetValue!(subject));
        Assert.Contains(interceptor.Writes, write => write.PropertyName == "Value" && Equals(write.Value, 3));
        Assert.True(outcome.Accepted);
        Assert.True(outcome.Mutated);
        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenSubjectOnlyImplementsReplayCapability_ThenCompilerReportsTheIncompleteCustomContract()
    {
        // Arrange
        const string source = """
            [Namotion.Interceptor.Attributes.InterceptorSubject]
            public partial class ReplaySubject : Namotion.Interceptor.ISubjectPropertyReplay
            {
                public partial int Value { get; set; }
                bool Namotion.Interceptor.ISubjectPropertyReplay.CanReplayProperty(string propertyName) => true;
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.Empty(result.GeneratorDiagnostics);
        var diagnostic = Assert.Single(result.CompilationErrors);
        Assert.Equal("CS0535", diagnostic.Id);
        Assert.Contains("ReplayProperty", diagnostic.GetMessage());
    }

    [Fact]
    public void WhenUngeneratedSubclassOverridesVirtualProperty_ThenReplayRejectsItAndNormalSetterUsesTheOverride()
    {
        // Arrange
        const string source = """
            [Namotion.Interceptor.Attributes.InterceptorSubject]
            public partial class ReplayRoot
            {
                public virtual partial int Value { get; set; }
                public partial int Other { get; set; }
            }

            public class ReplayDerived : ReplayRoot
            {
                public override int Value
                {
                    get => base.Value;
                    set => base.Value = value + 1;
                }
            }
            """;
        var result = GeneratorTestHost.RunForExecution(source);
        var subject = (IInterceptorSubject)result.CreateInstance("ReplayDerived");
        var outcome = default(PropertyReplayOutcome);

        // Act
        subject.Properties["Value"].SetValue!(subject, 2);

        // Assert
        Assert.Equal(3, subject.Properties["Value"].GetValue!(subject));
        Assert.False(((ISubjectPropertyReplay)subject).CanReplayProperty("Value"));
        Assert.Throws<NotSupportedException>(() =>
            ((ISubjectPropertyReplay)subject).ReplayProperty("Value", 5, ref outcome));
        Assert.Equal(3, subject.Properties["Value"].GetValue!(subject));
        Assert.False(outcome.Accepted);
        Assert.False(outcome.Mutated);
        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenSubjectHasUnrelatedReplayMethod_ThenGeneratedInterfaceDispatchRemainsActive()
    {
        // Arrange
        const string source = """
            [Namotion.Interceptor.Attributes.InterceptorSubject]
            public partial class ReplaySubject
            {
                public partial int Value { get; set; }
                public void ReplayProperty(string propertyName, object value, ref Namotion.Interceptor.PropertyReplayOutcome outcome)
                    => throw new System.InvalidOperationException();
            }
            """;
        var result = GeneratorTestHost.RunForExecution(source);
        var subject = (IInterceptorSubject)result.CreateInstance("ReplaySubject");
        var outcome = default(PropertyReplayOutcome);

        // Act
        var canReplay = ((ISubjectPropertyReplay)subject).CanReplayProperty("Value");
        ((ISubjectPropertyReplay)subject).ReplayProperty("Value", 2, ref outcome);

        // Assert
        Assert.True(canReplay);
        Assert.Equal(2, subject.Properties["Value"].GetValue!(subject));
        Assert.True(outcome.Accepted);
        Assert.True(outcome.Mutated);
        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenRootPropertyIsReplayed_ThenValueAndOutcomeAreUpdated()
    {
        // Arrange
        var subject = new ReplayInterfaceRoot { Value = 1 };
        var outcome = default(PropertyReplayOutcome);

        // Act
        var canReplay = ((ISubjectPropertyReplay)subject).CanReplayProperty(nameof(subject.Value));
        ((ISubjectPropertyReplay)subject).ReplayProperty(nameof(subject.Value), 2, ref outcome);

        // Assert
        Assert.True(canReplay);
        Assert.Equal(2, subject.Value);
        Assert.True(outcome.Accepted);
        Assert.True(outcome.Mutated);
    }

    [Fact]
    public void WhenUntouchedRootReceivesUnknownProperty_ThenReplayRejectsAndClearsOutcome()
    {
        // Arrange
        var subject = new ReplayInterfaceRoot { Value = 1 };
        var outcome = new PropertyReplayOutcome { Accepted = true, Mutated = true };

        // Act & Assert
        Assert.False(((ISubjectPropertyReplay)subject).CanReplayProperty("Unknown"));
        Assert.Throws<NotSupportedException>(() =>
            ((ISubjectPropertyReplay)subject).ReplayProperty("Unknown", 2, ref outcome));
        Assert.Equal(1, subject.Value);
        Assert.False(outcome.Accepted);
        Assert.False(outcome.Mutated);
    }

    [Fact]
    public void WhenMetadataIsAugmentedWithAnotherProperty_ThenOriginalPropertyStillSupportsReplay()
    {
        // Arrange
        var subject = new ReplayInterfaceRoot { Value = 1 };
        ((IInterceptorSubject)subject).AddProperties(new SubjectPropertyMetadata(
            "Extra", typeof(int), [], _ => 7, null, isIntercepted: false, isDynamic: true));
        var outcome = default(PropertyReplayOutcome);

        // Act
        var canReplay = ((ISubjectPropertyReplay)subject).CanReplayProperty(nameof(subject.Value));
        ((ISubjectPropertyReplay)subject).ReplayProperty(nameof(subject.Value), 2, ref outcome);

        // Assert
        Assert.NotSame(ReplayInterfaceRoot.DefaultProperties, ((IInterceptorSubject)subject).Properties);
        Assert.True(canReplay);
        Assert.Equal(2, subject.Value);
        Assert.True(outcome.Accepted);
        Assert.True(outcome.Mutated);
    }

    [Fact]
    public void WhenGeneratedDerivedInheritsUnhiddenProperty_ThenRootReplayRemainsSupported()
    {
        // Arrange
        var subject = new ReplayInterfaceDerived { Value = 1, Other = 3 };
        var outcome = default(PropertyReplayOutcome);

        // Act
        var canReplay = ((ISubjectPropertyReplay)subject).CanReplayProperty(nameof(subject.Value));
        ((ISubjectPropertyReplay)subject).ReplayProperty(nameof(subject.Value), 2, ref outcome);

        // Assert
        Assert.True(canReplay);
        Assert.Equal(2, subject.Value);
        Assert.Equal(3, subject.Other);
        Assert.True(outcome.Accepted);
        Assert.True(outcome.Mutated);
    }

    [Fact]
    public void WhenGeneratedDerivedHidesRootProperty_ThenReplayRejectsWithoutMutatingEitherField()
    {
        // Arrange
        const string source = """
            using Namotion.Interceptor.Attributes;

            [InterceptorSubject]
            public partial class ReplayRoot
            {
                public new static bool ReferenceEquals(object first, object second) => true;
                public partial int Value { get; set; }
            }

            [InterceptorSubject]
            public partial class ReplayHidden : ReplayRoot
            {
                public new partial int Value { get; set; }
            }
            """;
        var result = GeneratorTestHost.RunForExecution(source);
        var assembly = result.LoadAssembly();
        var rootProperty = assembly.GetType("ReplayRoot")!.GetProperty("Value")!;
        var hiddenType = assembly.GetType("ReplayHidden")!;
        var hiddenProperty = hiddenType.GetProperty("Value")!;
        var subject = Activator.CreateInstance(hiddenType)!;
        rootProperty.SetValue(subject, 1);
        hiddenProperty.SetValue(subject, 3);
        var outcome = new PropertyReplayOutcome { Accepted = true, Mutated = true };

        // Act & Assert
        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "NI0065");
        Assert.False(((ISubjectPropertyReplay)subject).CanReplayProperty("Value"));
        Assert.Throws<NotSupportedException>(() =>
            ((ISubjectPropertyReplay)subject).ReplayProperty("Value", 2, ref outcome));
        Assert.Equal(1, rootProperty.GetValue(subject));
        Assert.Equal(3, hiddenProperty.GetValue(subject));
        Assert.False(outcome.Accepted);
        Assert.False(outcome.Mutated);
    }

    [Fact]
    public void WhenGeneratedDerivedAddsProperty_ThenReplayUpdatesItsOwnProperty()
    {
        // Arrange
        var subject = new ReplayInterfaceDerived { Value = 1, Other = 3 };
        var outcome = default(PropertyReplayOutcome);

        // Act
        var canReplay = ((ISubjectPropertyReplay)subject).CanReplayProperty(nameof(subject.Other));
        ((ISubjectPropertyReplay)subject).ReplayProperty(nameof(subject.Other), 2, ref outcome);

        // Assert
        Assert.True(canReplay);
        Assert.Equal(1, subject.Value);
        Assert.Equal(2, subject.Other);
        Assert.True(outcome.Accepted);
        Assert.True(outcome.Mutated);
    }

    [Fact]
    public void WhenSubjectShadowsReferenceEqualsAndMetadataSetterIsReplaced_ThenReplayRejectsWithoutMutation()
    {
        // Arrange
        var subject = new ReplayInterfaceRoot { Value = 1 };
        var replacementCalled = false;
        ((IInterceptorSubject)subject).AddProperties(new SubjectPropertyMetadata(
            nameof(subject.Value), typeof(int), [],
            instance => ((ReplayInterfaceRoot)instance).Value,
            (_, _) => replacementCalled = true,
            isIntercepted: true, isDynamic: false));
        var outcome = default(PropertyReplayOutcome);

        // Act & Assert
        Assert.False(((ISubjectPropertyReplay)subject).CanReplayProperty(nameof(subject.Value)));
        Assert.Throws<NotSupportedException>(() =>
            ((ISubjectPropertyReplay)subject).ReplayProperty(nameof(subject.Value), 2, ref outcome));
        Assert.Equal(1, subject.Value);
        Assert.False(replacementCalled);
        Assert.False(outcome.Accepted);
        Assert.False(outcome.Mutated);
    }

    [Fact]
    public void WhenGeneratedSubjectUsesManualBase_ThenNormalSetterStillReachesItsInterceptor()
    {
        // Arrange
        var source = SubjectInheritanceTestSources.PublicMemberBase + SubjectInheritanceTestSources.GeneratedDerived;
        var result = GeneratorTestHost.RunForExecution(source);
        var subjectType = result.LoadAssembly().GetType("Repro.GenDerived")!;
        var interceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithService(() => interceptor);
        var subject = Activator.CreateInstance(subjectType, context)!;

        // Act
        subjectType.GetProperty("Name")!.SetValue(subject, "updated");

        // Assert
        Assert.Equal("updated", subjectType.GetProperty("Name")!.GetValue(subject));
        Assert.Contains(interceptor.Writes, write => write.PropertyName == "Name" && Equals(write.Value, "updated"));
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }
}

[InterceptorSubject]
public partial class ReplayInterfaceRoot
{
    public new static bool ReferenceEquals(object? first, object? second) => true;

    public partial int Value { get; set; }
}

[InterceptorSubject]
public partial class ReplayInterfaceDerived : ReplayInterfaceRoot
{
    public partial int Other { get; set; }
}
