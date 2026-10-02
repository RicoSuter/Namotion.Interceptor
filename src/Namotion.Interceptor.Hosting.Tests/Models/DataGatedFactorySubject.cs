using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A <see cref="DataGatedSubject"/> that has a hosted service, so the gate can interleave a context
/// entry with an explicit activation. Its own type, because the gated attach tests enter the base
/// subject into an activating context and would otherwise find an activation they never made.
/// </summary>
internal sealed class DataGatedFactorySubject : DataGatedSubject, ISubjectHostedServiceFactory
{
    private int _createCount;

    public int CreateCount => Volatile.Read(ref _createCount);

    /// <summary>
    /// Arms the gate to run <paramref name="gate"/> on the <paramref name="count"/>th read of
    /// <see cref="DataGatedSubject.Data"/> from now, re-arming itself on each read before that.
    /// </summary>
    public void GateDataRead(int count, Action gate)
    {
        GateNextDataRead(() =>
        {
            if (count > 1)
            {
                GateDataRead(count - 1, gate);
            }
            else
            {
                gate();
            }
        });
    }

    public IHostedService CreateHostedService(IServiceProvider serviceProvider)
    {
        Interlocked.Increment(ref _createCount);
        return new TrackedBackgroundService();
    }
}
