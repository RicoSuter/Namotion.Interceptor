namespace Namotion.Interceptor.Hosting;

/// <summary>
/// Adds what a subject needs to the context it runs in when it runs in a context of its own, through
/// <c>AddSubject</c> or <c>AddKeyedSubject</c> without a context resolver. Never called for a shared
/// context: whoever owns it decides what it contains.
/// </summary>
public interface ISubjectContextConfigurator
{
    /// <summary>
    /// Called once per such context, after the subject is constructed and configured and before it
    /// joins the context.
    /// </summary>
    /// <param name="context">
    /// The context, which already has property tracking and lifecycle and gets hosting after this
    /// returns, so the implementation must not add hosting; doing so throws
    /// <see cref="InvalidOperationException"/> before the subject joins.
    /// </param>
    void ConfigureContext(IInterceptorSubjectContext context);
}
