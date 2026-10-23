using System.Runtime.ExceptionServices;
using HomeBlaze.Components.Abstractions.Attributes;
using HomeBlaze.Components.Tests.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

// The widgets are set up by hand, as their parent would pass their parameters, and a renderer is the only way to give a
// component the handle that a change of the selection renders it with.
#pragma warning disable BL0005, BL0006

namespace HomeBlaze.Components.Tests;

public class WidgetSelectionTests
{
    [Fact]
    public async Task WhenTheSubjectToSelectHasNoWidgetYet_ThenTheSelectionStays()
    {
        await using var renderer = new SelectionRenderer();
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            // Arrange
            var selection = new WidgetSelection();
            var page = renderer.CreateWidget(selection, type: SubjectComponentType.Page);
            var card = renderer.CreateWidget(selection, page);
            selection.Select(card);

            // Act
            selection.SelectSubject(new PersistedDevice(), page);

            // Assert
            Assert.Same(card, selection.Selected);
        });
    }

    [Fact]
    public async Task WhenTheSubjectToSelectHasNoWidgetYet_ThenItIsPendingAndTheChangeIsRaised()
    {
        await using var renderer = new SelectionRenderer();
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            // Arrange: a widget that shows the subject, such as a tab layout that shows a new tab, learns it this way.
            var selection = new WidgetSelection();
            var page = renderer.CreateWidget(selection, type: SubjectComponentType.Page);
            var subject = new PersistedDevice();
            var changeCount = 0;
            selection.Changed += () => changeCount++;

            // Act
            selection.SelectSubject(subject, page);

            // Assert
            Assert.Same(subject, selection.PendingSubject);
            Assert.Equal(1, changeCount);
        });
    }

    [Fact]
    public async Task WhenAWidgetIsSelectedWhileASubjectIsPending_ThenNoSubjectIsPending()
    {
        await using var renderer = new SelectionRenderer();
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            // Arrange
            var selection = new WidgetSelection();
            var page = renderer.CreateWidget(selection, type: SubjectComponentType.Page);
            var card = renderer.CreateWidget(selection, page);
            selection.SelectSubject(new PersistedDevice(), page);

            // Act
            selection.Select(card);

            // Assert
            Assert.Null(selection.PendingSubject);
        });
    }

    [Fact]
    public async Task WhenTheSelectedWidgetIsRemoved_ThenTheWidgetAroundItIsSelected()
    {
        await using var renderer = new SelectionRenderer();
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            // Arrange
            var selection = new WidgetSelection();
            var page = renderer.CreateWidget(selection, type: SubjectComponentType.Page);
            var card = renderer.CreateWidget(selection, page);
            var text = renderer.CreateWidget(selection, card);
            selection.Select(text);

            // Act
            text.Dispose();

            // Assert
            Assert.Same(card, selection.Selected);
        });
    }

    [Fact]
    public async Task WhenTheSelectedWidgetIsRemovedAfterTheWidgetAroundIt_ThenTheNextWidgetOutsideIsSelected()
    {
        await using var renderer = new SelectionRenderer();
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            // Arrange: a render disposes a widget before the widgets inside it.
            var selection = new WidgetSelection();
            var page = renderer.CreateWidget(selection, type: SubjectComponentType.Page);
            var layout = renderer.CreateWidget(selection, page);
            var card = renderer.CreateWidget(selection, layout);
            var text = renderer.CreateWidget(selection, card);
            selection.Select(text);

            // Act
            card.Dispose();
            text.Dispose();

            // Assert
            Assert.Same(layout, selection.Selected);
        });
    }

    [Fact]
    public async Task WhenTheSelectedWidgetIsRemovedWhileASubjectIsPending_ThenThePendingSubjectIsSelectedOnceItRenders()
    {
        await using var renderer = new SelectionRenderer();
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            // Arrange: an edit of text in a card replaces the widget of the text after the edit asked for its new subject.
            var selection = new WidgetSelection();
            var page = renderer.CreateWidget(selection, type: SubjectComponentType.Page);
            var card = renderer.CreateWidget(selection, page);
            var oldText = renderer.CreateWidget(selection, card);
            selection.Select(oldText);
            var newSubject = new PersistedDevice();
            selection.SelectSubject(newSubject, page);

            // Act
            oldText.Dispose();
            var newText = renderer.CreateWidget(selection, card, newSubject);
            selection.OnRendered(newText);

            // Assert
            Assert.Same(newText, selection.Selected);
        });
    }

    [Fact]
    public async Task WhenAPendingSubjectRendersUnselectable_ThenItIsSelectedOnlyOnceItRendersSelectable()
    {
        await using var renderer = new SelectionRenderer();
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            // Arrange
            var selection = new WidgetSelection();
            var page = renderer.CreateWidget(selection, type: SubjectComponentType.Page);
            var card = renderer.CreateWidget(selection, page);
            selection.Select(card);
            var subject = new PersistedDevice();
            selection.SelectSubject(subject, page);
            var text = renderer.CreateWidget(selection, card, subject);
            text.IsSelectionEnabled = false;

            // Act
            selection.OnRendered(text);
            var selectedWhileUnselectable = selection.Selected;
            text.IsSelectionEnabled = true;
            selection.OnRendered(text);

            // Assert
            Assert.Same(card, selectedWhileUnselectable);
            Assert.Same(text, selection.Selected);
        });
    }

    [Fact]
    public async Task WhenAWidgetIsSelectedWhileASubjectIsPending_ThenThePendingSubjectIsNotSelectedLater()
    {
        await using var renderer = new SelectionRenderer();
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            // Arrange
            var selection = new WidgetSelection();
            var page = renderer.CreateWidget(selection, type: SubjectComponentType.Page);
            var card = renderer.CreateWidget(selection, page);
            var subject = new PersistedDevice();
            selection.SelectSubject(subject, page);

            // Act
            selection.Select(card);
            var widget = renderer.CreateWidget(selection, page, subject);
            selection.OnRendered(widget);

            // Assert
            Assert.Same(card, selection.Selected);
        });
    }

    [Fact]
    public async Task WhenAPendingSubjectIsSelected_ThenItsWidgetTakesTheFocusOnce()
    {
        await using var renderer = new SelectionRenderer();
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            // Arrange
            var selection = new WidgetSelection();
            var page = renderer.CreateWidget(selection, type: SubjectComponentType.Page);
            var subject = new PersistedDevice();
            selection.SelectSubject(subject, page);
            var widget = renderer.CreateWidget(selection, page, subject);
            selection.OnRendered(widget);

            // Act
            var takesFocus = selection.TakesFocus(widget);
            var takesFocusAgain = selection.TakesFocus(widget);

            // Assert
            Assert.True(takesFocus);
            Assert.False(takesFocusAgain);
        });
    }

    [Fact]
    public async Task WhenAWidgetIsSelectedByTheUser_ThenItDoesNotTakeTheFocus()
    {
        await using var renderer = new SelectionRenderer();
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            // Arrange
            var selection = new WidgetSelection();
            var page = renderer.CreateWidget(selection, type: SubjectComponentType.Page);
            var card = renderer.CreateWidget(selection, page);
            selection.SelectSubject(new PersistedDevice(), page);

            // Act
            selection.Select(card);

            // Assert
            Assert.False(selection.TakesFocus(card));
        });
    }

    private sealed class SelectionRenderer : Renderer
    {
        public SelectionRenderer()
            : base(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance)
        {
        }

        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        // A selectable widget of a page in edit mode, inside the given parent.
        public SubjectComponent CreateWidget(
            WidgetSelection selection,
            SubjectComponent? parent = null,
            PersistedDevice? subject = null,
            SubjectComponentType type = SubjectComponentType.Widget)
        {
            var component = new SubjectComponent
            {
                Subject = subject ?? new PersistedDevice(),
                Type = type,
                IsEditing = true,
                Selection = selection,
                ParentComponent = parent
            };

            AssignRootComponentId(component);
            return component;
        }

        // Renders nothing: the tests check the selection, and the markup of a widget needs the services of a page.
        protected override void ProcessPendingRender()
        {
        }

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

        protected override void HandleException(Exception exception) => ExceptionDispatchInfo.Capture(exception).Throw();
    }
}
