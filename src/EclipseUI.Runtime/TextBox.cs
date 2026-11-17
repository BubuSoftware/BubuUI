using System.Globalization;
using System.Text;

namespace EclipseUI.Elements;

[UiContent(UiContentKind.Text, Target = nameof(Text))]
public class TextBox : Element, IFrameAnimation
{
    public static readonly StyledProperty<double> HorizontalPaddingProperty = StyledProperty<double>.Register<TextBox>("HorizontalPadding", 0, new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<double> VerticalPaddingProperty = StyledProperty<double>.Register<TextBox>("VerticalPadding", 0, new() { Invalidate = InvalidateKind.Measure });
    public double HorizontalPadding { get => GetValue(HorizontalPaddingProperty); set => SetValue(HorizontalPaddingProperty, Math.Max(0, value)); }
    public double VerticalPadding { get => GetValue(VerticalPaddingProperty); set => SetValue(VerticalPaddingProperty, Math.Max(0, value)); }
    public static readonly StyledProperty<string> TextProperty = StyledProperty<string>.Register<TextBox>("Text", "", new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<string> PlaceholderProperty = StyledProperty<string>.Register<TextBox>("Placeholder", "", new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<bool> IsReadOnlyProperty = StyledProperty<bool>.Register<TextBox>("IsReadOnly", false, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<bool> AcceptsReturnProperty = StyledProperty<bool>.Register<TextBox>("AcceptsReturn", false, new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<int> MaxLengthProperty = StyledProperty<int>.Register<TextBox>("MaxLength", 0, new() { Invalidate = InvalidateKind.Render });
    public static readonly RoutedEvent TextChangedEvent = new("TextChanged");
    public static readonly RoutedEvent SelectionChangedEvent = new("SelectionChanged");
    private readonly Stack<Edit> _undo = new(), _redo = new();
    private record struct Edit(string Text, int Anchor, int Caret);
    private int _anchor, _caret;
    private bool _editing, _dragging;
    private double _scrollX, _scrollY;
    private string _composition = "";
    private double _blinkElapsed;
    private bool _caretVisible = true;
    private TextPresenter? _textPresenter;
    private Rect? _presentationBounds;
    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value ?? ""); }
    public string Placeholder { get => GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value ?? ""); }
    public bool IsReadOnly { get => GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
    public bool AcceptsReturn { get => GetValue(AcceptsReturnProperty); set => SetValue(AcceptsReturnProperty, value); }
    public int MaxLength { get => GetValue(MaxLengthProperty); set => SetValue(MaxLengthProperty, Math.Max(0, value)); }
    public int CaretIndex { get => _caret; set => Select(value, 0); }
    public int SelectionStart { get => Math.Min(_anchor, _caret); set => Select(value, SelectionLength); }
    public int SelectionLength { get => Math.Abs(_anchor - _caret); set => Select(SelectionStart, value); }
    public string SelectedText => Text.Substring(SelectionStart, SelectionLength);
    public string CompositionText => _composition;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    /// <summary>Text and geometry consumed by an explicitly installed text presenter.</summary>
    public string PresentedText => DisplayText;
    public string PresentedComposition => DisplayComposition;
    public double TextFontSize => FontSize;
    public double TextLineHeight => LineHeight;
    public Point TextOrigin => new(PresentationBounds.X + HorizontalPadding - _scrollX, TextTop - _scrollY);
    public Rect TextViewportBounds => TextViewport;
    public bool IsCaretVisible => _caretVisible;
    public event Action? PresentationChanged;
    public override bool IsFocusable => true;
    public override bool IsTextInputClient => true;
    internal override void CancelPointerInteraction() => _dragging = false;
    protected override FocusVisualMode DefaultFocusVisualMode => FocusVisualMode.Control;
    public override FocusVisualGeometry GetFocusVisualGeometry() => new(this, new(Point.Zero, FinalRect.Size), CornerRadius);
    protected virtual bool CanCopy => true;
    protected virtual string DisplayText => Text;
    protected virtual string DisplayComposition => _composition;
    protected double FontSize => GetValue(TextBlock.FontSizeProperty);
    public TextBox()
    {
        AddHandler(TextInputEvent, e => { if (e is TextInputEventArgs t && !IsReadOnly) { if (t.Text.Any(c => !char.IsControl(c) || AcceptsReturn && c is '\r' or '\n')) InsertText(t.Text); e.Handled = true; } });
        AddHandler(TextCompositionEvent, e =>
        {
            if (e is not TextCompositionEventArgs c || IsReadOnly) return;
            _composition = c.Stage == CompositionStage.Updated ? c.Text : "";
            if (c.Stage == CompositionStage.Committed) InsertText(c.Text);
            InvalidateMeasure(); NotifyPresentationChanged(); e.Handled = true;
        });
        AddHandler(KeyDownEvent, HandleKey);
        AddHandler(PointerPressedEvent, e =>
        {
            if (e is not PointerEventArgs p || p.Button != PointerButton.Left) return;
            HandlePointerPressed(p); e.Handled = true;
        });
        AddHandler(PointerMovedEvent, e => { if (_dragging && e is PointerEventArgs p) { MoveCaret(IndexAt(PointFromRoot(p.Position)), true); e.Handled = true; } });
        AddHandler(PointerReleasedEvent, _ => _dragging = false);
        AddHandler(GotFocusEvent, _ => { _blinkElapsed = 0; _caretVisible = true; NotifyPresentationChanged(); });
        AddHandler(LostFocusEvent, _ => { _composition = ""; _dragging = false; InvalidateMeasure(); NotifyPresentationChanged(); });
        PropertyChanged += (_, property) =>
        {
            if (property == TextProperty)
            {
                _anchor = Boundary(Math.Min(_anchor, Text.Length)); _caret = Boundary(Math.Min(_caret, Text.Length));
                if (!_editing) { _undo.Clear(); _redo.Clear(); }
                NotifyPresentationChanged(); RaiseEvent(new(TextChangedEvent));
            }
            else if (property == PlaceholderProperty || property == TextBlock.FontSizeProperty ||
                     property == HorizontalPaddingProperty || property == VerticalPaddingProperty ||
                     property == AcceptsReturnProperty || property == PasswordBox.PasswordCharProperty)
                NotifyPresentationChanged();
        };
    }
    protected virtual void HandlePointerPressed(PointerEventArgs p)
    {
        MoveCaret(IndexAt(PointFromRoot(p.Position)), p.Modifiers.HasFlag(KeyModifiers.Shift)); _dragging = true;
    }
    private int Boundary(int index)
    {
        index = Math.Clamp(index, 0, Text.Length);
        if (index == Text.Length) return index;
        var boundaries = StringInfo.ParseCombiningCharacters(Text);
        foreach (var boundary in boundaries.Reverse()) if (boundary <= index) return boundary;
        return 0;
    }
    private int Previous(int index) => index <= 0 ? 0 : Boundary(index - 1);
    private int Next(int index)
    {
        foreach (var boundary in StringInfo.ParseCombiningCharacters(Text)) if (boundary > index) return boundary;
        return Text.Length;
    }
    private int LineStart => Text[.._caret].LastIndexOf('\n') + 1;
    private int LineEnd
    {
        get { var end = Text.IndexOf('\n', _caret); return end >= 0 ? end : Text.Length; }
    }
    private int WordKind(int index)
    {
        if (!Rune.TryGetRuneAt(Text, index, out var rune)) rune = Rune.ReplacementChar;
        if (Rune.IsWhiteSpace(rune)) return 0;
        return Rune.IsLetterOrDigit(rune) || Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.EnclosingMark or UnicodeCategory.ConnectorPunctuation ? 1 : 2;
    }
    private int PreviousWord(int index)
    {
        while (index > 0 && WordKind(Previous(index)) == 0) index = Previous(index);
        if (index == 0) return 0;
        var kind = WordKind(Previous(index));
        while (index > 0 && WordKind(Previous(index)) == kind) index = Previous(index);
        return index;
    }
    private int NextWord(int index)
    {
        while (index < Text.Length && WordKind(index) == 0) index = Next(index);
        if (index == Text.Length) return index;
        var kind = WordKind(index);
        while (index < Text.Length && WordKind(index) == kind) index = Next(index);
        return index;
    }
    public void Select(int start, int length)
    {
        var end = Math.Clamp((long)start + Math.Max(0, length), 0, Text.Length);
        _anchor = Boundary(start); _caret = end == Text.Length ? Text.Length : Boundary((int)end);
        ChangedSelection();
    }
    public void SelectAll() => Select(0, Text.Length);
    private void MoveCaret(int index, bool extend)
    {
        _caret = index >= Text.Length ? Text.Length : Boundary(index);
        if (!extend) _anchor = _caret;
        ChangedSelection();
    }
    private void ChangedSelection()
    {
        _blinkElapsed = 0; _caretVisible = true;
        EnsureCaretVisible(); NotifyPresentationChanged(); RaiseEvent(new(SelectionChangedEvent));
    }
    public void InsertText(string value)
    {
        if (IsReadOnly) return;
        value = value.Replace("\r\n", "\n").Replace('\r', '\n');
        value = string.Concat(value.Where(c => c == '\n' ? AcceptsReturn : !char.IsControl(c)));
        if (MaxLength > 0)
        {
            var capacity = Math.Max(0, MaxLength - (Text.Length - SelectionLength));
            if (value.Length > capacity)
            {
                var cut = StringInfo.ParseCombiningCharacters(value).Where(i => i <= capacity).DefaultIfEmpty(0).Max();
                value = value[..cut];
            }
        }
        if (value.Length == 0 && SelectionLength == 0) return;
        SaveUndo();
        var start = SelectionStart;
        SetEdited(Text.Remove(start, SelectionLength).Insert(start, value), start + value.Length, start + value.Length);
    }
    private void SaveUndo() { if (_undo.Count >= 100) { var keep = _undo.Take(99).Reverse().ToArray(); _undo.Clear(); foreach (var e in keep) _undo.Push(e); } _undo.Push(new(Text, _anchor, _caret)); _redo.Clear(); }
    private void SetEdited(string text, int anchor, int caret)
    {
        _editing = true;
        try { Text = text; } finally { _editing = false; }
        _anchor = Math.Clamp(anchor, 0, Text.Length); _caret = Math.Clamp(caret, 0, Text.Length); ChangedSelection();
    }
    public void Undo()
    {
        if (IsReadOnly || !_undo.TryPop(out var edit)) return;
        _redo.Push(new(Text, _anchor, _caret)); SetEdited(edit.Text, edit.Anchor, edit.Caret);
    }
    public void Redo()
    {
        if (IsReadOnly || !_redo.TryPop(out var edit)) return;
        _undo.Push(new(Text, _anchor, _caret)); SetEdited(edit.Text, edit.Anchor, edit.Caret);
    }
    public void Copy() { if (CanCopy && SelectionLength > 0) Clipboard.Current.SetText(SelectedText); }
    public void Cut() { if (!CanCopy || IsReadOnly || SelectionLength == 0) return; Copy(); InsertText(""); }
    public void Paste() { if (!IsReadOnly) InsertText(Clipboard.Current.GetText()); }
    protected virtual void HandleKey(RoutedEventArgs args)
    {
        if (args is not KeyEventArgs e) return;
        var shortcut = InputGestures.HasCommand(e.Modifiers);
        var shift = e.Modifiers.HasFlag(KeyModifiers.Shift);
        var command = OperatingSystem.IsMacOS() && e.Modifiers.HasFlag(KeyModifiers.Meta);
        var word = OperatingSystem.IsMacOS() && e.Modifiers.HasFlag(KeyModifiers.Alt);
        if (shortcut)
        {
            switch (e.Key)
            {
                case Key.A: SelectAll(); break;
                case Key.C: Copy(); break;
                case Key.X: Cut(); break;
                case Key.V: Paste(); break;
                case Key.Z: if (shift) Redo(); else Undo(); break;
                case Key.Y: Redo(); break;
                default: goto Navigation;
            }
            e.Handled = true; return;
        }
    Navigation:
        switch (e.Key)
        {
            case Key.Left: MoveCaret(command ? LineStart : word ? PreviousWord(_caret) : !shift && SelectionLength > 0 ? SelectionStart : Previous(_caret), shift); break;
            case Key.Right: MoveCaret(command ? LineEnd : word ? NextWord(_caret) : !shift && SelectionLength > 0 ? SelectionStart + SelectionLength : Next(_caret), shift); break;
            case Key.Home: MoveCaret(shortcut ? 0 : LineStart, shift); break;
            case Key.End: MoveCaret(shortcut ? Text.Length : LineEnd, shift); break;
            case Key.Up:
            case Key.Down:
                if (command) { MoveCaret(e.Key == Key.Up ? 0 : Text.Length, shift); break; }
                var caret = CaretPosition(); MoveCaret(IndexAt(new(caret.X + PresentationBounds.X + HorizontalPadding - _scrollX, caret.Y + TextTop - _scrollY + (e.Key == Key.Up ? -1 : 1) * LineHeight)), shift); break;
            case Key.Backspace:
                if (!IsReadOnly) { if (SelectionLength == 0 && _caret > 0) { var previous = command ? LineStart : word ? PreviousWord(_caret) : Previous(_caret); Select(previous, _caret - previous); } if (SelectionLength > 0) InsertText(""); }
                break;
            case Key.Delete:
                if (!IsReadOnly) { if (SelectionLength == 0 && _caret < Text.Length) Select(_caret, (word ? NextWord(_caret) : Next(_caret)) - _caret); if (SelectionLength > 0) InsertText(""); }
                break;
            case Key.Enter: if (!AcceptsReturn) return; InsertText("\n"); break;
            default: return;
        }
        e.Handled = true;
    }
    private double LineHeight
    {
        get
        {
            var height = TextMeasurer.Current.Measure("Mg", FontSize).Height;
            foreach (var line in DisplayText.Split('\n')) height = Math.Max(height, TextMeasurer.Current.Measure(line, FontSize).Height);
            if (_composition.Length > 0) height = Math.Max(height, TextMeasurer.Current.Measure(DisplayComposition, FontSize).Height);
            if (Text.Length == 0) height = Math.Max(height, TextMeasurer.Current.Measure(Placeholder, FontSize).Height);
            return height;
        }
    }
    private Point CaretPosition()
    {
        var prefix = DisplayText[..Math.Min(_caret, DisplayText.Length)];
        var lines = prefix.Split('\n');
        return new(TextMeasurer.Current.Measure(lines[^1], FontSize).Width, (lines.Length - 1) * LineHeight);
    }
    protected Rect PresentationBounds => _presentationBounds ?? new Rect(Point.Zero, FinalRect.Size);
    private double TextTop => PresentationBounds.Y + (AcceptsReturn ? VerticalPadding : Math.Max(VerticalPadding, (PresentationBounds.Height - LineHeight) / 2));
    public Rect CaretRect { get { var p = CaretPosition(); return new(p.X + PresentationBounds.X + HorizontalPadding - _scrollX, p.Y + TextTop - _scrollY, 1, LineHeight); } }
    protected virtual Rect TextViewport => new(PresentationBounds.X + HorizontalPadding, PresentationBounds.Y + VerticalPadding,
        Math.Max(0, PresentationBounds.Width - HorizontalPadding * 2), Math.Max(0, PresentationBounds.Height - VerticalPadding * 2));
    private void EnsureCaretVisible()
    {
        var p = CaretPosition();
        var w = Math.Max(1, TextViewport.Width - CaretRect.Width); var h = Math.Max(LineHeight, TextViewport.Height);
        _scrollX = Math.Max(0, Math.Clamp(_scrollX, p.X - w, p.X)); _scrollY = Math.Max(0, Math.Clamp(_scrollY, p.Y - h + LineHeight, p.Y));
    }
    private int IndexAt(Point p)
    {
        var lines = DisplayText.Split('\n');
        var line = Math.Clamp((int)Math.Floor((p.Y - TextTop + _scrollY) / LineHeight), 0, lines.Length - 1);
        var start = lines.Take(line).Sum(l => l.Length + 1); var text = lines[line];
        var x = Math.Max(0, p.X - PresentationBounds.X - HorizontalPadding + _scrollX);
        var breaks = StringInfo.ParseCombiningCharacters(text).Append(text.Length).ToArray();
        for (var i = 1; i < breaks.Length; i++)
        {
            var left = TextMeasurer.Current.Measure(text[..breaks[i - 1]], FontSize).Width;
            var right = TextMeasurer.Current.Measure(text[..breaks[i]], FontSize).Width;
            if (x < (left + right) / 2) return start + breaks[i - 1];
        }
        return Math.Min(Text.Length, start + text.Length);
    }
    public virtual Size MeasureTextContent(int minimumLines = 1)
    {
        var lines = DisplayText.Split('\n');
        var textWidth = lines.Max(l => TextMeasurer.Current.Measure(l, FontSize).Width);
        if (Text.Length == 0) textWidth = Math.Max(textWidth, TextMeasurer.Current.Measure(Placeholder, FontSize).Width);
        return new(textWidth + HorizontalPadding * 2,
            Math.Max(Math.Max(1, minimumLines), lines.Length) * LineHeight + VerticalPadding * 2);
    }
    protected internal override Size MeasureCore(Size available) => MeasureTextContent();
    protected internal override void ArrangeCore(Rect final) => UpdateTextViewport();
    public void UpdateTextViewport()
    {
        var previous = (_scrollX, _scrollY);
        EnsureCaretVisible();
        if (previous != (_scrollX, _scrollY)) NotifyPresentationChanged();
    }
    internal void SetTextPresenter(TextPresenter presenter, Rect bounds)
    {
        _textPresenter = presenter;
        _presentationBounds = bounds;
        UpdateTextViewport();
    }
    internal void RemoveTextPresenter(TextPresenter presenter)
    {
        if (!ReferenceEquals(_textPresenter, presenter)) return;
        _textPresenter = null;
        _presentationBounds = null;
        UpdateTextViewport();
    }
    private void NotifyPresentationChanged()
    {
        InvalidateRender();
        PresentationChanged?.Invoke();
    }
    public void Advance(TimeSpan elapsed)
    {
        if (!IsFocused) return;
        _blinkElapsed = (_blinkElapsed + elapsed.TotalSeconds) % 1.1;
        var visible = _blinkElapsed < .55;
        if (visible != _caretVisible) { _caretVisible = visible; NotifyPresentationChanged(); }
    }
}

public class PasswordBox : TextBox
{
    public static readonly StyledProperty<char> PasswordCharProperty = StyledProperty<char>.Register<PasswordBox>("PasswordChar", '●', new() { Invalidate = InvalidateKind.Measure });
    public char PasswordChar { get => GetValue(PasswordCharProperty); set => SetValue(PasswordCharProperty, value); }
    protected override bool CanCopy => false;
    protected override string DisplayComposition => new(PasswordChar, CompositionText.Length);
    protected override string DisplayText => new(PasswordChar, Text.Length);
}
