using BubuUI.Elements;
using Xunit;

namespace BubuUI.Headless.Tests;

public class GraphemeEditBoundaryTests
{
    [Theory]
    [InlineData("👨👩", 2, "\u200d", "👨‍👩", 5)]
    [InlineData("\u0301b", 0, "a", "a\u0301b", 2)]
    public void InputThatJoinsExistingTextLeavesCaretAfterTheCombinedCharacter(
        string original, int position, string input, string joined, int expectedCaret)
    {
        using var editor = new TextBox { Text = original, Width = 300 };
        using var host = new HeadlessRoot(editor);
        host.UpdateLayout(); host.Focus.Focus(editor); editor.CaretIndex = position;

        host.Input.TextInput(input);
        Assert.Equal(joined, editor.Text);
        Assert.Equal(expectedCaret, editor.CaretIndex);
        Assert.Equal(0, editor.SelectionLength);

        editor.Undo();
        Assert.Equal(original, editor.Text);
        Assert.Equal(position, editor.CaretIndex);
        editor.Redo();
        Assert.Equal(joined, editor.Text);
        Assert.Equal(expectedCaret, editor.CaretIndex);

        host.Input.TextInput("X");
        Assert.Equal(joined.Insert(expectedCaret, "X"), editor.Text);
        host.KeyPress(Key.Backspace);
        Assert.Equal(joined, editor.Text);
        Assert.Equal(expectedCaret, editor.CaretIndex);
    }

    [Fact]
    public void DeletingBetweenRegionalIndicatorsKeepsTheNewFlagIntact()
    {
        using var editor = new TextBox { Text = "🇺x🇸", Width = 300 };
        using var host = new HeadlessRoot(editor);
        host.UpdateLayout(); host.Focus.Focus(editor); editor.Select(2, 1);

        host.KeyPress(Key.Delete);
        Assert.Equal("🇺🇸", editor.Text);
        Assert.Equal(4, editor.CaretIndex);
        editor.Undo();
        Assert.Equal("x", editor.SelectedText);
        editor.Redo();
        Assert.Equal(4, editor.CaretIndex);
        host.KeyPress(Key.Backspace);
        Assert.Empty(editor.Text);
    }
}
