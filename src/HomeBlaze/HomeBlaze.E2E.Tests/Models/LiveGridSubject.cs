using Namotion.Interceptor.Attributes;

namespace HomeBlaze.E2E.Tests.Models;

/// <summary>
/// Rows of values that <see cref="LiveGridWidget"/> shows in the templates of MudBlazor components.
/// </summary>
[InterceptorSubject]
public partial class LiveGridSubject
{
    public LiveGridSubject()
    {
        Rows = [];
    }

    public partial LiveValuesSubject[] Rows { get; set; }
}
