using System.Diagnostics;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using EclipseUI.Rendering.Vello;
using EclipseUI.Text.Shaping;
using Canvas = Android.Graphics.Canvas;
using Point = EclipseUI.Point;

namespace EclipseUI.Platform.Android;

/// <summary>Single-touch Android host, backed by the Vello software rasterizer.</summary>
public sealed class EclipseView : FrameLayout, ISystemThemeProvider
{
    private readonly AndroidTextEditor _editor;
    private readonly HeadlessRoot _host;
    private readonly HarfBuzzTextShaper? _font;
    private readonly ITextPainter _text;
    private readonly bool _ownsText;
    private readonly ThemeBinding _theme;
    private readonly ElementPainter _painter;
    private readonly VelloSparseBackend _renderer = new();
    private readonly Paint _paint = new(PaintFlags.FilterBitmap);
    private readonly int _touchSlop;
    private RenderTarget? _target;
    private Bitmap? _bitmap;
    private int[] _colors = [];
    private long _lastTick;
    private bool _active, _closed, _scrolling;
    private int _pointer = -1;
    private Point _down;
    private ScrollViewer? _viewer;
    private double _startOffset;

    public EclipseView(Context context, Element root, IHostTheme theme)
        : this(context, root, theme, new HarfBuzzTextShaper("/system/fonts/Roboto-Regular.ttf"))
    {
    }

    private EclipseView(Context context, Element root, IHostTheme theme, HarfBuzzTextShaper font)
        : this(context, root, theme, new HarfBuzzTextPainter(font,
            ["/system/fonts/NotoSansCJK-Regular.ttc", "/system/fonts/NotoColorEmoji.ttf"]))
    {
        _font = font;
        _ownsText = true;
    }

    /// <summary>Uses an application-owned text service; closing this view does not dispose it.</summary>
    public EclipseView(Context context, Element root, IHostTheme theme, ITextPainter textPainter) : base(context)
    {
        _text = textPainter ?? throw new ArgumentNullException(nameof(textPainter));
        _host = new HeadlessRoot(root);
        _theme = new ThemeBinding(theme, this);
        _painter = new ElementPainter(_theme.Current, _text);
        _touchSlop = ViewConfiguration.Get(context)!.ScaledTouchSlop;
        Clickable = true;
        SetWillNotDraw(false);
        _editor = new AndroidTextEditor(context);
        AddView(_editor, new LayoutParams(1, 1));
    }

    ThemeVariant ISystemThemeProvider.Variant =>
        (Resources!.Configuration!.UiMode & UiMode.NightMask) == UiMode.NightYes ? ThemeVariant.Dark : ThemeVariant.Light;

    public void SetTheme(IHostTheme theme)
    {
        _theme.SetSource(theme);
        Invalidate();
    }

    public void SetActive(bool active)
    {
        _active = active;
        _lastTick = 0;
        if (!active)
        {
            CancelTouch();
            _editor.EndEditing();
        }
        Invalidate();
    }

    private double Density => Resources!.DisplayMetrics!.Density;

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (_closed || !_active || Width <= 0 || Height <= 0) return;
        int width = Width, height = Height;
        double density = Density;
        var size = new Size(width / density, height / density);
        var previous = TextMeasurer.Current;
        try
        {
            TextMeasurer.Current = _text as ITextMeasurer ?? previous;
            var now = Stopwatch.GetTimestamp();
            if (_lastTick != 0) _host.AdvanceAnimations(Stopwatch.GetElapsedTime(_lastTick, now));
            _lastTick = now;
            _editor.Synchronize(_host.Focus.Focused);
            _theme.Update(_painter, _host.Content);
            _host.AvailableSize = size;
            _host.UpdateLayout();
            _host.Content.Arrange(new Rect(Point.Zero, size));
            var frame = _painter.BuildFrame(_host.Content, size, density);
            UpdateBitmap(frame, width, height);
            using var destination = new RectF(0, 0, Width, Height);
            canvas.DrawBitmap(_bitmap!, null, destination, _paint);
        }
        finally { TextMeasurer.Current = previous; }
        PostInvalidateDelayed(33);
    }

    private void UpdateBitmap(PaintedFrame frame, int width, int height)
    {
        bool resized = _target?.Width != width || _target.Height != height;
        if (resized)
        {
            _bitmap?.Dispose();
            _bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!);
            _target = new RenderTarget(width, height);
            _colors = new int[width * height];
        }
        if (resized || !frame.Damage.IsEmpty)
        {
            _renderer.Render(frame.List, _target!);
            var pixels = _target!.Pixels;
            for (int i = 0, j = 0; i < _colors.Length; i++, j += 4)
                _colors[i] = pixels[j + 3] << 24 | pixels[j] << 16 | pixels[j + 1] << 8 | pixels[j + 2];
            _bitmap!.SetPixels(_colors, 0, width, 0, 0, width, height);
        }
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null || !_active || _closed) return false;
        if (e.ActionMasked == MotionEventActions.Down)
        {
            CancelTouch();
            _pointer = e.GetPointerId(0);
            _down = new Point(e.GetX() / Density, e.GetY() / Density);
            _host.PointerPress(_down);
            for (var node = _host.Input.Captured; node != null; node = node.Parent)
            {
                if (node is ScrollViewer viewer && viewer.ScrollableHeight > 0)
                {
                    _viewer = viewer;
                    _startOffset = viewer.VerticalOffset;
                    break;
                }
            }
        }
        else if (e.ActionMasked == MotionEventActions.Cancel) CancelTouch();
        else if (_pointer >= 0 && e.FindPointerIndex(_pointer) is var index && index >= 0)
        {
            var point = new Point(e.GetX(index) / Density, e.GetY(index) / Density);
            if (e.ActionMasked == MotionEventActions.Move)
            {
                double dy = point.Y - _down.Y;
                if (!_scrolling && _viewer != null && Math.Abs(dy) * Density > _touchSlop && Math.Abs(dy) > Math.Abs(point.X - _down.X))
                {
                    _host.Input.CancelPointerCapture();
                    _scrolling = true;
                }
                if (_scrolling) _viewer!.ScrollToVerticalOffset(_startOffset - dy);
                else _host.PointerMove(point);
            }
            else if (e.ActionMasked is MotionEventActions.Up or MotionEventActions.PointerUp && e.ActionIndex == index)
            {
                if (!_scrolling)
                {
                    var hit = _host.Input.Captured;
                    _host.PointerRelease(point);
                    if (_host.Focus.Focused is TextBox { IsReadOnly: false, IsEffectiveEnabled: true } box &&
                        IsInside(hit, box))
                        _editor.Open(box);
                    else
                        _editor.EndEditing();
                }
                CancelTouch();
            }
        }
        Invalidate();
        return true;
    }

    private static bool IsInside(Element? hit, Element editor)
    {
        for (; hit != null; hit = hit.Parent)
            if (hit == editor) return true;
        return false;
    }

    private void CancelTouch()
    {
        if (_pointer < 0) return;
        _host.Input.CancelPointerCapture();
        _host.Input.PointerLeave();
        _pointer = -1;
        _viewer = null;
        _scrolling = false;
    }

    public void Close()
    {
        if (_closed) return;
        CancelTouch();
        _editor.EndEditing();
        _closed = true;
        _host.Dispose();
        _renderer.Dispose();
        _bitmap?.Dispose();
        _paint.Dispose();
        if (_ownsText) (_text as IDisposable)?.Dispose();
        _font?.Dispose();
    }
}
