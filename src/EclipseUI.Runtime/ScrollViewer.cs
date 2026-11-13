using System;
using EclipseUI;

namespace EclipseUI.Elements;

/// <summary>
/// 滚动条可见性（Avalonia 同名枚举语义对齐，design/05 §布局契约）：
/// <list type="bullet">
/// <item><see cref="Auto"/>：可滚，<see cref="ScrollViewer.ScrollableWidth"/>/Height &gt; 0 时出条；</item>
/// <item><see cref="Visible"/>：可滚且恒出条（不可滚时画满槽拇指）；</item>
/// <item><see cref="Hidden"/>：可滚但不出条；</item>
/// <item><see cref="Disabled"/>：该方向不可滚——<c>Scrollable*</c> 报 0、偏移钳到 0、
///   内容按视口宽/高排列，溢出由 PushClip 裁剪。</item>
/// </list>
/// 可滚方向以无穷约束测量内容；Disabled 方向保留视口约束。
/// </summary>
public enum ScrollBarVisibility
{
    Auto,
    Visible,
    Hidden,
    Disabled,
}

/// <summary>
/// 滚动视口：首个子元素作为内容，测量时按可滚方向给无穷约束量出完整
/// <see cref="ExtentHeight"/>，排列时内容按 -Offset 平移到视口内、绘制侧由
/// <c>ElementPainter</c> 对本元素强制 PushClip。
/// <list type="bullet">
/// <item>滚动偏移：滚轮一格（Win32 刻度 120）= <see cref="WheelScrollPixels"/> 逻辑像素；
///   也可代码调 <see cref="ScrollToVerticalOffset"/>/<see cref="ScrollToHorizontalOffset"/>。</item>
/// <item>越界钳制：偏移在布局使用点钳到 [0, extent-viewport]；属性值不回写，避免排列期失效循环。</item>
/// <item>可用高度无限（StackPanel 约束）时视口 = 内容期望，不产生滚动——外层须给有限高度
///   （如 Grid 固定/星号行、窗口根）。</item>
/// <item>方向开关（design/05 §布局契约）：Horizontal 默认 <see cref="ScrollBarVisibility.Disabled"/>
///   （Avalonia 对齐——单列文档式布局不被偶发横向溢出拖出滚动条），需要横滚显式设 Auto；
///   Vertical 默认 Auto。</item>
/// <item>滚动条支持轨道翻页和拖动；PageUp/Down、Ctrl+Home/End 与 Shift+横滚共用偏移钳制。</item>
/// </list>
/// </summary>
public class ScrollViewer : Panel
{
    /// <summary>每 1/120 齿轮刻度对应的滚动像素（一格滚轮 = 48 逻辑像素，WPF 惯例近似）。</summary>
    public const double WheelScrollPixels = 48d / 120d;

    public static readonly StyledProperty<double> VerticalOffsetProperty =
        StyledProperty<double>.Register<ScrollViewer>("VerticalOffset", 0d,
            new PropertyOptions { Invalidate = InvalidateKind.Arrange });

    public static readonly StyledProperty<double> HorizontalOffsetProperty =
        StyledProperty<double>.Register<ScrollViewer>("HorizontalOffset", 0d,
            new PropertyOptions { Invalidate = InvalidateKind.Arrange });

    /// <summary>横向滚动开关（默认 Disabled：内容按视口宽排列、溢出裁剪、不出条——Avalonia 对齐）。</summary>
    public static readonly StyledProperty<ScrollBarVisibility> HorizontalScrollBarVisibilityProperty =
        StyledProperty<ScrollBarVisibility>.Register<ScrollViewer>("HorizontalScrollBarVisibility",
            ScrollBarVisibility.Disabled,
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    /// <summary>纵向滚动开关（默认 Auto：按需出条）。</summary>
    public static readonly StyledProperty<ScrollBarVisibility> VerticalScrollBarVisibilityProperty =
        StyledProperty<ScrollBarVisibility>.Register<ScrollViewer>("VerticalScrollBarVisibility",
            ScrollBarVisibility.Auto,
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    private bool _draggingBar, _dragHorizontal;
    private double _dragStart, _dragOffset;
    public bool TryGetScrollBar(Point local, out bool horizontal, out Rect thumb)
    {
        foreach (var h in new[] { false, true })
        {
            var visibility = h ? HorizontalScrollBarVisibility : VerticalScrollBarVisibility;
            var scrollable = h ? ScrollableWidth : ScrollableHeight;
            if (visibility is ScrollBarVisibility.Hidden or ScrollBarVisibility.Disabled || visibility == ScrollBarVisibility.Auto && scrollable <= 0) continue;
            var length = (h ? FinalRect.Width : FinalRect.Height) - 8;
            if (length <= 12) continue;
            var viewport = h ? ViewportWidth : ViewportHeight; var extent = h ? ExtentWidth : ExtentHeight;
            var size = scrollable <= 0 ? length : Math.Max(6, length * viewport / Math.Max(1, extent));
            var offset = h ? HorizontalOffset : VerticalOffset;
            var position = 4 + (length - size) * offset / Math.Max(1, scrollable);
            thumb = h ? new(position, FinalRect.Height - 10, size, 6) : new(FinalRect.Width - 10, position, 6, size);
            var track = h ? new Rect(4, FinalRect.Height - 14, length, 14) : new Rect(FinalRect.Width - 14, 4, 14, length);
            if (track.Contains(local)) { horizontal = h; return true; }
        }
        horizontal = false; thumb = default; return false;
    }
    public ScrollViewer()
    {
        AddHandler(PointerPressedEvent, e =>
        {
            if (e is not PointerEventArgs p || !TryGetScrollBar(PointFromRoot(p.Position), out var h, out var thumb)) return;
            var local = PointFromRoot(p.Position); var coordinate = h ? local.X : local.Y;
            _dragHorizontal = h;
            if (!thumb.Contains(local))
            {
                var direction = coordinate < (h ? thumb.X : thumb.Y) ? -1 : 1;
                if (h) ScrollToHorizontalOffset(HorizontalOffset + direction * ViewportWidth); else ScrollToVerticalOffset(VerticalOffset + direction * ViewportHeight);
            }
            else { _draggingBar = true; _dragStart = coordinate; _dragOffset = h ? HorizontalOffset : VerticalOffset; }
            e.Handled = true;
        });
        AddHandler(PointerMovedEvent, e =>
        {
            if (!_draggingBar || e is not PointerEventArgs p) return;
            var local = PointFromRoot(p.Position); var h = _dragHorizontal;
            var length = (h ? FinalRect.Width : FinalRect.Height) - 8;
            var thumb = Math.Max(6, length * (h ? ViewportWidth : ViewportHeight) / Math.Max(1, h ? ExtentWidth : ExtentHeight));
            var offset = _dragOffset + ((h ? local.X : local.Y) - _dragStart) / Math.Max(1, length - thumb) * (h ? ScrollableWidth : ScrollableHeight);
            if (h) ScrollToHorizontalOffset(offset); else ScrollToVerticalOffset(offset);
            e.Handled = true;
        });
        AddHandler(PointerReleasedEvent, _ => _draggingBar = false);
        AddHandler(KeyDownEvent, e =>
        {
            if (e is not KeyEventArgs k) return;
            switch (k.Key)
            {
                case Key.PageDown: ScrollToVerticalOffset(VerticalOffset + ViewportHeight); break;
                case Key.PageUp: ScrollToVerticalOffset(VerticalOffset - ViewportHeight); break;
                case Key.Home when k.Modifiers.HasFlag(KeyModifiers.Control): ScrollToHome(); break;
                case Key.End when k.Modifiers.HasFlag(KeyModifiers.Control): ScrollToEnd(); break;
                default: return;
            }
            e.Handled = true;
        });
        // 冒泡路径上的滚轮：命中内容里的子元素，事件一路冒到本容器后消费。
        AddHandler(Element.PointerWheelChangedEvent, (RoutedEventArgs args) =>
        {
            if (args is not WheelEventArgs wheel || args.Handled) return;
            var step = wheel.Delta * WheelScrollPixels;
            if (wheel.Modifiers.HasFlag(KeyModifiers.Shift) && ScrollableWidth > 0)
            {
                ScrollToHorizontalOffset(HorizontalOffset - step); args.Handled = true;
            }
            else if (ScrollableHeight > 0)
            {
                ScrollToVerticalOffset(VerticalOffset - step);
                args.Handled = true;
            }
            else if (ScrollableWidth > 0)
            {
                ScrollToHorizontalOffset(HorizontalOffset - step);
                args.Handled = true;
            }
        });
    }

    public double VerticalOffset
    {
        get => Math.Clamp(GetValue(VerticalOffsetProperty), 0, ScrollableHeight);
        set => SetValue(VerticalOffsetProperty, value);
    }

    public double HorizontalOffset
    {
        get => Math.Clamp(GetValue(HorizontalOffsetProperty), 0, ScrollableWidth);
        set => SetValue(HorizontalOffsetProperty, value);
    }

    /// <summary>横向滚动开关。</summary>
    public ScrollBarVisibility HorizontalScrollBarVisibility
    {
        get => GetValue(HorizontalScrollBarVisibilityProperty);
        set => SetValue(HorizontalScrollBarVisibilityProperty, value);
    }

    /// <summary>纵向滚动开关。</summary>
    public ScrollBarVisibility VerticalScrollBarVisibility
    {
        get => GetValue(VerticalScrollBarVisibilityProperty);
        set => SetValue(VerticalScrollBarVisibilityProperty, value);
    }

    /// <summary>该方向是否可滚（Disabled = false——Scrollable* 报 0、偏移钳 0、内容按视口排列）。</summary>
    private bool CanHorizontallyScroll => HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled;

    private bool CanVerticallyScroll => VerticalScrollBarVisibility != ScrollBarVisibility.Disabled;

    /// <summary>内容范围；可滚方向由无穷约束测量得到。</summary>
    public double ExtentHeight { get; private set; }

    /// <summary>内容范围；可滚方向由无穷约束测量得到。</summary>
    public double ExtentWidth { get; private set; }

    /// <summary>可视区尺寸（测量期按可用估计，排列期按最终矩形定值）。</summary>
    public double ViewportHeight { get; private set; }

    /// <summary>可视区尺寸（测量期按可用估计，排列期按最终矩形定值）。</summary>
    public double ViewportWidth { get; private set; }

    public double ScrollableHeight => CanVerticallyScroll
        ? Math.Max(0, ExtentHeight - ViewportHeight)
        : 0;

    public double ScrollableWidth => CanHorizontallyScroll
        ? Math.Max(0, ExtentWidth - ViewportWidth)
        : 0;

    /// <summary>滚动到钳制后的偏移（越界值收敛到边界）。</summary>
    public void ScrollToVerticalOffset(double offset) =>
        VerticalOffset = Math.Clamp(offset, 0, ScrollableHeight);

    /// <summary>滚动到钳制后的偏移（越界值收敛到边界）。</summary>
    public void ScrollToHorizontalOffset(double offset) =>
        HorizontalOffset = Math.Clamp(offset, 0, ScrollableWidth);

    public void ScrollToEnd() => ScrollToVerticalOffset(double.PositiveInfinity);

    public void ScrollToHome() => ScrollToVerticalOffset(0);

    private Element? Content => Children.Count > 0 ? Children[0] : null;

    protected internal override Size MeasureCore(Size available)
    {
        var padding = Padding;
        var availW = Math.Max(0, available.Width - padding * 2);
        var availH = Math.Max(0, available.Height - padding * 2);

        var content = Content;
        if (content is null)
        {
            ExtentWidth = ExtentHeight = 0;
            ViewportWidth = double.IsPositiveInfinity(availW) ? 0 : availW;
            ViewportHeight = double.IsPositiveInfinity(availH) ? 0 : availH;
            return new Size(
                double.IsPositiveInfinity(availW) ? padding * 2 : availW + padding * 2,
                double.IsPositiveInfinity(availH) ? padding * 2 : availH + padding * 2);
        }

        MeasureContent(content, availW, availH);

        // 视口估计：可用有限时取 min(extent, 可用)——内容不满不空占，内容超出即出滚动条。
        ViewportWidth = double.IsPositiveInfinity(availW) ? ExtentWidth : Math.Min(availW, ExtentWidth);
        ViewportHeight = double.IsPositiveInfinity(availH) ? ExtentHeight : Math.Min(availH, ExtentHeight);

        return new Size(
            double.IsPositiveInfinity(availW) ? ExtentWidth + padding * 2 : ViewportWidth + padding * 2,
            double.IsPositiveInfinity(availH) ? ExtentHeight + padding * 2 : ViewportHeight + padding * 2);
    }

    protected internal override void ArrangeCore(Rect final)
    {
        var padding = Padding;
        var innerW = Math.Max(0, final.Width - padding * 2);
        var innerH = Math.Max(0, final.Height - padding * 2);
        ViewportWidth = innerW;
        ViewportHeight = innerH;

        var content = Content;
        if (content is null) return;
        // 最终视口可能与测量条件不同。方向约束相同则命中子级缓存，偏移变化不重测。
        MeasureContent(content, innerW, innerH);

        // 偏移在使用点钳制；不回写属性（排列期写带 Arrange 失效的属性会循环）。
        var vOffset = Math.Clamp(VerticalOffset, 0, ScrollableHeight);
        var hOffset = Math.Clamp(HorizontalOffset, 0, ScrollableWidth);

        // 内容铺 max(viewport, extent)：窄于视口时拉伸填满，宽于视口时保留全部可滚区域。
        // 方向开关（design/05 §布局契约）：Disabled 时该方向钳到视口宽/高——溢出由
        // PushClip 裁剪（Avalonia-Disabled 裁剪语义），可滚方向照旧保留完整可滚区域。
        content.Arrange(new Rect(
            padding - hOffset,
            padding - vOffset,
            CanHorizontallyScroll ? Math.Max(innerW, ExtentWidth) : innerW,
            CanVerticallyScroll ? Math.Max(innerH, ExtentHeight) : innerH));
    }

    private void MeasureContent(Element content, double viewportWidth, double viewportHeight)
    {
        content.Measure(new Size(
            CanHorizontallyScroll ? double.PositiveInfinity : viewportWidth,
            CanVerticallyScroll ? double.PositiveInfinity : viewportHeight));
        ExtentWidth = content.DesiredSize.Width;
        ExtentHeight = content.DesiredSize.Height;
    }
}
