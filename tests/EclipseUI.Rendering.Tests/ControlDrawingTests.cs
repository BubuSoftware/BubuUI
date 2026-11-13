using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Rendering.Tests;

public class ControlDrawingTests
{
    private sealed class CustomElement : Element
    {
        public static readonly StyledProperty<UiColor> FillProperty = StyledProperty<UiColor>.Register<CustomElement>("Fill", new(200, 40, 30), new() { Invalidate = InvalidateKind.Render });
        public UiColor Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
        protected override Size MeasureCore(Size available) => new(30, 20);
        protected override void ArrangeCore(Rect final) { }
        public override void Render(IElementDrawingContext c) => c.FillRectangle(new(0, 0, FinalRect.Width, FinalRect.Height), Fill);
    }
    private sealed class RecordingText : ITextPainter
    {
        public readonly List<string> Texts = [];
        public readonly List<Color> Colors = [];
        public TextLineMetrics Measure(string text, double fontSize) => new(fontSize, fontSize * .4, text.Length * fontSize * .6);
        public void Paint(DisplayList list, string text, double fontSize, Point topLeft, Color color) { Texts.Add(text); Colors.Add(color); list.Add(new FillRect(new(topLeft, new Size(Math.Max(1, text.Length), 2)), color)); }
    }
    private static void Layout(Element element, Size? size = null)
    {
        using var host = new HeadlessRoot(element) { AvailableSize = size ?? new(300, 200) }; host.UpdateLayout();
    }
    [Fact]
    public void CustomDrawingUsesLocalCoordinatesRetainsCommandsAndTracksDamage()
    {
        var root = new Canvas { Width = 300, Height = 200 }; var custom = new CustomElement(); Canvas.SetLeft(custom, 40); Canvas.SetTop(custom, 50); root.Children.Add(custom); Layout(root);
        var painter = new ElementPainter(); var first = painter.BuildFrame(root, new(300, 200));
        var fill = first.List.Commands.OfType<FillRoundedRect>().Single(c => ((SolidColorBrush)c.Brush).Color.R == 200); Assert.Equal(new Rect(40, 50, 30, 20), fill.Rect);
        var second = painter.BuildFrame(root, new(300, 200)); Assert.Equal(0, painter.LastBuildStats.Repainted); Assert.True(second.Damage.IsEmpty);
        custom.Fill = new(30, 80, 200); var third = painter.BuildFrame(root, new(300, 200)); Assert.False(third.Damage.IsEmpty); Assert.Equal(1, painter.LastBuildStats.Repainted);
    }
    [Fact]
    public void PasswordPaintNeverPassesSecretToTextPainter()
    {
        var editor = new PasswordBox { Text = "secret😀", Width = 200 }; Layout(editor); var text = new RecordingText();
        var list = new ElementPainter(textPainter: text).Build(editor, new(300, 200)); Assert.Empty(list.Validate());
        Assert.DoesNotContain(text.Texts, s => s.Contains("secret")); Assert.Contains(text.Texts, s => s.Contains('●'));
    }
    [Fact]
    public void ShapesImageAndEditorEmitBalancedDisplayLists()
    {
        var panel = new WrapPanel { Gap = 5 }; panel.Children.Add(new EclipseUI.Elements.Rectangle { Width = 30, Height = 20 }); panel.Children.Add(new Ellipse { Width = 30, Height = 20, Stroke = new(255, 255, 255) });
        panel.Children.Add(new Line()); panel.Children.Add(new EclipseUI.Elements.Path { Data = "M0 0 L20 0 Q25 10 20 20 C15 25 5 25 0 20 Z", Width = 30, Height = 30 });
        panel.Children.Add(new Image { Source = ImageHandle.Solid(10, 20, Color.White), Width = 30, Height = 30 }); panel.Children.Add(new TextBox { Text = "test" });
        Layout(panel); var list = new ElementPainter(textPainter: new RecordingText()).Build(panel, new(300, 200)); Assert.Empty(list.Validate()); Assert.Single(list.Commands.OfType<DrawImage>()); Assert.NotEmpty(list.Commands.OfType<FillPath>());
    }
    [Theory]
    [InlineData("M0 0 A10 10 0 0 0 20 20")]
    [InlineData("M0")]
    [InlineData("M0 0 L")]
    public void InvalidPathFailsWithUsefulFormatError(string data)
    {
        var path = new EclipseUI.Elements.Path { Data = data }; Layout(path);
        Assert.Throws<FormatException>(() => new ElementPainter().Build(path, new(100, 100)));
    }
    [Fact]
    public void PopupPaintsAfterAncestorClipAndHiddenContentProducesNoCommands()
    {
        var root = new Grid { Width = 300, Height = 200 }; var clipped = new StackPanel { ClipToBounds = true, Width = 50, Height = 30 };
        var body = new Border { Background = new(240, 30, 20), Width = 120, Height = 50 }; var popup = new Popup { Content = body, IsOpen = true, PlacementTarget = clipped }; clipped.Children.Add(popup); root.Children.Add(clipped); Layout(root);
        var painter = new ElementPainter(); var list = painter.Build(root, new(300, 200)); Assert.Empty(list.Validate());
        var index = list.Commands.FindIndex(c => c is FillRoundedRect f && ((SolidColorBrush)f.Brush).Color.R == 240);
        Assert.True(index > list.Commands.FindIndex(c => c is PopClip));
        popup.Close(); Layout(root); var hidden = painter.Build(root, new(300, 200)); Assert.DoesNotContain(hidden.Commands, c => c is FillRoundedRect f && ((SolidColorBrush)f.Brush).Color.R == 240);
    }
    [Fact]
    public void NewValueControlsPaintUsingBothThemesAndRespectDisabledForeground()
    {
        var panel = new WrapPanel(); Element[] controls = [new ToggleButton { Label = "toggle", Checked = true }, new RadioButton { Label = "radio", Checked = true }, new ToggleSwitch { Label = "switch" }, new Slider(), new ProgressBar { Value = 50 }, new NumericUpDown(), new Calendar()];
        foreach (var control in controls) panel.Children.Add(control); Layout(panel, new(1000, 500));
        foreach (var theme in new ElementTheme[] { new(), new LightTheme() })
        {
            var recorder = new RecordingText();
            var painter = new ElementPainter(theme, recorder);
            var list = painter.Build(panel, new(1000, 500)); Assert.Empty(list.Validate()); Assert.NotEmpty(list.Commands.OfType<FillEllipse>());
            panel.IsEnabled = false;
            Assert.Empty(painter.Build(panel, new(1000, 500)).Validate()); Assert.Contains(theme.DisabledForeground, recorder.Colors);
            panel.IsEnabled = true;
        }
    }
}
