; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
NI0001 | Namotion.Interceptor | Error | Interceptor subject must be partial
NI0002 | Namotion.Interceptor | Error | Containing type of an interceptor subject must be partial
NI0003 | Namotion.Interceptor | Error | InterceptorSubject is only supported on classes
NI0004 | Namotion.Interceptor | Error | Interceptor subject generation failed
NI0005 | Namotion.Interceptor | Warning | Property re-declares a member already implemented by the base class
NI0006 | Namotion.Interceptor | Warning | Unsupported member skipped
NI0007 | Namotion.Interceptor | Error | Attributes on an explicit interface implementation are ignored
NI0008 | Namotion.Interceptor | Warning | Two interface members collide on one property name
NI0009 | Namotion.Interceptor | Error | Generic interceptor subjects are not supported
NI0010 | Namotion.Interceptor | Error | File-local interceptor subjects are not supported
