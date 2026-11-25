using CoreAnimation;
using CoreGraphics;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using Foundation;
using UIKit;

namespace EclipseUI.Platform.iOS;

/// <summary>Displays an EclipseUI tree in UIKit points and routes a single active touch to the tree.</summary>
internal sealed class EclipseView : UIView, ISystemThemeProvider
{
    private readonly HeadlessRoot _host;
    private readonly ThemeBinding _theme;
    private readonly ElementPainter _painter;
    private readonly IosTextEditor _editor;
    private readonly UiSynchronizationContext _context;
    private ElementTheme? _chromeTheme;
    private CADisplayLink? _clock;
    private DisplayList? _frame;
    private double _lastTick;
    private UITouch? _touch;
    private Point _down;
    private ScrollViewer? _scroll;
    private double _initialOffset;
    private bool _horizontal;
    private bool _closed;

    internal EclipseView(Element content, IHostTheme theme, ITextPainter textPainter, UiSynchronizationContext context)
    {
        _context = context;
        _theme = new ThemeBinding(theme, this);
        _painter = new ElementPainter(_theme.Current, textPainter);
        _editor = new IosTextEditor(context);
        AddSubview(_editor);
        _host = new HeadlessRoot(content);
        ContentMode = UIViewContentMode.Redraw;
        Opaque = true;
    }

    ThemeVariant ISystemThemeProvider.Variant =>
        (Window?.WindowScene?.TraitCollection ?? TraitCollection).UserInterfaceStyle == UIUserInterfaceStyle.Dark
            ? ThemeVariant.Dark : ThemeVariant.Light;

    internal void SetTheme(IHostTheme theme) => _theme.SetSource(theme);

    internal void SetActive(bool active)
    {
        if (_closed || active == (_clock != null)) return;
        using var dispatch = new SceneDispatch(_context);
        if (active)
        {
            _painter.ResetDamage();
            _clock = CADisplayLink.Create(UpdateFrame);
            _clock.PreferredFrameRateRange = CAFrameRateRange.Create(30, 60, 60);
            _clock.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Common);
        }
        else
        {
            _clock!.Invalidate();
            _clock.Dispose();
            _clock = null;
            _lastTick = 0;
            EndTouch();
            _editor.Close();
        }
    }

    private void UpdateFrame()
    {
        if (_clock is null || Bounds.IsEmpty) return;
        using var dispatch = new SceneDispatch(_context);
        if (_context.Drain() > 0) _host.Content.InvalidateSubtree();

        var now = _clock.Timestamp;
        if (_lastTick > 0) _host.AdvanceAnimations(TimeSpan.FromSeconds(now - _lastTick));
        _lastTick = now;
        _editor.Synchronize(_host.Focus.Focused);

        var size = new Size(Bounds.Width, Bounds.Height);
        var shrank = size.Height < _host.AvailableSize.Height;
        _theme.Update(_painter, _host.Content);
        UpdateWindowAppearance(_theme.Current);
        _host.AvailableSize = size;
        _host.UpdateLayout();
        _host.Content.Arrange(new Rect(Point.Zero, size));
        if (shrank && _editor.IsFirstResponder && _host.Focus.Focused is TextBox editor)
            RevealEditor(editor);

        // UIKit's CGContext already scales points to device pixels.
        var frame = _painter.BuildFrame(_host.Content, size);
        if (frame.Damage.IsEmpty) return;
        _frame = frame.List;
        SetNeedsDisplay();
    }

    private void UpdateWindowAppearance(ElementTheme theme)
    {
        if (_chromeTheme == theme || Window?.RootViewController is not { } controller) return;
        var surface = theme.Surface;
        controller.View!.BackgroundColor = UIColor.FromRGBA(surface.R, surface.G, surface.B, surface.A);
        controller.OverrideUserInterfaceStyle = theme.IsLight ? UIUserInterfaceStyle.Light : UIUserInterfaceStyle.Dark;
        controller.SetNeedsStatusBarAppearanceUpdate();
        _chromeTheme = theme;
    }

    // The keyboard may shorten the viewport after the tap has already focused the field.
    private static void RevealEditor(TextBox editor)
    {
        for (var parent = editor.Parent; parent != null; parent = parent.Parent)
        {
            if (parent is not ScrollViewer { ScrollableHeight: > 0 } viewer) continue;
            var y = viewer.PointFromRoot(Point.Zero).Y - editor.PointFromRoot(Point.Zero).Y - viewer.Padding;
            var bottom = y + editor.FinalRect.Height;
            if (y < 0) viewer.ScrollToVerticalOffset(viewer.VerticalOffset + y);
            else if (bottom > viewer.ViewportHeight)
                viewer.ScrollToVerticalOffset(viewer.VerticalOffset + bottom - viewer.ViewportHeight);
            break;
        }
    }

    public override void Draw(CGRect rect)
    {
        if (_frame != null && UIGraphics.GetCurrentContext() is { } context)
            CoreGraphicsRenderer.Draw(context, _frame);
    }

    public override void TouchesBegan(NSSet touches, UIEvent? evt)
    {
        if (_clock is null || _touch != null || touches.AnyObject is not UITouch touch) return;
        using var dispatch = new SceneDispatch(_context);
        _touch = touch;
        _down = Location(touch);
        _host.PointerPress(_down);
    }

    public override void TouchesMoved(NSSet touches, UIEvent? evt)
    {
        if (_touch is null || !touches.Contains(_touch)) return;
        using var dispatch = new SceneDispatch(_context);
        var point = Location(_touch);
        var dx = point.X - _down.X;
        var dy = point.Y - _down.Y;
        if (_scroll == null && Math.Max(Math.Abs(dx), Math.Abs(dy)) >= 8)
        {
            _horizontal = Math.Abs(dx) > Math.Abs(dy);
            _scroll = FindScrollTarget(_horizontal);
            if (_scroll != null)
            {
                _initialOffset = _horizontal ? _scroll.HorizontalOffset : _scroll.VerticalOffset;
                _host.Input.CancelPointerCapture();
            }
        }

        if (_scroll == null) _host.PointerMove(point);
        else if (_scroll.IsAttached && _scroll.IsEffectiveEnabled)
        {
            if (_horizontal) _scroll.ScrollToHorizontalOffset(_initialOffset - dx);
            else _scroll.ScrollToVerticalOffset(_initialOffset - dy);
        }
    }

    private ScrollViewer? FindScrollTarget(bool horizontal)
    {
        for (var node = _host.Input.Captured; node != null; node = node.Parent)
        {
            if (node is Slider slider && (slider.Orientation == Orientation.Horizontal) == horizontal) break;
            if (node is ScrollViewer viewer)
            {
                if (viewer.TryGetScrollBar(viewer.PointFromRoot(_down), out _, out _)) break;
                if ((horizontal ? viewer.ScrollableWidth : viewer.ScrollableHeight) > 0) return viewer;
            }
            if (node is Popup) break;
        }
        return null;
    }

    public override void TouchesEnded(NSSet touches, UIEvent? evt)
    {
        if (_touch is null || !touches.Contains(_touch)) return;
        using var dispatch = new SceneDispatch(_context);
        try
        {
            if (_scroll != null) return;
            var point = Location(_touch);
            _host.PointerRelease(point);
            if (_host.Focus.Focused is TextBox { IsReadOnly: false, IsEffectiveEnabled: true } box)
            {
                for (var hit = _host.HitTest(point); hit != null; hit = hit.Parent)
                {
                    if (hit != box) continue;
                    _editor.Open(box);
                    return;
                }
            }
            _editor.Close();
        }
        finally { EndTouch(); }
    }

    public override void TouchesCancelled(NSSet touches, UIEvent? evt)
    {
        using var dispatch = new SceneDispatch(_context);
        if (_touch != null && touches.Contains(_touch)) EndTouch();
    }

    private Point Location(UITouch touch)
    {
        var point = touch.LocationInView(this);
        return new Point(point.X, point.Y);
    }

    private void EndTouch()
    {
        if (_touch is null) return;
        _touch = null;
        _scroll = null;
        _host.Input.CancelPointerCapture();
        _host.Input.PointerLeave();
    }

    internal void Close()
    {
        if (_closed) return;
        using var dispatch = new SceneDispatch(_context);
        SetActive(false);
        _closed = true;
        _frame = null;
        try { _host.Dispose(); }
        finally
        {
            _editor.RemoveFromSuperview();
            _editor.Dispose();
        }
    }
}
