using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class UnstableDerivedSubject
{
    private bool _useSecond;

    internal bool AlternateDependencies { get; set; }
    internal int GetterCallCount { get; private set; }

    public partial int First { get; set; }
    public partial int Second { get; set; }
    public partial int Unrelated { get; set; }

    [Derived]
    public int Selected
    {
        get
        {
            GetterCallCount++;
            if (AlternateDependencies)
            {
                _useSecond = !_useSecond;
                // A landed write while discovering a new dependency starts stabilization.
                Unrelated = 1;
            }

            return _useSecond ? Second : First;
        }
    }
}
