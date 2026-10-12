using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Storage.Pages;

/// <summary>
/// The content of one open slot: HTML, expressions and widgets in page order.
/// </summary>
[InterceptorSubject]
public partial class MarkdownFragment
{
    public MarkdownFragment()
    {
        Children = new Dictionary<string, IInterceptorSubject>();
    }

    /// <summary>
    /// The segments of the content, keyed like the children of a page.
    /// </summary>
    [InlinePaths]
    public partial IDictionary<string, IInterceptorSubject> Children { get; internal set; }
}
