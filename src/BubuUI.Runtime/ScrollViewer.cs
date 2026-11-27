using System;
using BubuUI;

namespace BubuUI.Elements;

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
/// <item>滚动条支持宽命中区拖动、轨道点击翻页与按住连滚；PageUp/Down、Ctrl+Home/End 与 Shift+横滚共用偏移钳制。</item>
/// </list>
/// </summary>
public class ScrollViewer : Panel, IFrameAnimation
{
    public override Rect? GetContentClip() => new Rect(Padding, Padding, ViewportWidth, ViewportHeight);
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

    public static readonly StyledProperty<double> ScrollBarInsetProperty = StyledProperty<double>.Register<ScrollViewer>(nameof(ScrollBarInset), 0, new() { Invalidate = InvalidateKind.Arrange });
    public static readonly StyledProperty<double> ScrollBarThicknessProperty = StyledProperty<double>.Register<ScrollViewer>(nameof(ScrollBarThickness), 0, new() { Invalidate = InvalidateKind.Arrange });
    public static readonly StyledProperty<double> ScrollBarHitThicknessProperty = StyledProperty<double>.Register<ScrollViewer>(nameof(ScrollBarHitThickness), 0, new() { Invalidate = InvalidateKind.Arrange });
    public static readonly StyledProperty<double> ScrollBarMinimumThumbLengthProperty = StyledProperty<double>.Register<ScrollViewer>(nameof(ScrollBarMinimumThumbLength), 0, new() { Invalidate = InvalidateKind.Arrange });
    public static readonly StyledProperty<double> ScrollBarButtonLengthProperty = StyledProperty<double>.Register<ScrollViewer>(nameof(ScrollBarButtonLength), 0, new() { Invalidate = InvalidateKind.Arrange });
    public static readonly StyledProperty<double> ScrollBarThumbPaddingProperty = StyledProperty<double>.Register<ScrollViewer>(nameof(ScrollBarThumbPadding), 0, new() { Invalidate = InvalidateKind.Arrange });
    public double ScrollBarInset { get => GetValue(ScrollBarInsetProperty); set => SetValue(ScrollBarInsetProperty, Math.Max(0, value)); }
    public double ScrollBarThickness { get => GetValue(ScrollBarThicknessProperty); set => SetValue(ScrollBarThicknessProperty, Math.Max(0, value)); }
    public double ScrollBarHitThickness { get => GetValue(ScrollBarHitThicknessProperty); set => SetValue(ScrollBarHitThicknessProperty, Math.Max(0, value)); }
    public double ScrollBarMinimumThumbLength { get => GetValue(ScrollBarMinimumThumbLengthProperty); set => SetValue(ScrollBarMinimumThumbLengthProperty, Math.Max(0, value)); }
    public double ScrollBarButtonLength { get => GetValue(ScrollBarButtonLengthProperty); set => SetValue(ScrollBarButtonLengthProperty, Math.Max(0, value)); }
    public double ScrollBarThumbPadding { get => GetValue(ScrollBarThumbPaddingProperty); set => SetValue(ScrollBarThumbPaddingProperty, Math.Max(0, value)); }
    public bool IsPointerWithinScrollArea { get; private set; }
    public long ScrollActivityVersion { get; private set; }
    private Point _scrollPointer;
    public bool IsScrollBarHovered(bool horizontal) => IsEffectiveEnabled && IsPointerWithinScrollArea &&
        TryGetScrollBar(PointFromRoot(_scrollPointer), out var h, out _) && h == horizontal;
    public bool IsScrollBarPressed(bool horizontal) => _barHeld && IsPressed && IsEffectiveEnabled && _dragHorizontal == horizontal;
    public bool IsScrollBarButtonHovered(bool horizontal, bool increase) => IsScrollBarHovered(horizontal) &&
        TryGetScrollBarGeometry(horizontal, out var bar) && (increase ? bar.IncreaseButton : bar.DecreaseButton).Contains(PointFromRoot(_scrollPointer));
    public bool IsScrollBarButtonPressed(bool horizontal, bool increase) => IsScrollBarPressed(horizontal) && _lineScrolling &&
        (_pageDirection > 0) == increase && IsScrollBarButtonHovered(horizontal, increase);
    private double BarInset => ScrollBarInset;
    private double BarThickness => ScrollBarThickness;
    private double BarHitThickness => ScrollBarHitThickness;
    private const double RepeatDelay = .4, RepeatInterval = .06;
    private bool _barHeld, _barScrolling, _draggingBar, _dragHorizontal, _lineScrolling;
    private double _dragStart, _dragOffset, _repeatRemaining;
    private int _pageDirection;
    private Point _barPointer;

    /// <summary>Local scrollbar geometry shared by drawing, hit testing and dragging.</summary>
    public bool TryGetScrollBarGeometry(bool horizontal, out ScrollBarGeometry geometry)
    {
        geometry = default;
        if (BarThickness <= 0 || BarHitThickness <= 0) return false;
        var visibility = horizontal ? HorizontalScrollBarVisibility : VerticalScrollBarVisibility;
        var scrollable = horizontal ? ScrollableWidth : ScrollableHeight;
        if (visibility is ScrollBarVisibility.Hidden or ScrollBarVisibility.Disabled || visibility == ScrollBarVisibility.Auto && scrollable <= 0) return false;
        var otherVisibility = horizontal ? VerticalScrollBarVisibility : HorizontalScrollBarVisibility;
        var otherScrollable = horizontal ? ScrollableHeight : ScrollableWidth;
        var corner = otherVisibility == ScrollBarVisibility.Visible || otherVisibility == ScrollBarVisibility.Auto && otherScrollable > 0 ? BarHitThickness : 0;
        var endInset = ScrollBarButtonLength > 0 ? 0 : BarInset;
        var availableLength = (horizontal ? FinalRect.Width : FinalRect.Height) - endInset * 2 - corner;
        var buttonLength = Math.Min(ScrollBarButtonLength, Math.Max(0, (availableLength - BarThickness * 2) / 2));
        var length = availableLength - buttonLength * 2;
        if (length <= BarThickness * 2 || (horizontal ? FinalRect.Height : FinalRect.Width) < BarInset + BarThickness) return false;
        var viewport = horizontal ? ViewportWidth : ViewportHeight;
        var extent = horizontal ? ExtentWidth : ExtentHeight;
        var thumbPadding = Math.Min(ScrollBarThumbPadding, Math.Max(0, (length - BarThickness) / 2));
        var size = scrollable <= 0 ? length : Math.Clamp(length * viewport / Math.Max(1, extent), Math.Min(length, Math.Max(BarThickness, ScrollBarMinimumThumbLength + thumbPadding * 2)), length);
        var offset = horizontal ? HorizontalOffset : VerticalOffset;
        var position = endInset + buttonLength + (length - size) * offset / (scrollable > 0 ? scrollable : 1);
        var thumb = horizontal ? new Rect(position + thumbPadding, FinalRect.Height - BarInset - BarThickness, size - thumbPadding * 2, BarThickness)
            : new Rect(FinalRect.Width - BarInset - BarThickness, position + thumbPadding, BarThickness, size - thumbPadding * 2);
        var track = horizontal ? new Rect(endInset + buttonLength, Math.Max(0, FinalRect.Height - BarHitThickness), length, Math.Min(BarHitThickness, FinalRect.Height))
            : new Rect(Math.Max(0, FinalRect.Width - BarHitThickness), endInset + buttonLength, Math.Min(BarHitThickness, FinalRect.Width), length);
        geometry = new(track, thumb)
        {
            ThumbHit = horizontal ? new(position, track.Y, size, track.Height) : new(track.X, position, track.Width, size),
            DecreaseButton = horizontal ? new(endInset, track.Y, buttonLength, track.Height) : new(track.X, endInset, track.Width, buttonLength),
            IncreaseButton = horizontal ? new(track.Right, track.Y, buttonLength, track.Height) : new(track.X, track.Bottom, track.Width, buttonLength),
        };
        return true;
    }

    public bool TryGetScrollBar(Point local, out bool horizontal, out Rect thumb)
    {
        // Horizontal is drawn last and owns any overlap at the bottom-right corner.
        for (var i = 0; i < 2; i++)
        {
            var h = i == 0;
            if (TryGetScrollBarGeometry(h, out var bar) && (bar.Track.Contains(local) || bar.DecreaseButton.Contains(local) || bar.IncreaseButton.Contains(local)))
            { horizontal = h; thumb = bar.ThumbHit; return true; }
        }
        horizontal = false; thumb = default; return false;
    }
    public ScrollViewer()
    {
        AddHandler(PointerEnteredEvent, _ => { IsPointerWithinScrollArea = true; InvalidateRender(); });
        AddHandler(PointerExitedEvent, _ => { IsPointerWithinScrollArea = false; InvalidateRender(); });
        AddHandler(PointerMovedEvent, e =>
        {
            if (e is not PointerEventArgs p) return;
            var vertical = IsScrollBarHovered(false); var horizontal = IsScrollBarHovered(true);
            _scrollPointer = p.Position;
            if (vertical || horizontal || IsScrollBarHovered(false) || IsScrollBarHovered(true)) InvalidateRender();
        });
        PropertyChanged += (_, property) =>
        {
            if (property == HorizontalOffsetProperty || property == VerticalOffsetProperty) ScrollActivityVersion++;
        };
        AddHandler(PointerPressedEvent, e =>
        {
            if (!ReferenceEquals(e.Source, this) || e is not PointerEventArgs { Button: PointerButton.Left } p ||
                !TryGetScrollBar(PointFromRoot(p.Position), out var h, out var thumb)) return;
            var local = PointFromRoot(p.Position); var coordinate = h ? local.X : local.Y;
            _scrollPointer = p.Position;
            _barHeld = true; _barScrolling = (h ? ScrollableWidth : ScrollableHeight) > 0;
            _dragHorizontal = h; _barPointer = p.Position;
            _repeatRemaining = RepeatDelay;
            // Use the whole cross-axis hit strip for the thumb, including its padding.
            var start = h ? thumb.Left : thumb.Top; var end = h ? thumb.Right : thumb.Bottom;
            TryGetScrollBarGeometry(h, out var geometry);
            _lineScrolling = geometry.DecreaseButton.Contains(local) || geometry.IncreaseButton.Contains(local);
            _draggingBar = !_lineScrolling && coordinate >= start && coordinate <= end;
            if (!_draggingBar)
            {
                _pageDirection = coordinate < start ? -1 : 1;
                PageTowardPointer();
            }
            else { _dragStart = coordinate; _dragOffset = h ? HorizontalOffset : VerticalOffset; }
            e.Handled = true;
        });
        AddHandler(PointerMovedEvent, e =>
        {
            if (!_barHeld || !ReferenceEquals(e.Source, this) || e is not PointerEventArgs p) return;
            _barPointer = p.Position;
            e.Handled = true;
            if (!_barScrolling) return;
            if (!CanContinueBarInteraction()) { StopBarScrolling(); return; }
            if (!_draggingBar) return;
            var local = PointFromRoot(p.Position); var h = _dragHorizontal;
            TryGetScrollBarGeometry(h, out var bar);
            var travel = h ? bar.Track.Width - bar.ThumbHit.Width : bar.Track.Height - bar.ThumbHit.Height;
            var offset = _dragOffset + ((h ? local.X : local.Y) - _dragStart) / (travel > 0 ? travel : 1) * (h ? ScrollableWidth : ScrollableHeight);
            if (h) ScrollToHorizontalOffset(offset); else ScrollToVerticalOffset(offset);
        });
        AddHandler(PointerReleasedEvent, e => { if (_barHeld) { CancelPointerInteraction(); e.Handled = true; } });
        AddHandler(KeyDownEvent, e =>
        {
            if (e is not KeyEventArgs k) return;
            switch (k.Key)
            {
                case Key.PageDown: ScrollToVerticalOffset(VerticalOffset + ViewportHeight); break;
                case Key.PageUp: ScrollToVerticalOffset(VerticalOffset - ViewportHeight); break;
                case Key.Home when InputGestures.HasCommand(k.Modifiers): ScrollToHome(); break;
                case Key.End when InputGestures.HasCommand(k.Modifiers): ScrollToEnd(); break;
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

    private bool CanContinueBarInteraction() => IsPressed && !IsDisposed && IsEffectiveEnabled && IsEffectivelyVisible && IsHitTestVisible &&
        TryGetScrollBarGeometry(_dragHorizontal, out _) && (_dragHorizontal ? ScrollableWidth : ScrollableHeight) > 0;

    private void PageTowardPointer()
    {
        if (!TryGetScrollBarGeometry(_dragHorizontal, out var bar)) return;
        var local = PointFromRoot(_barPointer);
        if (_lineScrolling)
        {
            var button = _pageDirection < 0 ? bar.DecreaseButton : bar.IncreaseButton;
            if (!button.Contains(local)) return;
            if (_dragHorizontal) ScrollToHorizontalOffset(HorizontalOffset + _pageDirection * 16);
            else ScrollToVerticalOffset(VerticalOffset + _pageDirection * 16);
            return;
        }
        if (!bar.Track.Contains(local)) return; // Leaving the track pauses auto-repeat.
        var coordinate = _dragHorizontal ? local.X : local.Y;
        var edge = _dragHorizontal ? _pageDirection < 0 ? bar.ThumbHit.Left : bar.ThumbHit.Right
            : _pageDirection < 0 ? bar.ThumbHit.Top : bar.ThumbHit.Bottom;
        if ((coordinate - edge) * _pageDirection <= 0) return; // Stop when the thumb reaches the pointer.
        if (_dragHorizontal) ScrollToHorizontalOffset(HorizontalOffset + _pageDirection * ViewportWidth);
        else ScrollToVerticalOffset(VerticalOffset + _pageDirection * ViewportHeight);
    }

    public void Advance(TimeSpan elapsed)
    {
        if (!_barHeld || !_barScrolling) return;
        if (!CanContinueBarInteraction()) { StopBarScrolling(); return; }
        if (_draggingBar) return;
        _repeatRemaining -= Math.Min(1, elapsed.TotalSeconds);
        // Bound catch-up work after a suspended or stalled frame.
        for (var i = 0; _repeatRemaining <= 0 && i < 16; i++)
        { PageTowardPointer(); _repeatRemaining += RepeatInterval; }
        if (_repeatRemaining <= 0) _repeatRemaining = RepeatInterval;
    }

    private void StopBarScrolling() { _barScrolling = _draggingBar = _lineScrolling = false; }
    internal override void CancelPointerInteraction() { _barHeld = false; StopBarScrolling(); _repeatRemaining = 0; }
    protected override void OnDetached() { IsPointerWithinScrollArea = false; CancelPointerInteraction(); base.OnDetached(); }
    protected override void OnDispose() { CancelPointerInteraction(); base.OnDispose(); }

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

/// <summary>Scrollbar hit track and painted thumb in ScrollViewer-local coordinates.</summary>
public readonly record struct ScrollBarGeometry(Rect Track, Rect Thumb)
{
    public Rect ThumbHit { get; init; }
    public Rect DecreaseButton { get; init; }
    public Rect IncreaseButton { get; init; }
}
