using System;
using System.Threading;
using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class HostLifecycleTests
{
    // A code-authored single-content object uses the same host protocol as Window.
    private sealed class Shell : UiObject
    {
        public Element? Body { get => SingleContent; set => SingleContent = value; }
        public Element? NextBody;
        public Action? Attached;
        public int InitializationCount;
        public int AttachmentCount;
        public int DetachmentCount;
        public SynchronizationContext? ContextAtDetachment;

        protected override void OnInitialized() => InitializationCount++;
        protected override void OnAttached()
        {
            AttachmentCount++;
            Attached?.Invoke();
        }
        protected override void OnDetached()
        {
            DetachmentCount++;
            ContextAtDetachment = SynchronizationContext.Current;
        }
        protected override void Render()
        {
            if (NextBody is not { } next) return;
            NextBody = null;
            Body = next;
        }
    }

    private sealed class Probe : StackPanel
    {
        public int AttachmentCount;
        public int DetachmentCount;
        protected override void OnAttached() => AttachmentCount++;
        protected override void OnDetached() => DetachmentCount++;
    }

    [Fact]
    public void DisposingHostDetachesWithoutDisposingTree_AndAllowsAnotherHost()
    {
        var content = new Probe();
        var shell = new Shell { Body = content };
        var first = new HeadlessRoot(shell);
        Assert.True(shell.IsAttached);
        Assert.True(content.IsAttached);

        first.Dispose();
        first.Dispose();

        Assert.False(shell.IsAttached);
        Assert.False(content.IsAttached);
        Assert.False(shell.IsDisposed);
        Assert.False(content.IsDisposed);
        Assert.Equal(1, shell.DetachmentCount);
        Assert.Equal(1, content.DetachmentCount);
        Assert.Throws<ObjectDisposedException>(() => first.UpdateLayout());
        Assert.Throws<ObjectDisposedException>(() => first.InstallSynchronizationContext());
        Assert.Throws<ObjectDisposedException>(() => first.DrainPostedWork());
        Assert.Throws<ObjectDisposedException>(() => first.PointerPress(Point.Zero));

        using var second = new HeadlessRoot(shell);
        Assert.True(shell.IsAttached);
        Assert.Equal(1, shell.InitializationCount);
        Assert.Equal(2, shell.AttachmentCount);
        Assert.Equal(2, content.AttachmentCount);
    }

    [Fact]
    public void AttachmentHookCanReplaceContent_BeforeHostIsReturned()
    {
        var oldContent = new Probe();
        var newContent = new Probe();
        var shell = new Shell { Body = oldContent };
        shell.Attached = () => shell.Body = newContent;

        using var host = new HeadlessRoot(shell);

        Assert.Same(newContent, host.Content);
        Assert.True(newContent.IsAttached);
        Assert.False(oldContent.IsAttached);
        Assert.False(oldContent.IsDisposed);
        Assert.Equal(1, newContent.AttachmentCount);
    }

    [Fact]
    public void ReplacingContentSwitchesLayoutAndInput_AndClearsOldInputWithoutClick()
    {
        var oldContent = new Button { Label = "old", Width = 100, Height = 40 };
        var newContent = new Button { Label = "new", Width = 160, Height = 50 };
        var shell = new Shell { Body = oldContent };
        using var host = new HeadlessRoot(shell);
        host.UpdateLayout();
        var previousInput = host.Input;
        var oldClicks = 0;
        var newClicks = 0;
        oldContent.AddHandler(Button.ClickEvent, () => oldClicks++);
        newContent.AddHandler(Button.ClickEvent, () => newClicks++);
        host.PointerPress(new Point(10, 10));
        Assert.Same(oldContent, previousInput.Captured);
        Assert.True(oldContent.IsPressed);

        shell.Body = newContent;
        host.UpdateLayout();

        Assert.Same(newContent, host.Content);
        Assert.NotSame(previousInput, host.Input);
        Assert.Null(previousInput.Captured);
        Assert.Null(previousInput.Hovered);
        Assert.Null(previousInput.Focus.Focused);
        Assert.False(oldContent.IsPressed);
        Assert.False(oldContent.IsHovered);
        Assert.False(oldContent.IsFocused);
        Assert.Equal(160, newContent.FinalRect.Width);
        Assert.Equal(50, newContent.FinalRect.Height);
        Assert.Same(newContent, host.HitTest(new Point(10, 10)));

        host.PointerRelease(new Point(10, 10));
        Assert.Equal(0, oldClicks);
        Assert.Equal(0, newClicks);
        host.PointerPress(new Point(10, 10));
        host.PointerRelease(new Point(10, 10));
        Assert.Equal(1, newClicks);
    }

    [Fact]
    public void RenderCanReplaceContent_AndLayoutUsesNewRootInSameUpdate()
    {
        var shell = new Shell { Body = new Probe() };
        var replacement = new Button { Width = 123, Height = 45 };
        using var host = new HeadlessRoot(shell);
        shell.NextBody = replacement;
        shell.Invalidate();

        host.UpdateLayout();

        Assert.Same(replacement, host.Content);
        Assert.Equal(123, replacement.FinalRect.Width);
        Assert.Equal(45, replacement.FinalRect.Height);
    }

    [Fact]
    public void FocusCleanupCanReplaceContentAgain_AndHostUsesTheFinalRoot()
    {
        var oldContent = new Button { Width = 100, Height = 40 };
        var intermediate = new Button { Width = 120, Height = 40 };
        var final = new Button { Width = 140, Height = 40 };
        var shell = new Shell { Body = oldContent };
        using var host = new HeadlessRoot(shell);
        host.UpdateLayout();
        Assert.True(host.Focus.Focus(oldContent));
        oldContent.AddHandler(Element.LostFocusEvent, () => shell.Body = final);
        shell.Body = intermediate;

        host.UpdateLayout();

        Assert.Same(final, host.Content);
        Assert.Equal(140, final.FinalRect.Width);
    }

    [Fact]
    public void HostDisposalDetachesWhileItsSynchronizationContextIsStillInstalled()
    {
        var previous = SynchronizationContext.Current;
        var shell = new Shell { Body = new Probe() };
        var host = new HeadlessRoot(shell);
        using var scope = host.InstallSynchronizationContext();

        host.Dispose();

        Assert.Same(scope.Context, shell.ContextAtDetachment);
        Assert.Same(previous, SynchronizationContext.Current);
        Assert.Null(host.SynchronizationContext);
        Assert.Throws<ObjectDisposedException>(() => scope.Drain());
    }

    [Fact]
    public void DisposingOuterHostDoesNotReplaceInnerContext_OrRestoreEndedOuterContext()
    {
        var previous = SynchronizationContext.Current;
        var outer = new HeadlessRoot(new Probe());
        var outerScope = outer.InstallSynchronizationContext();
        using var inner = new HeadlessRoot(new Probe());
        var innerScope = inner.InstallSynchronizationContext();
        try
        {
            outer.Dispose();
            Assert.Same(innerScope.Context, SynchronizationContext.Current);
            innerScope.Dispose();
            Assert.Same(previous, SynchronizationContext.Current);
        }
        finally
        {
            innerScope.Dispose();
            outer.Dispose();
            outerScope.Dispose();
        }
    }

    [Fact]
    public void NestedScopesCanEndOutOfOrder_WithoutLosingCurrentHostContext()
    {
        var previous = SynchronizationContext.Current;
        using var host = new HeadlessRoot(new Probe());
        using var outer = host.InstallSynchronizationContext();
        using var inner = host.InstallSynchronizationContext();

        outer.Dispose();
        Assert.Same(inner.Context, host.SynchronizationContext);
        Assert.Same(inner.Context, SynchronizationContext.Current);
        inner.Dispose();
        Assert.Null(host.SynchronizationContext);
        Assert.Same(previous, SynchronizationContext.Current);
    }

    [Fact]
    public void DisposingHostFromPostedWorkDoesNotResurrectItsContextAfterDrain()
    {
        var previous = SynchronizationContext.Current;
        var shell = new Shell { Body = new Probe() };
        using var host = new HeadlessRoot(shell);
        using var scope = host.InstallSynchronizationContext();
        scope.Context.Post(_ => host.Dispose(), null);

        Assert.Equal(1, host.DrainPostedWork());

        Assert.False(shell.IsAttached);
        Assert.Same(previous, SynchronizationContext.Current);
    }

    [Fact]
    public void OffThreadScopeAndHostDisposalAreRejectedBeforeStateChanges_AndCanBeRetried()
    {
        var previous = SynchronizationContext.Current;
        var root = new Probe();
        using var host = new HeadlessRoot(root);
        using var scope = host.InstallSynchronizationContext();
        Exception? scopeFailure = null;
        Exception? hostFailure = null;
        var thread = new Thread(() =>
        {
            try { scope.Dispose(); }
            catch (Exception error) { scopeFailure = error; }
            try { host.Dispose(); }
            catch (Exception error) { hostFailure = error; }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));

        Assert.IsType<UiThreadViolationException>(scopeFailure);
        Assert.IsType<UiThreadViolationException>(hostFailure);
        Assert.True(root.IsAttached);
        Assert.Same(scope.Context, host.SynchronizationContext);
        Assert.Same(scope.Context, SynchronizationContext.Current);
        host.UpdateLayout();

        host.Dispose();

        Assert.False(root.IsAttached);
        Assert.Null(host.SynchronizationContext);
        Assert.Same(previous, SynchronizationContext.Current);
    }
}
