; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
NI0001 | Namotion.Interceptor | Error | Interceptor subject must be partial
NI0002 | Namotion.Interceptor | Error | Containing type of an interceptor subject must be partial
NI0003 | Namotion.Interceptor | Error | InterceptorSubject is only supported on classes
NI0009 | Namotion.Interceptor | Error | Generic interceptor subjects are not supported
NI0010 | Namotion.Interceptor | Error | File-local interceptor subjects are not supported
