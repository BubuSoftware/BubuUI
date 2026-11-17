using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class TextEditingTests
{
    private static (TextBox Editor, HeadlessRoot Host) Create(string text = "")
    {
        var editor = new TextBox { Text = text, Width = 220 };
        var host = new HeadlessRoot(editor); host.UpdateLayout(); host.Focus.Focus(editor); return (editor, host);
    }
    [Fact]
    public void TextInputReplacesSelectionAndUndoRestoresTextAndSelection()
    {
        var (editor, host) = Create("hello world"); using var lifetime = host;
        editor.Select(6, 5); host.Input.TextInput("UI");
        Assert.Equal("hello UI", editor.Text); Assert.Equal(8, editor.CaretIndex);
        host.Input.KeyPress(Key.Z, KeyModifiers.Control);
        Assert.Equal("hello world", editor.Text); Assert.Equal("world", editor.SelectedText);
        host.Input.KeyPress(Key.Y, KeyModifiers.Control); Assert.Equal("hello UI", editor.Text);
    }
    [Theory]
    [InlineData("a😀b", 3, "ab")]
    [InlineData("aéb", 3, "ab")]
    [InlineData("a👩‍💻b", 6, "ab")]
    public void BackspaceDeletesOneTextElement(string value, int caret, string expected)
    {
        var (editor, host) = Create(value); using var lifetime = host;
        editor.CaretIndex = caret; host.KeyPress(Key.Backspace); Assert.Equal(expected, editor.Text);
    }
    [Fact]
    public void ShiftNavigationSelectsAndDeleteKeepsCaretAtSelectionStart()
    {
        var (editor, host) = Create("abc"); using var lifetime = host;
        editor.CaretIndex = 3; host.Input.KeyPress(Key.Left, KeyModifiers.Shift); host.Input.KeyPress(Key.Left, KeyModifiers.Shift);
        Assert.Equal("bc", editor.SelectedText); host.KeyPress(Key.Delete); Assert.Equal("a", editor.Text); Assert.Equal(1, editor.CaretIndex);
    }
    [Fact]
    public void MultilineNavigationAndReturnUseLineBoundaries()
    {
        var (editor, host) = Create("ab\ncd"); using var lifetime = host;
        editor.AcceptsReturn = true; editor.CaretIndex = 4; host.KeyPress(Key.Home); Assert.Equal(3, editor.CaretIndex);
        host.KeyPress(Key.End); Assert.Equal(5, editor.CaretIndex); host.KeyPress(Key.Enter); Assert.Equal("ab\ncd\n", editor.Text);
    }
    [Fact]
    public void ReadOnlyAllowsSelectionAndCopyButRejectsEdits()
    {
        var original = Clipboard.Current; Clipboard.Current = new MemoryClipboard();
        try
        {
            var (editor, host) = Create("copy"); using var lifetime = host; editor.IsReadOnly = true;
            host.Input.KeyPress(Key.A, KeyModifiers.Control); host.Input.KeyPress(Key.C, KeyModifiers.Control);
            Assert.Equal("copy", Clipboard.Current.GetText()); host.Input.TextInput("x"); host.KeyPress(Key.Delete); Assert.Equal("copy", editor.Text);
        }
        finally { Clipboard.Current = original; }
    }
    [Fact]
    public void PasswordDoesNotExposeClipboardContents()
    {
        var original = Clipboard.Current; Clipboard.Current = new MemoryClipboard();
        try { var editor = new PasswordBox { Text = "secret" }; editor.SelectAll(); editor.Copy(); editor.Cut(); Assert.Equal("", Clipboard.Current.GetText()); Assert.Equal("secret", editor.Text); }
        finally { Clipboard.Current = original; }
    }
    [Fact]
    public void CompositionDoesNotChangeTextUntilCommitAndCancelPreservesText()
    {
        var (editor, host) = Create(); using var lifetime = host;
        host.Input.TextComposition("ni", CompositionStage.Updated); Assert.Equal("", editor.Text); Assert.Equal("ni", editor.CompositionText);
        host.Input.TextComposition("你", CompositionStage.Committed); Assert.Equal("你", editor.Text); Assert.Empty(editor.CompositionText);
        host.Input.TextComposition("hao", CompositionStage.Updated); host.Input.TextComposition("", CompositionStage.Cancelled); Assert.Equal("你", editor.Text);
    }
    [Fact]
    public void MaxLengthDoesNotSplitSurrogatePairs()
    {
        var (editor, host) = Create(); using var lifetime = host; editor.MaxLength = 2;
        host.Input.TextInput("a😀"); Assert.Equal("a", editor.Text);
        editor.SelectAll(); host.Input.TextInput("😀x"); Assert.Equal("😀", editor.Text);
    }
    [Fact]
    public void PointerDragSelectsTextAndCaretCanReachEnd()
    {
        var (editor, host) = Create("abcd"); using var lifetime = host;
        var start = new Point(editor.HorizontalPadding, editor.VerticalPadding + 2);
        host.PointerPress(start); host.PointerMove(new(200, start.Y)); host.PointerRelease(new(200, start.Y));
        Assert.Equal("abcd", editor.SelectedText); Assert.Equal(4, editor.CaretIndex);
    }
    [Fact]
    public void KeyUpRetainsModifiersAndShiftTabMovesBackward()
    {
        var panel = new StackPanel(); var first = new TextBox(); var second = new Button { Label = "next" }; panel.Children.Add(first); panel.Children.Add(second);
        using var host = new HeadlessRoot(panel); host.UpdateLayout(); host.Focus.Focus(second);
        host.Input.KeyPress(Key.Tab, KeyModifiers.Shift); Assert.Same(first, host.Focus.Focused);
        KeyEventArgs? received = null; first.AddHandler(Element.KeyUpEvent, e => received = (KeyEventArgs)e);
        host.Input.KeyRelease(Key.A, KeyModifiers.Control | KeyModifiers.Shift); Assert.Equal(KeyModifiers.Control | KeyModifiers.Shift, received!.Modifiers);
    }
}
