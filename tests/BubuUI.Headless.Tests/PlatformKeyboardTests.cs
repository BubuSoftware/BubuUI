using BubuUI.Elements;
using Xunit;

namespace BubuUI.Headless.Tests;

public sealed class MacOSKeyboardFactAttribute : FactAttribute
{
    public MacOSKeyboardFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "Requires macOS keyboard conventions.";
    }
}

public class PlatformKeyboardTests
{
    private static KeyModifiers Command => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private static (TextBox Editor, HeadlessRoot Host) CreateEditor(string text)
    {
        var editor = new TextBox { Text = text, Width = 240, AcceptsReturn = true };
        var host = new HeadlessRoot(editor);
        host.UpdateLayout();
        host.Focus.Focus(editor);
        return (editor, host);
    }

    [Fact]
    public void NativeMetaAndControlModifiersRemainDistinctWhenRouted()
    {
        var button = new Button();
        using var host = new HeadlessRoot(button);
        host.UpdateLayout(); host.Focus.Focus(button);
        var received = new List<KeyModifiers>();
        button.AddHandler(Element.KeyDownEvent, e => received.Add(((KeyEventArgs)e).Modifiers));
        host.Input.KeyPress(Key.A, KeyModifiers.Meta);
        host.Input.KeyPress(Key.A, KeyModifiers.Control);
        Assert.Equal(new[] { KeyModifiers.Meta, KeyModifiers.Control }, received);
    }

    [Fact]
    public void PlatformCommandPerformsClipboardUndoAndRedo()
    {
        var original = Clipboard.Current;
        Clipboard.Current = new MemoryClipboard();
        try
        {
            var (editor, host) = CreateEditor("hello world");
            using var lifetime = host;
            editor.Select(6, 5);
            host.Input.KeyPress(Key.C, Command);
            Assert.Equal("world", Clipboard.Current.GetText());
            host.Input.KeyPress(Key.X, Command);
            Assert.Equal("hello ", editor.Text);
            host.Input.KeyPress(Key.V, Command);
            Assert.Equal("hello world", editor.Text);
            host.Input.KeyPress(Key.Z, Command);
            Assert.Equal("hello ", editor.Text);
            host.Input.KeyPress(Key.Z, Command | KeyModifiers.Shift);
            Assert.Equal("hello world", editor.Text);
            host.Input.KeyPress(Key.A, Command);
            Assert.Equal("hello world", editor.SelectedText);
        }
        finally { Clipboard.Current = original; }
    }

    [Fact]
    public void MetaSelectAllOnlyUsesMacOSCommandConvention()
    {
        var (editor, host) = CreateEditor("text");
        using var lifetime = host;
        host.Input.KeyPress(Key.A, KeyModifiers.Meta);
        Assert.Equal(OperatingSystem.IsMacOS() ? "text" : "", editor.SelectedText);
    }

    [Fact]
    public void PlatformCommandTogglesExtendedSelectionAndSelectsAllItems()
    {
        var list = new ListBox { SelectionMode = SelectionMode.Extended, ItemsSource = new[] { "a", "b", "c" } };
        using var host = new HeadlessRoot(list);
        host.UpdateLayout(); host.Focus.Focus(list);
        list.Select(0);
        list.Select(2, Command);
        Assert.Equal(new object?[] { "a", "c" }, list.SelectedItems);
        list.Select(0, Command);
        Assert.Equal(new object?[] { "c" }, list.SelectedItems);
        host.Input.KeyPress(Key.A, Command);
        Assert.Equal(new object?[] { "a", "b", "c" }, list.SelectedItems);
    }

    [MacOSKeyboardFact]
    public void CommandArrowsMoveToLineAndDocumentBoundariesAndShiftExtendsSelection()
    {
        var (editor, host) = CreateEditor("first\nsecond\nthird");
        using var lifetime = host;
        editor.CaretIndex = 8;
        host.Input.KeyPress(Key.Left, KeyModifiers.Meta);
        Assert.Equal(6, editor.CaretIndex);
        host.Input.KeyPress(Key.Right, KeyModifiers.Meta | KeyModifiers.Shift);
        Assert.Equal("second", editor.SelectedText);
        host.Input.KeyPress(Key.Up, KeyModifiers.Meta);
        Assert.Equal(0, editor.CaretIndex);
        host.Input.KeyPress(Key.Down, KeyModifiers.Meta | KeyModifiers.Shift);
        Assert.Equal(editor.Text, editor.SelectedText);
    }

    [MacOSKeyboardFact]
    public void OptionMovesAndDeletesWordsWithoutSplittingGraphemes()
    {
        var (editor, host) = CreateEditor("aé 👩‍💻 hello");
        using var lifetime = host;
        editor.CaretIndex = editor.Text.Length;
        host.Input.KeyPress(Key.Left, KeyModifiers.Alt);
        Assert.Equal("aé 👩‍💻 ".Length, editor.CaretIndex);
        host.Input.KeyPress(Key.Left, KeyModifiers.Alt | KeyModifiers.Shift);
        Assert.Equal("👩‍💻 ", editor.SelectedText);
        host.Input.KeyPress(Key.Backspace);
        Assert.Equal("aé hello", editor.Text);
        editor.CaretIndex = 0;
        host.Input.KeyPress(Key.Right, KeyModifiers.Alt);
        Assert.Equal(3, editor.CaretIndex);
        host.Input.KeyPress(Key.Delete, KeyModifiers.Alt);
        Assert.Equal("aé", editor.Text);
        host.Input.KeyPress(Key.Backspace, KeyModifiers.Alt);
        Assert.Equal("", editor.Text);
    }

    [MacOSKeyboardFact]
    public void CommandBackspaceDeletesToLineStartAndUndoRestoresIt()
    {
        var (editor, host) = CreateEditor("first\nsecond");
        using var lifetime = host;
        editor.CaretIndex = 9;
        host.Input.KeyPress(Key.Backspace, KeyModifiers.Meta);
        Assert.Equal("first\nond", editor.Text);
        Assert.Equal(6, editor.CaretIndex);
        host.Input.KeyPress(Key.Z, KeyModifiers.Meta);
        Assert.Equal("first\nsecond", editor.Text);
        Assert.Equal(9, editor.CaretIndex);
        editor.IsReadOnly = true;
        host.Input.KeyPress(Key.Backspace, KeyModifiers.Meta);
        Assert.Equal("first\nsecond", editor.Text);
    }
}
