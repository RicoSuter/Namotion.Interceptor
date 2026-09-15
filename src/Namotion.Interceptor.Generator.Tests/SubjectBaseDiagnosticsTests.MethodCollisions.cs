using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

public partial class SubjectBaseDiagnosticsTests
{
    [Fact]
    public void WhenAWrapperWouldBeNamedLikeAnInheritedInterceptionMember_ThenNI0040IsReportedAndThePropertiesSurvive()
    {
        // Arrange: stripping the postfix yields "GetInstanceProperties", the inherited helper the
        // generated IInterceptorSubject.Properties calls. Emitting the wrapper captures that call,
        // so Properties reports whatever the wrapper returns and the registry sees nothing, while
        // writes keep working and hide the breakage.
        var source = LeafDeclaring(
            "public IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstancePropertiesWithoutInterceptor()" +
            " => new Dictionary<string, SubjectPropertyMetadata>();");

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var leaf = (IInterceptorSubject)result.CreateInstance("Repro.LeafSubject");

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0040");
        Assert.Equal(["LeafName", "RootName"], leaf.Properties.Keys.OrderBy(name => name));
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenAWrapperWouldBeNamedAddProperties_ThenNI0040IsReportedAndAddedPropertiesSurvive()
    {
        // Arrange: the wrapper would be emitted as a public void AddProperties(IEnumerable<...>),
        // and 'params' is not part of a signature, so it is an implicit implementation of
        // IInterceptorSubject.AddProperties and takes the slot from the root's explicit one, because
        // a derived subject re-lists the interface. Neither the generator nor the compiler says
        // anything about that, which makes it quieter than the capture the guard was written for.
        var source = LeafDeclaring(
            "public void AddPropertiesWithoutInterceptor(params IEnumerable<SubjectPropertyMetadata> properties) { }");

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var leaf = (IInterceptorSubject)result.CreateInstance("Repro.LeafSubject");
        leaf.AddProperties(new SubjectPropertyMetadata(
            "Extra", typeof(string), [], _ => "e", (_, _) => { }, isIntercepted: false, isDynamic: true));

        // Assert: the root's implementation still runs, which is the only evidence that the wrapper
        // was really dropped. With the wrapper emitted, this call is a silent no-op.
        Assert.True(leaf.Properties.ContainsKey("Extra"));
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0040");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenAWrapperWouldBeNamedInvokeMethodAtAnotherArity_ThenNI0040IsReportedAndTheRealBodyRuns()
    {
        // Arrange: the accessor helper InvokeMethod ends in "params object?[]", so the generated call site
        // for a parameterless method, InvokeMethod("Echo", lambda), passes two arguments. A
        // two-parameter overload is applicable in normal form and therefore beats the helper, which
        // is only applicable in expanded form, so the wrapper swallows the call. Nothing in the
        // compiler says a word about it and Echo() returns the wrapper's answer instead of "echo".
        var source = LeafDeclaring("""
                public string EchoWithoutInterceptor() => "echo";

                public object InvokeMethodWithoutInterceptor(string name, Action<IInterceptorSubject, object[]> callback)
                    => "HIJACKED:" + name;
            """);

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var leafType = result.LoadAssembly().GetType("Repro.LeafSubject");
        Assert.NotNull(leafType);
        var instance = Activator.CreateInstance(leafType)!;

        // Assert: the real body runs, which is the only evidence there is. The value is what the
        // capture changes, and no diagnostic accompanies it.
        Assert.Equal("echo", leafType.GetMethod("Echo", Type.EmptyTypes)!.Invoke(instance, []));
        Assert.Null(leafType.GetMethod("InvokeMethod", [typeof(string), typeof(Action<IInterceptorSubject, object[]>)]));
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0040");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenAWrapperSharesAnInterceptionMemberNameButNotItsArity_ThenNI0040IsReportedAndNoWrapperIsEmitted()
    {
        // Arrange: the deliberate inversion of a rule that used to compare the arity and let these
        // two through. Since InvokeMethod takes a parameter array, no arity is safe, and the guard no
        // longer reasons about signatures at all. Both wrappers are the accepted false positive: the
        // author is told to rename, which is loud and recoverable, unlike the capture above. Echo is
        // here to show that the helper still binds once they are gone.
        var source = LeafDeclaring("""
                public object InvokeMethodWithoutInterceptor(string name, object[] arguments) => name;

                public string GetPropertyValueWithoutInterceptor(string key) => key;

                public string EchoWithoutInterceptor(string value) => value;
            """);

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var leafType = result.LoadAssembly().GetType("Repro.LeafSubject");
        Assert.NotNull(leafType);
        var instance = Activator.CreateInstance(leafType)!;

        // Assert
        Assert.Null(leafType.GetMethod("InvokeMethod", [typeof(string), typeof(object[])]));
        Assert.Null(leafType.GetMethod("GetPropertyValue", [typeof(string)]));
        Assert.Equal("v", leafType.GetMethod("Echo", [typeof(string)])!.Invoke(instance, ["v"]));

        var skipped = result.GeneratorDiagnostics
            .Where(d => d.Id == "NI0040")
            .Select(d => d.GetMessage())
            .ToList();

        Assert.Equal(2, skipped.Count);
        Assert.Contains(skipped, message => message.Contains("InvokeMethodWithoutInterceptor") && message.Contains("rename"));
        Assert.Contains(skipped, message => message.Contains("GetPropertyValueWithoutInterceptor") && message.Contains("rename"));
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenAWrapperIsNamedLikeAnExplicitlyImplementedInterfaceProperty_ThenNI0040IsReported()
    {
        // Arrange: the deliberate inversion of an exemption for Context, Data and SyncRoot. Those are
        // explicit interface properties in a generated root, where a method of the same name really
        // does collide with nothing, but the exemption was keyed on the name rather than on the base,
        // so it applied just as much to a hand-written base that exposes them publicly, where the
        // wrapper is a CS0108. The name is now enough on its own.
        var source = LeafDeclaring("public string DataWithoutInterceptor(string tag) => tag;");

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var leaf = result.CreateInstance("Repro.LeafSubject");

        // Assert: no wrapper, and the interface slot is still the root's dictionary.
        Assert.Null(leaf.GetType().GetMethod("Data", [typeof(string)]));
        Assert.NotNull(((IInterceptorSubject)leaf).Data);
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0040");
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0064");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenAWrapperWouldBeNamedContextOnABaseWithPublicMembers_ThenNI0040IsReportedAndNothingIsHidden()
    {
        // Arrange: the half of the exemption that was not merely unnecessary but unsound. This base
        // satisfies the contract with public members, which is the shape generator.md
        // documents, so a "Context" wrapper hides the inherited public property. CS0108 lands in a
        // generated file the consumer cannot edit and fails any build with TreatWarningsAsErrors.
        var source = PublicMemberBase + ContextWrapperDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0040");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }
}
