using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Recorder;

// ReSharper disable StaticMemberInGenericType

namespace Namotion.Interceptor.Blazor;

public class TrackingComponentBase<TSubject> : ComponentBase, IDisposable
    where TSubject : IInterceptorSubject
{
    private IDisposable? _subscription;

    private ConcurrentDictionary<PropertyReference, bool> _collectingProperties = [];
    private ConcurrentDictionary<PropertyReference, bool> _properties = [];

    // Cache
    private static readonly BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly FieldInfo? ComponentBaseRenderFragmentField;
    private static readonly FieldInfo? BuilderEntriesField;
    private static readonly FieldInfo? EntriesItemsField;
    private static readonly FieldInfo? EntriesCountField;
    private static readonly FieldInfo? FrameComponentKeyField;
    private static readonly FieldInfo? FrameElementKeyField;
    private static readonly FieldInfo? FrameAttributeValueField;

    private static readonly MethodInfo? WrapGenericDef;
    private static readonly ConcurrentDictionary<Type, MethodInfo> WrapGenericCache = new();

    [Inject]
    public TSubject? Subject { get; set; }

    protected override void OnInitialized()
    {
        _subscription = Subject?
            .Context
            .GetPropertyChangeObservable()
            .Subscribe(change =>
            {
                if (_properties.TryGetValue(change.Property, out _))
                {
                    InvokeAsync(StateHasChanged);
                }
            });
        
        // Wrap the root render fragment
        if (ComponentBaseRenderFragmentField?.GetValue(this) is RenderFragment renderFragment)
        {
            // This render fragment will be called before OnAfterRender
            ComponentBaseRenderFragmentField.SetValue(this, (RenderFragment)(builder =>
            {
                _collectingProperties.Clear();

                using var recorder = ReadPropertyRecorder.Start(_collectingProperties);
                renderFragment(builder);

                WrapNestedFragmentsInBuilderFrames(builder, recorder);
            }));
        }
    }

    protected override void OnAfterRender(bool firstRender)
    {
        (_properties, _collectingProperties) = (_collectingProperties, _properties);
        base.OnAfterRender(firstRender);
    }

    private static RenderFragment WrapRenderFragment(RenderFragment renderFragment, ReadPropertyRecorderScope? scope)
    {
        scope ??= ReadPropertyRecorder.Scopes.Value!.Single().Key;
        return builder =>
        { 
            using var recorder = ReadPropertyRecorder.Start(scope);
            renderFragment(builder);

            WrapNestedFragmentsInBuilderFrames(builder, scope);
        };
    }

    private static RenderFragment<T> WrapRenderFragment<T>(RenderFragment<T> renderFragment, ReadPropertyRecorderScope? scope)
    {
        scope ??= ReadPropertyRecorder.Scopes.Value!.Single().Key;
        return value => builder =>
        {
            using var recorder = ReadPropertyRecorder.Start(scope);
            var inner = renderFragment(value);
            inner(builder);

            WrapNestedFragmentsInBuilderFrames(builder, scope);
        };
    }

#pragma warning disable BL0006

    private static void WrapNestedFragmentsInBuilderFrames(RenderTreeBuilder builder, ReadPropertyRecorderScope scope)
    {
        if (BuilderEntriesField?.GetValue(builder) is {} entries && 
            EntriesItemsField?.GetValue(entries) is RenderTreeFrame[] frames)
        {
            var countObj = EntriesCountField?.GetValue(entries);
            var count = countObj is int i ? i : frames.Length;

            for (var index = 0; index < count; index++)
            {
                var frame = frames[index];

                if (FrameComponentKeyField is not null)
                {
                    frame = WrapFieldIfFragment(frame, FrameComponentKeyField, scope);
                }

                if (FrameElementKeyField is not null)
                {
                    frame = WrapFieldIfFragment(frame, FrameElementKeyField, scope);
                }

                if (FrameAttributeValueField is not null)
                {
                    frame = WrapFieldIfFragment(frame, FrameAttributeValueField, scope);
                }

                frames[index] = frame;
            }
        }
    }

    private static RenderTreeFrame WrapFieldIfFragment(RenderTreeFrame frame, FieldInfo field, ReadPropertyRecorderScope scope)
    {
        var value = field.GetValue(frame);
        if (value is RenderFragment rf)
        {
            var tr = __makeref(frame);
            field.SetValueDirect(tr, WrapRenderFragment(rf, scope));
            return frame;
        }
        
        if (value is Delegate del)
        {
            var delType = del.GetType();
            if (delType.IsGenericType && delType.GetGenericTypeDefinition() == typeof(RenderFragment<>))
            {
                var tArg = delType.GetGenericArguments()[0];
                var closed = WrapGenericCache.GetOrAdd(tArg, static (t, def) => def.MakeGenericMethod(t), WrapGenericDef!);
                var wrapped = closed.Invoke(null, new object[] { del, scope });
                var tr = __makeref(frame);
                field.SetValueDirect(tr, wrapped!);
                return frame;
            }
        }

        return frame;
    }

#pragma warning restore BL0006

    public virtual void Dispose()
    {
        _subscription?.Dispose();
    }

    static TrackingComponentBase()
    {
        // Cache ComponentBase internals
        ComponentBaseRenderFragmentField = typeof(ComponentBase).GetField("_renderFragment", NonPublicInstance);

        // Cache RenderTreeBuilder internals
        var builderType = typeof(RenderTreeBuilder);
        BuilderEntriesField = builderType.GetField("_entries", NonPublicInstance);
        var entriesType = BuilderEntriesField?.FieldType;
        if (entriesType is not null)
        {
            EntriesItemsField = entriesType.GetField("_items", NonPublicInstance);
            EntriesCountField = entriesType.GetField("_count", NonPublicInstance);
        }

        // Cache RenderTreeFrame fields that can hold fragments
        var frameType = typeof(RenderTreeFrame);
        FrameComponentKeyField = frameType.GetField("ComponentKeyField", NonPublicInstance);
        FrameElementKeyField = frameType.GetField("ElementKeyField", NonPublicInstance);
        FrameAttributeValueField = frameType.GetField("AttributeValueField", NonPublicInstance);

        WrapGenericDef = typeof(TrackingComponentBase<TSubject>)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == nameof(WrapRenderFragment) && m.IsGenericMethodDefinition && m.GetParameters().Length == 2);
    }
}