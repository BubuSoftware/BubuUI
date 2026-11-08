using System.Runtime.CompilerServices;
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
/// G2 retained（D93）：每个元素的**自绘命令**（类型绘制 + 卡片 + 焦点环）按
/// (RenderVersion, 绝对位置, 尺寸, 焦点态) 缓存——树未变的元素直接复用命令段，
/// 不再逐条重新构造（帧间分配趋零）；失效契约与 <c>DamageTracker</c> 一致：
/// 「一切影响像素的变化都递增 <see cref="Element.RenderVersion"/>」。
/// clip 作用域与滚动条每帧重新生成（位置可能变、且成本可忽略），不属于段。
/// 金标准：retained 构建与全量构建渲染结果逐像素一致（<c>RetainedPaintTests</c> 锁定）。
/// <see cref="BuildFrame"/> 额外产出元素级脏区，宿主据此跳过空闲帧的上屏。
/// </summary>
public sealed class ElementPainter
{
    /// <summary>单元素自绘命令段；缓存后视为只读。</summary>
    private sealed record Segment(long Version, Point Position, Size Size, bool Focused, List<DrawCommand> Commands);

    private readonly ElementTheme _theme;
    private readonly ITextPainter? _text;
    private readonly DamageTracker _damageTracker = new();
    private readonly ConditionalWeakTable<Element, Segment> _segments = new();

    private DisplayList _list = null!;
    private DisplayList _paintList = null!;
    private Element? _focused;
    private int _reusedSegments;
    private int _repaintedSegments;

    public ElementPainter(ElementTheme? theme = null, ITextPainter? textPainter = null)
    {
        _theme = theme ?? new ElementTheme();
        _text = textPainter;
    }

    public ElementTheme Theme => _theme;

    /// <summary>上一次构建的段复用统计（复用数 / 重画数）。</summary>
    public (int Reused, int Repainted) LastBuildStats { get; private set; }

    /// <summary>丢弃全部段缓存：下一次构建为全量重画（窗口遮挡恢复、测试对照）。</summary>
    public void ResetSegments() => _segments.Clear();

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
        _paintList = list;
        _focused = focused;
        _reusedSegments = 0;
        _repaintedSegments = 0;

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

        LastBuildStats = (_reusedSegments, _repaintedSegments);
        return list;
    }

    private void PaintElement(Element element, Point offset, bool isRoot, DamageTracker? tracker)
    {
        var final = element.FinalRect;
        var rect = new Rect(offset.X + final.X, offset.Y + final.Y, final.Width, final.Height);

        // 焦点环画在元素矩形之外，脏区必须包含它。
        var focused = ReferenceEquals(element, _focused) && element.IsFocusable;
        var visualBounds = focused ? Inflate(rect, _theme.FocusRingWidth) : rect;
        tracker?.Visit(element, visualBounds);

        // retained 段：自绘命令（类型绘制 + 卡片 + 焦点环）按失效键复用。
        if (_segments.TryGetValue(element, out var segment) &&
            segment.Version == element.RenderVersion &&
            segment.Position == rect.Location &&
            segment.Size == rect.Size &&
            segment.Focused == focused)
        {
            _list.AddRange(segment.Commands);
            _reusedSegments++;
        }
        else
        {
            var buffer = new DisplayList();
            _paintList = buffer;
            PaintSelf(element, rect, focused, isRoot);
            _paintList = _list;
            _segments.Remove(element);
            _segments.Add(element, new Segment(element.RenderVersion, rect.Location, rect.Size, focused, buffer.Commands));
            _list.AddRange(buffer.Commands);
            _repaintedSegments++;
        }

        // ScrollViewer 无论 ClipToBounds 与否都必须裁剪：内容按 -Offset 平移，不裁就画出视口外。
        var clip = (element is Panel { ClipToBounds: true } || element is ScrollViewer)
                   && rect.Width > 0 && rect.Height > 0;
        if (clip) _list.Add(new PushClip(rect));

        // G2 的可视子节点全部来自 Panel（TextBlock 的 run 也是 Panel.Children 的一部分）。
        if (element is Panel panel)
        {
            foreach (var child in panel.Children)
                PaintElement(child, rect.Location, isRoot: false, tracker);
        }

        if (clip) _list.Add(new PopClip());

        // 滚动条画在内容之上（裁剪作用域外也不越界：位置按视口矩形内缩计算）。
        if (element is ScrollViewer viewer)
            PaintScrollBar(viewer, rect);
    }

    /// <summary>元素的自身绘制（写入当前段缓冲；子树由 <see cref="PaintElement"/> 递归处理）。</summary>
    private void PaintSelf(Element element, Rect rect, bool focused, bool isRoot)
    {
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
                case ScrollViewer:
                    PaintCard(rect, _theme.PanelBackground, _theme.PanelBorder, _theme.PanelRadius);
                    break;
                case StackPanel:
                    PaintCard(rect, _theme.PanelBackground, _theme.PanelBorder, _theme.PanelRadius);
                    break;
            }
        }

        if (focused)
            PaintFocusRing(element, rect);
    }

    /// <summary>叠加式滚动条：可滚动方向在视口内缘画拇指（v1 只显示，不做命中拖拽）。</summary>
    private void PaintScrollBar(ScrollViewer viewer, Rect rect)
    {
        if (viewer.ScrollableHeight > 0)
        {
            var trackHeight = rect.Height - ScrollBarInset * 2;
            if (trackHeight > ScrollBarThickness * 2)
            {
                var thumbHeight = Math.Max(ScrollBarThickness,
                    trackHeight * viewer.ViewportHeight / Math.Max(1, viewer.ExtentHeight));
                var maxScroll = Math.Max(1, viewer.ScrollableHeight);
                var offset = Math.Clamp(viewer.VerticalOffset, 0, viewer.ScrollableHeight);
                var thumbY = rect.Y + ScrollBarInset
                             + (trackHeight - thumbHeight) * (offset / maxScroll);
                _paintList.Add(new FillRoundedRect(
                    new Rect(rect.Right - ScrollBarInset - ScrollBarThickness, thumbY, ScrollBarThickness, thumbHeight),
                    ScrollBarThickness / 2,
                    _theme.ScrollBarThumb));
            }
        }

        if (viewer.ScrollableWidth > 0)
        {
            var trackWidth = rect.Width - ScrollBarInset * 2;
            if (trackWidth > ScrollBarThickness * 2)
            {
                var thumbWidth = Math.Max(ScrollBarThickness,
                    trackWidth * viewer.ViewportWidth / Math.Max(1, viewer.ExtentWidth));
                var maxScroll = Math.Max(1, viewer.ScrollableWidth);
                var offset = Math.Clamp(viewer.HorizontalOffset, 0, viewer.ScrollableWidth);
                var thumbX = rect.X + ScrollBarInset
                             + (trackWidth - thumbWidth) * (offset / maxScroll);
                _paintList.Add(new FillRoundedRect(
                    new Rect(thumbX, rect.Bottom - ScrollBarInset - ScrollBarThickness, thumbWidth, ScrollBarThickness),
                    ScrollBarThickness / 2,
                    _theme.ScrollBarThumb));
            }
        }
    }

    private const double ScrollBarInset = 4;
    private const double ScrollBarThickness = 6;

    private void PaintTextBlock(TextBlock block, Rect rect)
    {
        foreach (var run in block.Runs)
        {
            var text = run.Text;
            if (string.IsNullOrEmpty(text)) continue;

            var fontSize = run.GetValue(TextBlock.FontSizeProperty);
            var color = fontSize < _theme.MutedFontSizeBelow ? _theme.MutedForeground : _theme.Foreground;
            var origin = new Point(rect.X + run.FinalRect.X, rect.Y + run.FinalRect.Y);
            _text?.Paint(_paintList, text, fontSize, origin, color);
        }
    }

    private void PaintButton(Button button, Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;

        _paintList.Add(new FillRoundedRect(
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
        // 必须写段缓冲 _paintList：写 _list 会先于背景 AddRange（背景后画盖掉文字），
        // 且文字命令不进段、复用帧彻底丢失（D97 回归测试锁定）。
        _text.Paint(_paintList, label, fontSize, origin, _theme.OnAccent);
    }

    private void PaintCheckBox(CheckBox checkBox, Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var fontSize = checkBox.GetValue(TextBlock.FontSizeProperty);
        var boxSize = fontSize * 1.1;
        var box = new Rect(rect.X, rect.Y + (rect.Height - boxSize) / 2, boxSize, boxSize);

        if (checkBox.Checked)
        {
            _paintList.Add(new FillRoundedRect(box, _theme.CheckBoxRadius,
                checkBox.IsPressed ? _theme.AccentPressed : _theme.Accent));

            var strokeWidth = Math.Max(1.5, boxSize * 0.11);
            var check = new PathGeometryBuilder()
                .MoveTo(box.X + boxSize * 0.24, box.Y + boxSize * 0.52)
                .LineTo(box.X + boxSize * 0.43, box.Y + boxSize * 0.71)
                .LineTo(box.X + boxSize * 0.77, box.Y + boxSize * 0.31)
                .Build();
            _paintList.Add(new StrokePath(check, _theme.OnAccent, strokeWidth));
        }
        else
        {
            _paintList.Add(new FillRoundedRect(box, _theme.CheckBoxRadius,
                checkBox.IsPressed ? _theme.AccentPressed : _theme.ControlBackground));
            _paintList.Add(new StrokePath(
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
        // 同 PaintButton：标签必须写段缓冲（D97）。
        _text.Paint(_paintList, label, fontSize, origin, _theme.Foreground);
    }

    private void PaintCard(Rect rect, Color background, Color border, double radius)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;

        _paintList.Add(new FillRoundedRect(rect, radius, background));

        if (_theme.BorderWidth <= 0 || border.A == 0) return;
        _paintList.Add(new StrokePath(
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
        _paintList.Add(new StrokePath(
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
