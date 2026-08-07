using System.Collections.Generic;

namespace Namotion.Interceptor.Generator.Models;

/// <summary>
/// What the generator knows about the class a subject inherits from. All of it is resolved from the
/// nearest subject ancestor rather than the immediate base, because a plain class may sit in
/// between, except <see cref="HasInpc"/>, whose second disjunct is asked of the subject itself.
/// </summary>
/// <param name="TypeName">The ancestor's fully qualified name, or null when there is no subject ancestor.</param>
/// <param name="HasInterceptorSubject">Whether that ancestor carries the attribute.</param>
/// <param name="HasInpc">Whether the INotifyPropertyChanged plumbing is already inherited.</param>
/// <param name="EmitsPlumbingHere">True in root mode, where the subject itself, not the base class, emits the whole IInterceptorSubject block.</param>
/// <param name="HiddenPlumbingMemberNames">Root mode members that need a 'new' modifier because the ancestor already exposes that name.</param>
internal sealed record BaseClassInfo(
    string? TypeName,
    bool HasInterceptorSubject,
    bool HasInpc,
    bool EmitsPlumbingHere,
    IReadOnlyList<string> HiddenPlumbingMemberNames);
