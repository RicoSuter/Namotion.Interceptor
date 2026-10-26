using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

public sealed class AuthorizationServiceConcurrencyTests
{
    [Fact]
    public void WhenTheGraphChangesOnceWhileTheRolesAreRead_ThenTheyAreReadAgain()
    {
        // Arrange
        var registry = new ConcurrentlyChangedRegistry(CreateAuthorization()) { FailuresLeft = 1 };
        using var service = CreateService(registry);

        // Act
        var result = service.GetPolicy(["Custom"]);

        // Assert
        Assert.Null(result.Error);
        Assert.Equal(0, registry.FailuresLeft);
    }

    [Fact]
    public void WhenTheGraphKeepsChangingWhileTheRolesAreRead_ThenCustomRolesAreUnavailableUntilTheyCanBeRead()
    {
        // Arrange
        var registry = new ConcurrentlyChangedRegistry(CreateAuthorization()) { FailuresLeft = int.MaxValue };
        using var service = CreateService(registry);

        // Act
        var custom = service.GetPolicy(["Custom"]);
        var builtIn = service.GetPolicy([BuiltInRoles.ReaderName]);
        registry.FailuresLeft = 0;
        var customAfterwards = service.GetPolicy(["Custom"]);

        // Assert
        Assert.Same(AuthorizationPolicy.DenyAll, custom.Policy);
        Assert.Contains("'Custom'", custom.Error);
        Assert.Null(builtIn.Error);
        Assert.Null(customAfterwards.Error);
    }

    private static Authorization CreateAuthorization() =>
        new() { Roles = new Dictionary<string, Role> { ["Custom"] = new() { Includes = [BuiltInRoles.ReaderName] } } };

    private static AuthorizationService CreateService(ConcurrentlyChangedRegistry registry)
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        context.AddService<ISubjectRegistry>(registry);
        return new AuthorizationService(context, new DefaultPathProvider('/'));
    }

    /// <summary>
    /// A registry whose known subjects throw as a collection that changes while it is enumerated does: on the thread
    /// that created it while failures are left, so that the test decides, and on every other thread, so that the
    /// rebuilds the service starts in the background never succeed.
    /// </summary>
    private sealed class ConcurrentlyChangedRegistry(IInterceptorSubject subject) : ISubjectRegistry
    {
        private readonly int _threadId = Environment.CurrentManagedThreadId;
        private readonly Dictionary<IInterceptorSubject, RegisteredSubject> _subjects = new() { [subject] = null! };

        public int FailuresLeft { get; set; }

        public IReadOnlyDictionary<IInterceptorSubject, RegisteredSubject> KnownSubjects => new KnownSubjectsView(this);

        public RegisteredSubject? TryGetRegisteredSubject(IInterceptorSubject subject) => null;

        private IEnumerable<IInterceptorSubject> GetKeys()
        {
            if (Environment.CurrentManagedThreadId != _threadId || FailuresLeft > 0)
            {
                if (Environment.CurrentManagedThreadId == _threadId)
                {
                    FailuresLeft--;
                }

                throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
            }

            return _subjects.Keys;
        }

        private sealed class KnownSubjectsView(ConcurrentlyChangedRegistry registry) : IReadOnlyDictionary<IInterceptorSubject, RegisteredSubject>
        {
            public IEnumerable<IInterceptorSubject> Keys => registry.GetKeys();

            public IEnumerable<RegisteredSubject> Values => registry._subjects.Values;

            public int Count => registry._subjects.Count;

            public RegisteredSubject this[IInterceptorSubject key] => registry._subjects[key];

            public bool ContainsKey(IInterceptorSubject key) => registry._subjects.ContainsKey(key);

            public bool TryGetValue(IInterceptorSubject key, [MaybeNullWhen(false)] out RegisteredSubject value) =>
                registry._subjects.TryGetValue(key, out value);

            public IEnumerator<KeyValuePair<IInterceptorSubject, RegisteredSubject>> GetEnumerator() => registry._subjects.GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
