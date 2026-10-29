using EclipseUI.Elements;

namespace EclipseUI.Rendering;

/// <summary>
/// 元素树 → <see cref="DisplayList"/> 的场景图遍历（G2-10）。
///
/// 约定：
/// <list type="bullet">
/// <item>元素的 <c>FinalRect</c> 位于父元素局部坐标系；遍历时累加绝对偏移，指令直接用根坐标。</item>
/// <item>块协调器（<c>@if</c> / <c>@foreach</c>）通过 <see cref="Element.IsVisualTransparent"/> 跳过自身背景。</item>
/// <item>文本走 <see cref="ITextPainter"/>，布局与绘制共用同一 shaping 结果。</item>
/// <item>根元素由宿主 arrange 到整个可视区；根自身不画卡片，只画主题 surface。</item>
/// </list>
///
/// G2 仍为全量构建 display list；<see cref="BuildFrame"/> 额外产出元素级脏区，
/// 宿主据此跳过空闲帧的上屏。真正按脏区裁剪绘制需要 retained layer（后续切片）。
/// </summary>
public sealed class ElementPainter
{
    private readonly ElementTheme _theme;
    private readonly ITextPainter? _text;
    private readonly DamageTracker _damageTracker = new();

    private DisplayList _list = null!;
    private Element? _focused;

    public ElementPainter(ElementTheme? theme = null, ITextPainter? textPainter = null)
    {
        _theme = theme ?? new ElementTheme();
        _text = textPainter;
    }

    public ElementTheme Theme => _theme;

    /// <summary>
    /// 构建一帧（不做脏区跟踪）。会清空脏区历史：下一次 <see cref="BuildFrame"/> 按整帧处理。
    /// 坐标使用设备无关单位；<paramref name="scale"/> 为 DPI 缩放，
    /// 由根变换一次性放大到设备像素。返回的 list 尺寸为逻辑尺寸。
    /// </summary>
    public DisplayList Build(Element root, Size size, double scale = 1, Element? focused = null)
    {
        _damageTracker.Reset();
        return BuildCore(root, size, scale, focused, tracker: null);
    }

    /// <summary>
    /// 构建一帧并返回相对上一帧的脏区。首帧 / 尺寸 / DPI 变化为整帧；
    /// 脏区为空时宿主可跳过上屏（画面保持不变）。
    /// </summary>
    public PaintedFrame BuildFrame(Element root, Size size, double scale = 1, Element? focused = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        var damage = _damageTracker.Begin(size, scale, focused);
        var list = BuildCore(root, size, scale, focused, _damageTracker);
        _damageTracker.End();
        return new PaintedFrame(list, damage);
    }

    /// <summary>丢弃脏区历史，下一帧强制整帧重绘（窗口遮挡恢复等场景）。</summary>
    public void ResetDamage() => _damageTracker.Reset();

    private DisplayList BuildCore(Element root, Size size, double scale, Element? focused, DamageTracker? tracker)
    {
        ArgumentNullException.ThrowIfNull(root);

        var list = new DisplayList(size);
        _list = list;
        _focused = focused;

        var scaled = scale > 0 && Math.Abs(scale - 1) > 1e-6;
        if (scaled)
            list.Add(new PushTransform(Transform.Scale(scale)));

        list.Add(new FillRect(new Rect(0, 0, size.Width, size.Height), _theme.Surface));

        var clipSurface = size.Width > 0 && size.Height > 0;
        if (clipSurface)
            list.Add(new PushClip(new Rect(0, 0, size.Width, size.Height)));

        PaintElement(root, Point.Zero, isRoot: true, tracker);

        if (clipSurface) list.Add(new PopClip());
        if (scaled) list.Add(new PopTransform());

        return list;
    }

    private void PaintElement(Element element, Point offset, bool isRoot, DamageTracker? tracker)
    {
        var final = element.FinalRect;
        var rect = new Rect(offset.X + final.X, offset.Y + final.Y, final.Width, final.Height);

        // 焦点环画在元素矩形之外，脏区必须包含它。
        var visualBounds = ReferenceEquals(element, _focused) && element.IsFocusable
            ? Inflate(rect, _theme.FocusRingWidth)
            : rect;
        tracker?.Visit(element, visualBounds);

        switch (element)
        {
            case TextBlock block:
                PaintTextBlock(block, rect);
                break;
            case Button button:
                PaintButton(button, rect);
                break;
            case CheckBox checkBox:
                PaintCheckBox(checkBox, rect);
                break;
        }

        if (!isRoot && !element.IsVisualTransparent)
        {
            switch (element)
            {
                case ListBoxItem:
                    PaintCard(rect, _theme.ItemBackground, _theme.ItemBorder, _theme.ItemRadius);
                    break;
                case StackPanel:
                    PaintCard(rect, _theme.PanelBackground, _theme.PanelBorder, _theme.PanelRadius);
                    break;
            }
        }

        if (ReferenceEquals(element, _focused) && element.IsFocusable)
            PaintFocusRing(element, rect);

        var clip = element is Panel { ClipToBounds: true } && rect.Width > 0 && rect.Height > 0;
        if (clip) _list.Add(new PushClip(rect));

        // G2 的可视子节点全部来自 Panel（TextBlock 的 run 也是 Panel.Children 的一部分）。
        if (element is Panel panel)
        {
            foreach (var child in panel.Children)
                PaintElement(child, rect.Location, isRoot: false, tracker);
        }

        if (clip) _list.Add(new PopClip());
    }

    private void PaintTextBlock(TextBlock block, Rect rect)
    {
        foreach (var run in block.Runs)
        {
            var text = run.Text;
            if (string.IsNullOrEmpty(text)) continue;

            var fontSize = run.GetValue(TextBlock.FontSizeProperty);
            var color = fontSize < _theme.MutedFontSizeBelow ? _theme.MutedForeground : _theme.Foreground;
            var origin = new Point(rect.X + run.FinalRect.X, rect.Y + run.FinalRect.Y);
            _text?.Paint(_list, text, fontSize, origin, color);
        }
    }

    private void PaintButton(Button button, Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;

        _list.Add(new FillRoundedRect(
            rect,
            _theme.ButtonRadius,
            button.IsPressed ? _theme.AccentPressed : _theme.Accent));

        var label = button.Label;
        if (string.IsNullOrEmpty(label) || _text is null) return;

        var fontSize = button.GetValue(TextBlock.FontSizeProperty);
        var metrics = _text.Measure(label, fontSize);
        var origin = new Point(
            rect.X + (rect.Width - metrics.Width) / 2,
            rect.Y + (rect.Height - metrics.Height) / 2);
        _text.Paint(_list, label, fontSize, origin, _theme.OnAccent);
    }

    private void PaintCheckBox(CheckBox checkBox, Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var fontSize = checkBox.GetValue(TextBlock.FontSizeProperty);
        var boxSize = fontSize * 1.1;
        var box = new Rect(rect.X, rect.Y + (rect.Height - boxSize) / 2, boxSize, boxSize);

        if (checkBox.Checked)
        {
            _list.Add(new FillRoundedRect(box, _theme.CheckBoxRadius,
                checkBox.IsPressed ? _theme.AccentPressed : _theme.Accent));

            var strokeWidth = Math.Max(1.5, boxSize * 0.11);
            var check = new PathGeometryBuilder()
                .MoveTo(box.X + boxSize * 0.24, box.Y + boxSize * 0.52)
                .LineTo(box.X + boxSize * 0.43, box.Y + boxSize * 0.71)
                .LineTo(box.X + boxSize * 0.77, box.Y + boxSize * 0.31)
                .Build();
            _list.Add(new StrokePath(check, _theme.OnAccent, strokeWidth));
        }
        else
        {
            _list.Add(new FillRoundedRect(box, _theme.CheckBoxRadius,
                checkBox.IsPressed ? _theme.AccentPressed : _theme.ControlBackground));
            _list.Add(new StrokePath(
                PathGeometry.RoundedRectangle(Inset(box, _theme.BorderWidth / 2), _theme.CheckBoxRadius),
                _theme.ControlBorder,
                _theme.BorderWidth));
        }

        var label = checkBox.Label;
        if (string.IsNullOrEmpty(label) || _text is null) return;

        var metrics = _text.Measure(label, fontSize);
        var origin = new Point(
            rect.X + boxSize + 8,
            rect.Y + (rect.Height - metrics.Height) / 2);
        _text.Paint(_list, label, fontSize, origin, _theme.Foreground);
    }

    private void PaintCard(Rect rect, Color background, Color border, double radius)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;

        _list.Add(new FillRoundedRect(rect, radius, background));

        if (_theme.BorderWidth <= 0 || border.A == 0) return;
        _list.Add(new StrokePath(
            PathGeometry.RoundedRectangle(Inset(rect, _theme.BorderWidth / 2), radius),
            border,
            _theme.BorderWidth));
    }

    private void PaintFocusRing(Element element, Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var radius = element switch
        {
            Button => _theme.ButtonRadius,
            CheckBox => _theme.CheckBoxRadius,
            ListBoxItem => _theme.ItemRadius,
            _ => _theme.PanelRadius,
        };

        var ring = Inflate(rect, _theme.FocusRingWidth);
        _list.Add(new StrokePath(
            PathGeometry.RoundedRectangle(ring, radius + _theme.FocusRingWidth),
            _theme.FocusRing,
            _theme.FocusRingWidth));
    }

    private static Rect Inset(Rect rect, double amount) => new(
        rect.X + amount,
        rect.Y + amount,
        Math.Max(0, rect.Width - amount * 2),
        Math.Max(0, rect.Height - amount * 2));

    private static Rect Inflate(Rect rect, double amount) => new(
        rect.X - amount,
        rect.Y - amount,
        rect.Width + amount * 2,
        rect.Height + amount * 2);
}

/// <summary>一帧的绘制结果：display list + 相对上一帧的脏区。</summary>
public readonly record struct PaintedFrame(DisplayList List, FrameDamage Damage);
