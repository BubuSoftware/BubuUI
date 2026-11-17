using EclipseUI.Elements;
using EclipseUI.Themes.Fluent;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class LooklessTextControlTests
{
    [Fact]
    public void EditorsKeepTheirBehaviorWithoutDrawingAnImplicitSkin()
    {
        foreach (var editor in new TextBox[] { new(), new PasswordBox(), new NumericUpDown() })
        {
            using var host = new HeadlessRoot(editor);
            host.UpdateLayout(); host.Focus.Focus(editor);
            editor.Text = "value"; editor.SelectAll(); host.Input.TextInput("changed");
            Assert.Equal("changed", editor.Text);
            Assert.Equal(0, editor.HorizontalPadding);
            Assert.Equal(0, editor.VerticalPadding);
            var drawing = new Drawing(); editor.Render(drawing);
            Assert.Empty(drawing.Texts); Assert.Equal(0, drawing.Fills);
            Assert.Null(editor.TemplateRoot);
        }
    }

    [Fact]
    public void GenericPresenterMasksPasswordTextAndComposition()
    {
        using var editor = new PasswordBox { Text = "secret", Width = 200, Height = 40 };
        editor.Template = new FuncControlTemplate<PasswordBox>(owner => new(new TextPresenter
        {
            Editor = owner, Foreground = new(12, 34, 56), CaretBrush = new(12, 34, 56),
            CompositionUnderlineBrush = new(1, 2, 3), SelectionBackground = new(4, 5, 6)
        }));
        using var host = new HeadlessRoot(editor); host.UpdateLayout(); host.Focus.Focus(editor);
        editor.CaretIndex = editor.Text.Length;
        host.Input.TextComposition("你好", CompositionStage.Updated); host.UpdateLayout();
        var drawing = new Drawing(); editor.TemplateRoot!.Render(drawing);
        Assert.Contains("●●●●●●", drawing.Texts);
        Assert.Contains("●●", drawing.Texts);
        Assert.DoesNotContain(drawing.Texts, text => text.Contains("secret") || text.Contains("你好"));
        Assert.True(drawing.Fills > 0);
    }

    [Fact]
    public void ReplacingTheTextTemplatePreservesEditingStateAndUpdatesPointerGeometry()
    {
        using var editor = new TextBox { Text = "abcd", Width = 200, Height = 60 };
        static ControlTemplate Template(double inset) => new FuncControlTemplate<TextBox>(owner => new(new Border
        {
            Padding = inset, BorderThickness = 0,
            Content = new TextPresenter { Editor = owner }
        }));
        editor.Template = Template(10);
        using var host = new HeadlessRoot(editor); host.UpdateLayout(); host.Focus.Focus(editor);
        editor.Select(1, 2);
        var first = editor.Descendants().OfType<TextPresenter>().Single();
        editor.Template = Template(20); host.UpdateLayout();
        Assert.True(first.IsDisposed);
        Assert.Equal("abcd", editor.Text); Assert.Equal("bc", editor.SelectedText);
        Assert.Same(editor, host.Focus.Focused);
        editor.CaretIndex = 0;
        Assert.Equal(20, editor.CaretRect.X);
        host.Input.PointerPress(new(20, 30)); host.Input.PointerRelease(new(20, 30));
        Assert.Equal(0, editor.CaretIndex);
    }

    [Fact]
    public void FluentEditorsHaveDistinctCompiledThemesAndRetainTextAcrossPaletteChanges()
    {
        foreach (var editor in new TextBox[] { new(), new PasswordBox(), new NumericUpDown() })
        {
            editor.Text = "keep"; editor.SetThemeDefaults(FluentTheme.Light);
            using var host = new HeadlessRoot(editor); host.UpdateLayout(); host.Focus.Focus(editor);
            editor.Select(1, 2);
            var template = editor.TemplateRoot;
            Assert.NotNull(template);
            Assert.Equal(FluentResources.InputActiveBackground.Resolve(editor), editor.Background);
            var lightForeground = editor.Foreground;
            Assert.Single(editor.Descendants().OfType<TextPresenter>());
            Assert.Equal(12, editor.HorizontalPadding); Assert.Equal(6, editor.VerticalPadding);
            if (editor is NumericUpDown number) Assert.Equal(20, number.SpinnerWidth);
            editor.SetThemeDefaults(FluentTheme.Dark); host.UpdateLayout();
            Assert.Same(template, editor.TemplateRoot);
            Assert.Equal(FluentResources.InputActiveBackground.Resolve(editor), editor.Background);
            Assert.Equal(FluentResources.Foreground.Resolve(editor), editor.Foreground);
            Assert.NotEqual(lightForeground, editor.Foreground);
            Assert.Equal("keep", editor.Text); Assert.Equal("ee", editor.SelectedText);
            Assert.Same(editor, host.Focus.Focused);
        }
    }

    [Fact]
    public void PresenterRepaintsWhenOnlyCaretSelectionOrCompositionChanges()
    {
        using var editor = new TextBox { Text = "abcd", Width = 200, Height = 40 };
        editor.Template = new FuncControlTemplate<TextBox>(owner => new(new TextPresenter { Editor = owner }));
        using var host = new HeadlessRoot(editor); host.UpdateLayout(); host.Focus.Focus(editor);
        var presenter = Assert.IsType<TextPresenter>(editor.TemplateRoot);
        var previous = presenter.RenderVersion;
        editor.Select(1, 2); Assert.True(presenter.RenderVersion > previous);
        previous = presenter.RenderVersion;
        editor.Advance(TimeSpan.FromSeconds(.6)); Assert.True(presenter.RenderVersion > previous);
        previous = presenter.RenderVersion;
        host.Input.TextComposition("compose", CompositionStage.Updated);
        Assert.True(presenter.RenderVersion > previous);
    }

    private sealed class Drawing : IElementDrawingContext
    {
        public List<string> Texts { get; } = [];
        public int Fills { get; private set; }
        public UiColor Foreground => new(0, 0, 0);
        public UiColor Background => UiColor.Transparent;
        public UiColor Border => UiColor.Transparent;
        public UiColor Accent => UiColor.Transparent;
        public UiColor OnAccent => UiColor.Transparent;
        public void FillRectangle(Rect rect, UiColor color, double radius = 0) => Fills++;
        public void StrokeRectangle(Rect rect, UiColor color, double width = 1, double radius = 0) { }
        public void Ellipse(Rect rect, UiColor fill, UiColor stroke, double strokeWidth = 0) { }
        public void Line(Point start, Point end, UiColor color, double width = 1) { }
        public void Path(string data, UiColor fill, UiColor stroke, double strokeWidth = 1) { }
        public void Text(string text, Point origin, double fontSize, UiColor color) => Texts.Add(text);
        public void Image(IRasterImage image, Rect destination, double opacity = 1) { }
        public void PushClip(Rect rect) { }
        public void PopClip() { }
    }
}
