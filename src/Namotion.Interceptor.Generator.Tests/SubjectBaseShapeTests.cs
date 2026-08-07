using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

public class SubjectBaseShapeTests
{
    [Fact]
    public void WhenBaseImplementsRaisePropertyChangedWithoutBeingASubject_ThenNoNotifyPlumbingIsRedeclared()
    {
        // Arrange: the base is INPC + IRaisePropertyChanged but NOT IInterceptorSubject and has no
        // attribute, so it is not a subject ancestor. BaseClassHasInpc must still be true, because
        // its second disjunct is asked of the subject, not of the ancestor.
        const string source = """
            using System.ComponentModel;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public abstract class ManualBase : INotifyPropertyChanged, IRaisePropertyChanged
                {
                    public event PropertyChangedEventHandler? PropertyChanged;

                    public void RaisePropertyChanged(string propertyName)
                        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
                }

                [InterceptorSubject]
                public partial class ManualDerived : ManualBase
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var generated = result.SingleSource();

        // Assert
        Assert.DoesNotContain("public event PropertyChangedEventHandler? PropertyChanged;", generated);
        Assert.DoesNotContain("protected void RaisePropertyChanged(string propertyName)", generated);
        Assert.Contains("((IRaisePropertyChanged)this).RaisePropertyChanged(nameof(Name))", generated);
    }

    [Fact]
    public void WhenSubjectIsSealedAndDerived_ThenItCompilesWithoutWarnings()
    {
        // Arrange: a sealed DERIVED subject is legal today, because RaisePropertyChanged is gated
        // on BaseClassHasInpc and so is not emitted into it. Only a sealed ROOT fails (Task 3).
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class BaseSubject
                {
                    public partial string BaseName { get; set; }
                }

                [InterceptorSubject]
                public sealed partial class SealedLeaf : BaseSubject
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        // Act & Assert
        GeneratorTestHost.RunExpectingNoWarnings(source);
    }
}
