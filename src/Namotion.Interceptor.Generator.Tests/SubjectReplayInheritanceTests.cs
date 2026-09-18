using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Generator.Tests;

public class SubjectReplayInheritanceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void WhenGeneratedHierarchyReplaysProperties_ThenEveryLevelUsesItsOwnSetter(bool emptyRoot, bool emptyMiddle)
    {
        // Arrange
        var result = GeneratorTestHost.RunForExecution($$"""
            using Namotion.Interceptor.Attributes;
            [InterceptorSubject]
            public partial class Root { {{(emptyRoot ? "" : "public partial int RootValue { get; set; }")}} }
            public class Bridge : Root { }
            [InterceptorSubject]
            public partial class Middle : Bridge { {{(emptyMiddle ? "" : "public partial int MiddleValue { get; set; }")}} }
            [InterceptorSubject]
            public sealed partial class Leaf : Middle { public partial int LeafValue { get; set; } }
            """);
        var subject = (IInterceptorSubject)result.CreateInstance("Leaf");

        // Act & Assert
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        foreach (var property in subject.Properties.Values)
        {
            var outcome = default(PropertyReplayOutcome);
            Assert.True(((ISubjectPropertyReplay)subject).CanReplayProperty(property.Name));
            ((ISubjectPropertyReplay)subject).ReplayProperty(property.Name, 42, ref outcome);
            Assert.Equal(42, property.GetValue!(subject));
            Assert.True(outcome.Accepted);
            Assert.True(outcome.Mutated);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void WhenAncestorIsCompiledSeparately_ThenOwnAndInheritedReplayAreSupported(bool generatedMiddle, bool redeclareSubject)
    {
        // Arrange
        var result = GeneratorTestHost.RunWithLibraryReferenceForExecution($$"""
            using Namotion.Interceptor.Attributes;
            [InterceptorSubject]
            public partial class Root { public partial int RootValue { get; set; } }
            {{(generatedMiddle ? "[InterceptorSubject] public partial class Middle : Root { public partial int MiddleValue { get; set; } }" : "public class Middle : Root { }")}}
            public class Bridge : Middle{{(redeclareSubject ? ", Namotion.Interceptor.IInterceptorSubject" : "")}} { }
            """, """
            using Namotion.Interceptor.Attributes;
            [InterceptorSubject]
            public partial class Leaf : Bridge { public partial int LeafValue { get; set; } }
            """);
        var subject = (IInterceptorSubject)result.CreateInstance("Leaf");

        // Act & Assert
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        foreach (var property in subject.Properties.Values)
        {
            var outcome = default(PropertyReplayOutcome);
            Assert.True(((ISubjectPropertyReplay)subject).CanReplayProperty(property.Name));
            ((ISubjectPropertyReplay)subject).ReplayProperty(property.Name, 42, ref outcome);
            Assert.Equal(42, property.GetValue!(subject));
            Assert.True(outcome.Mutated);
        }
    }

    [Theory]
    [InlineData("protected new InterceptorExecutor GetPropertyReplayExecutor() => new(this);")]
    [InlineData("public new static int GetPropertyReplayExecutor() => 0;")]
    [InlineData("public new int GetPropertyReplayExecutor => 0;")]
    [InlineData("protected new virtual InterceptorExecutor GetPropertyReplayExecutor() => new(this);")]
    [InlineData("protected InterceptorExecutor GetPropertyReplayExecutor(int ignored) => new(this);")]
    [InlineData("protected new bool CanReplayGeneratedProperty(string propertyName) => true;")]
    [InlineData("protected new void ReplayGeneratedProperty(string propertyName, object value, ref PropertyReplayOutcome outcome) { outcome.Accepted = true; }")]
    public void WhenCompiledProviderHidesReplayHelper_ThenReplayRejectsAndOrdinarySetterUsesItsInterceptor(string declaration)
    {
        // Arrange
        var result = GeneratorTestHost.RunWithLibraryReferenceForExecution($$"""
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Interceptors;
            [InterceptorSubject]
            public partial class Root { public partial int RootValue { get; set; } }
            public class Bridge : Root, IInterceptorSubject
            {
                {{declaration}}
            }
            """, """
            [Namotion.Interceptor.Attributes.InterceptorSubject]
            public partial class Leaf : Bridge { public partial int Value { get; set; } }
            """);
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        var interceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext.Create().WithService(() => interceptor);
        var subjectType = result.LoadAssembly().GetType("Leaf")!;
        var subject = (IInterceptorSubject)Activator.CreateInstance(subjectType, context)!;
        var outcome = default(PropertyReplayOutcome);

        // Act
        subject.Properties["Value"].SetValue!(subject, 10);

        // Assert
        Assert.False(((ISubjectPropertyReplay)subject).CanReplayProperty("Value"));
        Assert.Throws<NotSupportedException>(() => ((ISubjectPropertyReplay)subject).ReplayProperty("Value", 20, ref outcome));
        Assert.Equal(10, subject.Properties["Value"].GetValue!(subject));
        Assert.Equal(new object[] { 10 }, interceptor.Writes.Select(write => write.Value));
        Assert.False(outcome.Accepted);
        Assert.False(outcome.Mutated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenInheritedCustomReplayExists_ThenItRetainsControl(bool compiledAncestor)
    {
        // Arrange
        const string ancestor = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            [InterceptorSubject]
            public partial class Root : ISubjectPropertyReplay
            {
                public partial int RootValue { get; set; }
                public bool CanReplayProperty(string propertyName) => propertyName == "Custom";
                public void ReplayProperty(string propertyName, object value, ref PropertyReplayOutcome outcome)
                {
                    RootValue = 123;
                    outcome.Accepted = true;
                    outcome.Mutated = true;
                }
            }
            public class Bridge : Root { }
            """;
        const string derived = """
            [Namotion.Interceptor.Attributes.InterceptorSubject]
            public partial class Leaf : Bridge { public partial int LeafValue { get; set; } }
            """;
        var result = compiledAncestor
            ? GeneratorTestHost.RunWithLibraryReferenceForExecution(ancestor, derived)
            : GeneratorTestHost.RunForExecution(ancestor + derived);
        var subject = (IInterceptorSubject)result.CreateInstance("Leaf");
        var outcome = default(PropertyReplayOutcome);

        // Act
        ((ISubjectPropertyReplay)subject).ReplayProperty("Custom", 42, ref outcome);

        // Assert
        Assert.False(((ISubjectPropertyReplay)subject).CanReplayProperty("LeafValue"));
        Assert.True(((ISubjectPropertyReplay)subject).CanReplayProperty("Custom"));
        Assert.Equal(123, subject.Properties["RootValue"].GetValue!(subject));
        Assert.True(outcome.Accepted);
        Assert.True(outcome.Mutated);
    }

    [Fact]
    public void WhenDerivedReplaysWithAContext_ThenNormalAndReplayWritesUseTheSameInterceptorAndHooks()
    {
        // Arrange
        var result = GeneratorTestHost.RunForExecution("""
            using Namotion.Interceptor.Attributes;
            [InterceptorSubject]
            public partial class Root { public partial int RootValue { get; set; } }
            [InterceptorSubject]
            public partial class Leaf : Root
            {
                public partial int Value { get; set; }
                public int ChangingCalls;
                public int ChangedCalls;
                partial void OnValueChanging(ref int newValue, ref bool cancel) { ChangingCalls++; newValue++; }
                partial void OnValueChanged(int newValue) { ChangedCalls++; }
            }
            """);
        var interceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithService(() => interceptor);
        var subjectType = result.LoadAssembly().GetType("Leaf")!;
        var subject = (IInterceptorSubject)Activator.CreateInstance(subjectType, context)!;
        var outcome = default(PropertyReplayOutcome);

        // Act
        subject.Properties["Value"].SetValue!(subject, 10);
        ((ISubjectPropertyReplay)subject).ReplayProperty("Value", 20, ref outcome);
        ((ISubjectPropertyReplay)subject).ReplayProperty("RootValue", 30, ref outcome);

        // Assert
        Assert.Equal(21, subject.Properties["Value"].GetValue!(subject));
        Assert.Equal(30, subject.Properties["RootValue"].GetValue!(subject));
        Assert.Equal(2, subjectType.GetField("ChangingCalls")!.GetValue(subject));
        Assert.Equal(2, subjectType.GetField("ChangedCalls")!.GetValue(subject));
        Assert.Equal(new object[] { 11, 21, 30 }, interceptor.Writes.Select(write => write.Value));
    }

    [Theory]
    [InlineData("GetPropertyReplayExecutor")]
    [InlineData("CanReplayGeneratedProperty")]
    [InlineData("ReplayGeneratedProperty")]
    public void WhenIntermediateHidesReplayHelper_ThenGeneratorReportsCollisionAndReplayRejectsOwnProperty(string helperName)
    {
        // Arrange
        var result = GeneratorTestHost.RunForExecution($$"""
            using Namotion.Interceptor.Attributes;
            [InterceptorSubject]
            public partial class Root { public partial int RootValue { get; set; } }
            public class Bridge : Root { public new int {{helperName}} => 1; }
            [InterceptorSubject]
            public partial class Leaf : Bridge { public partial int Value { get; set; } }
            """);
        var subject = (ISubjectPropertyReplay)result.CreateInstance("Leaf");
        var outcome = default(PropertyReplayOutcome);

        // Act & Assert
        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "NI0063");
        Assert.False(subject.CanReplayProperty("Value"));
        Assert.Throws<NotSupportedException>(() => subject.ReplayProperty("Value", 42, ref outcome));
        Assert.False(outcome.Mutated);
    }

    [Fact]
    public void WhenContextInterfaceIsReimplemented_ThenReplayUsesTheOrdinarySettersExecutor()
    {
        // Arrange
        var result = GeneratorTestHost.RunForExecution("""
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            [InterceptorSubject]
            public partial class Root
            {
                public partial int RootValue { get; set; }
                public void AttachRootContext(IInterceptorSubjectContext context)
                    => Namotion.Interceptor.Interceptors.InterceptorExecutor.GetOrCreate(ref _context, this).AddFallbackContext(context);
            }
            public class Bridge : Root { }
            [InterceptorSubject]
            public partial class Leaf : Bridge, IInterceptorSubject
            {
                public partial int Value { get; set; }
                IInterceptorSubjectContext IInterceptorSubject.Context => throw new System.InvalidOperationException("Displaced context accessed.");
            }
            """);
        var interceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext.Create().WithService(() => interceptor);
        var subject = (IInterceptorSubject)result.CreateInstance("Leaf");
        subject.GetType().GetMethod("AttachRootContext")!.Invoke(subject, [context]);
        var outcome = default(PropertyReplayOutcome);

        // Act
        subject.Properties["Value"].SetValue!(subject, 10);
        ((ISubjectPropertyReplay)subject).ReplayProperty("Value", 20, ref outcome);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "NI0064");
        Assert.Equal(new object[] { 10, 20 }, interceptor.Writes.Select(write => write.Value));
        Assert.Equal(20, subject.Properties["Value"].GetValue!(subject));
        Assert.True(outcome.Mutated);
    }

    [Theory]
    [InlineData("GetPropertyReplayExecutor")]
    [InlineData("CanReplayGeneratedProperty")]
    [InlineData("ReplayGeneratedProperty")]
    public void WhenRootDeclaresReplayHelperName_ThenOrdinarySetterCompilesAndReplayRemainsUnsupported(string helperName)
    {
        // Arrange
        var result = GeneratorTestHost.RunForExecution($$"""
            [Namotion.Interceptor.Attributes.InterceptorSubject]
            public partial class Root
            {
                public partial int Value { get; set; }
                public int {{helperName}} => 1;
            }
            """);
        var subject = (IInterceptorSubject)result.CreateInstance("Root");

        // Act
        subject.Properties["Value"].SetValue!(subject, 42);

        // Assert
        Assert.False(subject is ISubjectPropertyReplay);
        Assert.Equal(42, subject.Properties["Value"].GetValue!(subject));
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenOlderCompiledSubjectLacksReplayContract_ThenDerivedReplayRemainsUnsupported()
    {
        // Arrange
        var ancestor = SubjectInheritanceTestSources.PublicMemberBase.Replace(
            "public class HandBase", "[Namotion.Interceptor.Attributes.InterceptorSubject] public class HandBase");

        // Act
        var result = GeneratorTestHost.RunWithLibraryReference(ancestor, SubjectInheritanceTestSources.GeneratedDerived);

        // Assert
        Assert.DoesNotContain("ISubjectPropertyReplay", result.SingleSource());
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenManualBaseLacksReplayContract_ThenDerivedReplayRemainsUnsupported()
    {
        // Arrange
        var result = GeneratorTestHost.RunForExecution(
            SubjectInheritanceTestSources.PublicMemberBase + SubjectInheritanceTestSources.GeneratedDerived);
        var subject = result.CreateInstance("Repro.GenDerived");

        // Act & Assert
        Assert.False(subject is ISubjectPropertyReplay);
        Assert.Empty(result.CompilationErrors);
    }
}
