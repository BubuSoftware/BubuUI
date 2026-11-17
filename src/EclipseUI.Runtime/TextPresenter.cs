namespace EclipseUI.Elements;

/// <summary>Displays an editor's text, selection, caret and composition using explicit brushes.
/// It supplies no control chrome and forwards input ownership to the editor.</summary>
public class TextPresenter : Element
{
    private TextBox? _editor;
    private Point _origin;
    public TextBox? Editor
    {
        get => _editor;
        set
        {
            if (ReferenceEquals(_editor, value)) return;
            if (_editor is not null)
            {
                _editor.PresentationChanged -= OnPresentationChanged;
                _editor.RemoveTextPresenter(this);
            }
            _editor = value;
            if (value is not null) value.PresentationChanged += OnPresentationChanged;
            InvalidateMeasure(); InvalidateRender();
        }
    }
    public static readonly StyledProperty<UiColor> PlaceholderForegroundProperty = StyledProperty<UiColor>.Register<TextPresenter>(nameof(PlaceholderForeground), UiColor.Transparent, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<UiColor> SelectionBackgroundProperty = StyledProperty<UiColor>.Register<TextPresenter>(nameof(SelectionBackground), UiColor.Transparent, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<UiColor> CaretBrushProperty = StyledProperty<UiColor>.Register<TextPresenter>(nameof(CaretBrush), UiColor.Transparent, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<UiColor> CompositionUnderlineBrushProperty = StyledProperty<UiColor>.Register<TextPresenter>(nameof(CompositionUnderlineBrush), UiColor.Transparent, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<int> MinimumLinesProperty = StyledProperty<int>.Register<TextPresenter>(nameof(MinimumLines), 1, new() { Invalidate = InvalidateKind.Measure });
    public UiColor PlaceholderForeground { get => GetValue(PlaceholderForegroundProperty); set => SetValue(PlaceholderForegroundProperty, value); }
    public UiColor SelectionBackground { get => GetValue(SelectionBackgroundProperty); set => SetValue(SelectionBackgroundProperty, value); }
    public UiColor CaretBrush { get => GetValue(CaretBrushProperty); set => SetValue(CaretBrushProperty, value); }
    public UiColor CompositionUnderlineBrush { get => GetValue(CompositionUnderlineBrushProperty); set => SetValue(CompositionUnderlineBrushProperty, value); }
    public int MinimumLines { get => GetValue(MinimumLinesProperty); set => SetValue(MinimumLinesProperty, Math.Max(1, value)); }
    public TextPresenter() => IsHitTestVisible = false;
    private void OnPresentationChanged() { InvalidateMeasure(); InvalidateRender(); }
    protected internal override Size MeasureCore(Size available) => Editor?.MeasureTextContent(MinimumLines) ?? Size.Empty;
    protected internal override void ArrangeCore(Rect final)
    {
        if (Editor is not { } editor) return;
        var own = PointFromRoot(Point.Zero); var owner = editor.PointFromRoot(Point.Zero);
        _origin = new(owner.X - own.X, owner.Y - own.Y);
        editor.SetTextPresenter(this, new(_origin, FinalRect.Size));
    }
    private Point Local(Point point) => new(point.X - _origin.X, point.Y - _origin.Y);
    private Rect Local(Rect rect) => new(Local(rect.Location), rect.Size);
    public override void Render(IElementDrawingContext context)
    {
        if (Editor is not { } editor) return;
        var origin = Local(editor.TextOrigin);
        context.PushClip(Local(editor.TextViewportBounds));
        var lines = editor.PresentedText.Split('\n'); var offset = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i]; var y = origin.Y + i * editor.TextLineHeight;
            var start = Math.Clamp(editor.SelectionStart - offset, 0, line.Length);
            var end = Math.Clamp(editor.SelectionStart + editor.SelectionLength - offset, 0, line.Length);
            if (end > start)
            {
                var x = TextMeasurer.Current.Measure(line[..start], editor.TextFontSize).Width;
                var width = TextMeasurer.Current.Measure(line[start..end], editor.TextFontSize).Width;
                context.FillRectangle(new(origin.X + x, y, width, editor.TextLineHeight), SelectionBackground);
            }
            context.Text(line, new(origin.X, y), editor.TextFontSize, Foreground);
            offset += line.Length + 1;
        }
        if (editor.Text.Length == 0 && editor.CompositionText.Length == 0)
            context.Text(editor.Placeholder, origin, editor.TextFontSize, PlaceholderForeground);
        if (editor.IsFocused)
        {
            var caret = Local(editor.CaretRect);
            if (editor.IsCaretVisible) context.FillRectangle(caret, CaretBrush);
            if (editor.CompositionText.Length > 0)
            {
                context.Text(editor.PresentedComposition, caret.Location, editor.TextFontSize, Foreground);
                var width = TextMeasurer.Current.Measure(editor.PresentedComposition, editor.TextFontSize).Width;
                context.Line(new(caret.X, caret.Bottom), new(caret.X + width, caret.Bottom), CompositionUnderlineBrush);
            }
        }
        context.PopClip();
    }
    protected override void OnDispose()
    {
        Editor = null;
        base.OnDispose();
    }
}
