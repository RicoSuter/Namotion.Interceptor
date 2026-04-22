using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Registry.Tests;

public partial class MethodInitializerReentrancyTests
{
    [AttributeUsage(AttributeTargets.Method)]
    private sealed class RegistryProbingMethodAttribute : SubjectMethodAttribute, ISubjectMethodInitializer
    {
        public void InitializeMethod(RegisteredSubjectMethod method)
        {
            // Before the hoist this ran while SubjectRegistry._knownSubjects was
            // held. Reentrant callbacks on the same thread worked (Monitor is
            // reentrant), but the lock being held during user-supplied initializer
            // code was a fragility the hoist removes. This test locks in the
            // reentrant-callback case; the outer LifecycleInterceptor still
            // serializes cross-thread registrations at a higher layer and is
            // out of scope for this PR.
            var probed = method.Parent.Subject.TryGetRegisteredSubject();
            Assert.NotNull(probed);
        }
    }

    [InterceptorSubject]
    public partial class ProbingHost
    {
        [RegistryProbingMethod]
        public void Ping() { }
    }

    [Fact]
    public async Task WhenInitializerCallsIntoRegistry_ThenNoDeadlock()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();

        // Act
        var construction = Task.Run(() =>
        {
            var host = new ProbingHost(context);
            return host;
        });

        var completed = await Task.WhenAny(construction, Task.Delay(TimeSpan.FromSeconds(5))) == construction;

        // Assert
        Assert.True(completed, "initializer-triggered registry call blocked longer than 5 seconds");
        await construction;
    }
}
