using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Blazor.Tests;

[InterceptorSubject]
public partial class Person
{
    public partial string? FirstName { get; set; }
}

/// <summary>
/// Counts the renders of each test component by its key and keeps the value it rendered last.
/// </summary>
public sealed class RenderLog
{
    private readonly ConcurrentDictionary<string, int> _renders = new();
    private readonly ConcurrentDictionary<string, string?> _values = new();

    public void Record(string key, string? value)
    {
        _values[key] = value;
        _renders.AddOrUpdate(key, 1, static (_, count) => count + 1);
    }

    public int Renders(string key) => _renders.GetValueOrDefault(key);

    public string? Value(string key) => _values.GetValueOrDefault(key);
}

/// <summary>
/// The root of a test: renders <see cref="Content"/>, which the test can render again.
/// </summary>
public sealed class Host : ComponentBase
{
    [Parameter]
    public RenderFragment? Content { get; set; }

    public Task RenderAgainAsync() => InvokeAsync(StateHasChanged);

    /// <summary>Replaces the content, on the dispatcher.</summary>
    public void SetContent(RenderFragment content)
    {
        Content = content;
        StateHasChanged();
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.AddContent(0, Content);
    }
}

/// <summary>
/// Renders the first name of <see cref="Person"/>. Its parameters are not of a known immutable type, so it renders
/// whenever its parent renders, as most components do.
/// </summary>
public class Reader : ComponentBase
{
    [Parameter]
    public string Key { get; set; } = null!;

    [Parameter]
    public Person Person { get; set; } = null!;

    [Parameter]
    public RenderLog Log { get; set; } = null!;

    /// <summary>A person whose first name is read as well, without being rendered.</summary>
    [Parameter]
    public Person? AlsoRead { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        _ = AlsoRead?.FirstName;
        var value = Person.FirstName;
        builder.AddContent(0, value);
        Log.Record(Key, value);
    }
}

/// <summary>
/// Renders nothing until its first render has completed and renders the first name from then on, in a batch of its own,
/// as a component that waits for its element or for JavaScript does.
/// </summary>
public sealed class DeferredReader : Reader
{
    private bool _isReady;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (_isReady)
        {
            base.BuildRenderTree(builder);
        }
        else
        {
            Log.Record(Key, null);
        }
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
        {
            _isReady = true;
            StateHasChanged();
        }
    }
}

internal static class Fragments
{
    public static RenderFragment Scope(IInterceptorSubjectContext context, RenderFragment content) => builder =>
    {
        builder.OpenComponent<TrackingScope>(0);
        builder.AddComponentParameter(1, nameof(TrackingScope.Context), context);
        builder.AddComponentParameter(2, nameof(TrackingScope.ChildContent), content);
        builder.CloseComponent();
    };

    public static RenderFragment Read<TReader>(string key, Person person, RenderLog log, Person? alsoRead = null)
        where TReader : Reader => builder =>
    {
        builder.OpenComponent<TReader>(0);
        builder.AddComponentParameter(1, nameof(Reader.Key), key);
        builder.AddComponentParameter(2, nameof(Reader.Person), person);
        builder.AddComponentParameter(3, nameof(Reader.Log), log);
        builder.AddComponentParameter(4, nameof(Reader.AlsoRead), alsoRead);
        builder.CloseComponent();
    };

    public static RenderFragment Read(string key, Person person, RenderLog log, Person? alsoRead = null) =>
        Read<Reader>(key, person, log, alsoRead);

    public static RenderFragment Each(int count, Func<int, RenderFragment> item) => builder =>
    {
        for (var index = 0; index < count; index++)
        {
            builder.OpenRegion(index);
            builder.AddContent(0, item(index));
            builder.CloseRegion();
        }
    };

    /// <summary>Reads the first name in the fragment itself, so the component that invokes the fragment reads it.</summary>
    public static RenderFragment Inline(string key, Person person, RenderLog log) => builder =>
    {
        var value = person.FirstName;
        builder.AddContent(0, value);
        log.Record(key, value);
    };

    public static RenderFragment Cascading(RenderFragment content) => builder =>
    {
        builder.OpenComponent<CascadingValue<int>>(0);
        builder.AddComponentParameter(1, nameof(CascadingValue<int>.Value), 1);
        builder.AddComponentParameter(2, nameof(CascadingValue<int>.ChildContent), content);
        builder.CloseComponent();
    };

    public static RenderFragment Both(RenderFragment first, RenderFragment second) => builder =>
    {
        builder.AddContent(0, first);
        builder.AddContent(1, second);
    };
}
