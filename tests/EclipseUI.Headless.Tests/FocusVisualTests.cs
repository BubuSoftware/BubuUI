using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class FocusVisualTests
{
    [Fact]
    public void SameTarget_InputSwitchUpdatesVisibilityWithoutFocusEvents()
    {
        var button = new Button { Width = 120, Height = 32 };
        using var host = new HeadlessRoot(button); host.UpdateLayout();
        var got = 0; var lost = 0;
        button.AddHandler(Element.GotFocusEvent, _ => got++);
        button.AddHandler(Element.LostFocusEvent, _ => lost++);
        host.Input.PointerPress(new(10, 10)); host.Input.PointerRelease(new(10, 10));
        Assert.True(button.IsFocused); Assert.False(button.IsFocusVisible);
        host.Input.KeyPress(Key.Space);
        Assert.True(button.IsFocusVisible);
        host.Input.PointerMove(new(11, 11)); host.Input.PointerWheel(new(11, 11), 120);
        Assert.True(button.IsFocusVisible);
        host.Input.PointerPress(new(10, 10));
        Assert.False(button.IsFocusVisible); Assert.Equal(1, got); Assert.Equal(0, lost);
    }

    [Fact]
    public void ProgrammaticFocusInheritsModality_ShowAndAlwaysShowAreRootLocal()
    {
        var root = new StackPanel(); var a = new Button(); var b = new Button(); root.Children.Add(a); root.Children.Add(b);
        var focus = new FocusManager(root);
        focus.Focus(a, FocusOrigin.Pointer); focus.Focus(b);
        Assert.False(b.IsFocusVisible); Assert.Equal(FocusOrigin.Programmatic, focus.LastFocusOrigin);
        focus.Focus(b, visibility: FocusVisibility.Show); Assert.True(b.IsFocusVisible);
        focus.NotifyInput(FocusOrigin.Pointer); Assert.False(b.IsFocusVisible);
        focus.AlwaysShowFocusVisuals = true; Assert.True(b.IsFocusVisible);
        var other = new Button(); var otherFocus = new FocusManager(other); otherFocus.Focus(other, FocusOrigin.Pointer);
        Assert.False(other.IsFocusVisible);
        focus.AlwaysShowFocusVisuals = false; Assert.False(b.IsFocusVisible);
    }

    [Fact]
    public void TextEditorsAlwaysRequestVisibleFocus()
    {
        foreach (var editor in new TextBox[] { new(), new PasswordBox(), new NumericUpDown(), new() { IsReadOnly = true } })
        {
            var focus = new FocusManager(editor); focus.Focus(editor, FocusOrigin.Pointer);
            Assert.True(editor.IsFocusVisible); Assert.Equal(FocusVisualMode.Control, editor.EffectiveFocusVisualMode);
        }
    }

    [Fact]
    public void SharedAncestorStateIsCommittedBeforeNotifications()
    {
        var root = new StackPanel(); var a = new Button(); var b = new Button(); root.Children.Add(a); root.Children.Add(b);
        var focus = new FocusManager(root); focus.Focus(a);
        var version = root.RenderVersion;
        a.AddHandler(Element.LostFocusEvent, e =>
        {
            Assert.True(root.IsFocusWithin); Assert.True(b.IsFocused); Assert.True(b.IsFocusVisible);
            Assert.False(a.IsFocused); Assert.False(a.IsFocusWithin); Assert.Same(b, focus.Focused);
            Assert.Equal(FocusOrigin.Keyboard, Assert.IsType<FocusEventArgs>(e).Origin);
        });
        focus.Focus(b, FocusOrigin.Keyboard);
        Assert.Equal(version, root.RenderVersion);
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("hide")]
    [InlineData("detach")]
    [InlineData("dispose")]
    public void InvalidatedTreeClearsOldAncestorPath(string operation)
    {
        var root = new StackPanel(); var panel = new StackPanel(); var button = new Button();
        root.Children.Add(panel); panel.Children.Add(button); var focus = new FocusManager(root); focus.Focus(button);
        switch (operation)
        {
            case "disable": panel.IsEnabled = false; break;
            case "hide": panel.IsVisible = false; break;
            case "detach": root.Children.Remove(panel); break;
            case "dispose": panel.Dispose(); break;
        }
        Assert.Null(focus.Focused); Assert.False(button.IsFocused); Assert.False(button.IsFocusVisible);
        Assert.False(panel.IsFocusWithin); Assert.False(root.IsFocusWithin);
    }

    [Fact]
    public void RejectsForeignAndDisposedTargets()
    {
        var root = new StackPanel(); var focus = new FocusManager(root); var foreign = new Button();
        Assert.False(focus.Focus(foreign)); root.Children.Add(foreign); foreign.Dispose(); Assert.False(focus.Focus(foreign));
    }

    [Fact]
    public void MovingFormerRootClearsItsFocusBeforeNewRootAcceptsIt()
    {
        var subtree = new StackPanel(); var button = new Button(); subtree.Children.Add(button);
        var oldFocus = new FocusManager(subtree); oldFocus.Focus(button);
        var root = new StackPanel(); var newFocus = new FocusManager(root); root.Children.Add(subtree);
        Assert.Null(oldFocus.Focused); Assert.False(button.IsFocused); Assert.False(subtree.IsFocusWithin);
        Assert.False(oldFocus.Focus(button)); Assert.True(newFocus.Focus(button)); Assert.True(root.IsFocusWithin);
    }

    [Fact]
    public void ReplacingManagerRejectsRequestsToOldManagerDuringFocusCallbacks()
    {
        var button = new Button(); var oldFocus = new FocusManager(button); oldFocus.Focus(button);
        button.AddHandler(Element.LostFocusEvent, _ =>
        {
            Assert.False(oldFocus.Focus(button));
            Assert.True(button.RequestFocus());
        });
        var newFocus = new FocusManager(button);
        Assert.Null(oldFocus.Focused); Assert.Same(button, newFocus.Focused); Assert.True(button.IsFocusVisible);
    }

    [Fact]
    public void ControlTabFromEditorMovesFocusToSelectedHeader()
    {
        var tabs = new TabControl(); var editor = new TextBox();
        tabs.Children.Add(new TabItem { Content = editor }); tabs.Children.Add(new TabItem { Content = new TextBox() });
        using var host = new HeadlessRoot(tabs); host.UpdateLayout(); host.Focus.Focus(editor, FocusOrigin.Pointer);
        host.Input.KeyPress(Key.Tab, KeyModifiers.Control);
        Assert.Equal(1, tabs.SelectedIndex); Assert.Same(tabs, host.Focus.Focused); Assert.True(tabs.IsFocusVisible);
        Assert.False(editor.IsFocused); Assert.False(tabs.Children[0].IsFocusWithin);
    }

    [Fact]
    public void ReentrantRequestsAreQueuedAndCyclesAreBounded()
    {
        var root = new StackPanel(); var a = new Button(); var b = new Button(); root.Children.Add(a); root.Children.Add(b);
        var focus = new FocusManager(root);
        a.AddHandler(Element.GotFocusEvent, _ => { Assert.True(a.IsFocused); b.RequestFocus(); Assert.True(a.IsFocused); });
        focus.Focus(a); Assert.Same(b, focus.Focused);
        b.AddHandler(Element.GotFocusEvent, _ => a.RequestFocus());
        Assert.Throws<InvalidOperationException>(() => focus.Focus(a));
        focus.ClearFocus(); Assert.Null(focus.Focused);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PopupRestoreUsesClosingInput(bool keyboard)
    {
        var root = new StackPanel(); var button = new Button(); var editor = new TextBox();
        var popup = new Popup { Content = editor }; root.Children.Add(button); root.Children.Add(popup);
        using var host = new HeadlessRoot(root); host.UpdateLayout(); host.Focus.Focus(button, FocusOrigin.Pointer);
        popup.Open(); host.UpdateLayout(); host.Focus.Focus(editor);
        if (keyboard) host.Input.KeyPress(Key.Escape);
        else host.Input.PointerPress(new(1000, 700));
        Assert.False(popup.IsOpen); Assert.Same(button, host.Focus.Focused);
        Assert.Equal(keyboard, button.IsFocusVisible); Assert.Equal(FocusOrigin.Restore, host.Focus.LastFocusOrigin);
    }

    [Fact]
    public void PopupDoesNotRestoreDetachedTarget()
    {
        var root = new StackPanel(); var button = new Button(); var editor = new TextBox();
        var popup = new Popup { Content = editor }; root.Children.Add(button); root.Children.Add(popup);
        var focus = new FocusManager(root); focus.Focus(button); popup.Open(); focus.Focus(editor);
        root.Children.Remove(button); popup.Close(); Assert.Null(focus.Focused);
    }

    [Fact]
    public void InputCallbackFocusInheritsOriginAndModifiers()
    {
        var root = new StackPanel(); var a = new Button(); var b = new Button(); root.Children.Add(a); root.Children.Add(b);
        using var host = new HeadlessRoot(root); host.UpdateLayout(); host.Focus.Focus(a, FocusOrigin.Pointer);
        FocusEventArgs? received = null;
        b.AddHandler(Element.GotFocusEvent, e => received = Assert.IsType<FocusEventArgs>(e));
        a.AddHandler(Element.KeyDownEvent, _ => b.RequestFocus());
        host.Input.KeyPress(Key.Right, KeyModifiers.Control);
        Assert.NotNull(received); Assert.Equal(FocusOrigin.Keyboard, received.Origin); Assert.Equal(KeyModifiers.Control, received.Modifiers);
        Assert.True(b.IsFocusVisible);
    }

    [Fact]
    public void NestedPopupsRestoreThroughStackAndRootDisposalClearsState()
    {
        var root = new StackPanel(); var button = new Button(); var outerContent = new StackPanel(); var innerButton = new Button();
        var inner = new Popup { Content = new TextBox() }; outerContent.Children.Add(innerButton); outerContent.Children.Add(inner);
        var outer = new Popup { Content = outerContent }; root.Children.Add(button); root.Children.Add(outer);
        var input = new InputRouter(root); input.Focus.Focus(button, FocusOrigin.Pointer);
        outer.Open(); input.Focus.Focus(innerButton); inner.Open(); input.Focus.Focus(inner.Content!);
        input.KeyPress(Key.Escape); Assert.Same(innerButton, input.Focus.Focused); Assert.True(innerButton.IsFocusVisible); Assert.True(outer.IsOpen);
        input.KeyPress(Key.Escape); Assert.Same(button, input.Focus.Focused); Assert.True(button.IsFocusVisible);
        root.Dispose(); Assert.Null(input.Focus.Focused); Assert.False(root.IsFocusWithin); Assert.False(button.IsFocused);
    }

    [Fact]
    public void ThrowingFocusCallbackStillCompletesDetachmentAndQueuedTransfer()
    {
        var root = new StackPanel(); var a = new Button(); var b = new Button(); root.Children.Add(a); root.Children.Add(b);
        using var host = new HeadlessRoot(root); host.Focus.Focus(a);
        a.AddHandler(Element.LostFocusEvent, _ => { b.RequestFocus(); throw new InvalidOperationException("listener"); });
        Assert.Throws<AggregateException>(() => root.Children.Remove(a));
        Assert.False(a.IsAttached); Assert.Null(a.Parent); Assert.False(a.IsFocused);
        Assert.Same(b, host.Focus.Focused); Assert.True(b.IsFocusVisible); Assert.True(root.IsFocusWithin);
    }
}
