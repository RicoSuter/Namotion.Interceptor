using System.Runtime.CompilerServices;
using Bunit;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Components.Tests;

/// <summary>
/// Gives the WaitFor operations of bUnit the same time as the other waits of the tests, linked into each test project
/// that waits for a render.
/// </summary>
internal static class BunitWaitTimeout
{
    // The default of one second fails a wait for a render that a busy runner schedules late, although nothing is wrong.
    [ModuleInitializer]
    public static void Initialize() => BunitContext.DefaultWaitTimeout = TestTimeouts.Default;
}
