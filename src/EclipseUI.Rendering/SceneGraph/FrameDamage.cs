using System;
using System.Collections.Generic;

namespace EclipseUI.Rendering;

/// <summary>
/// 一帧的脏区集合（根坐标，逻辑单位）。重叠/相邻矩形自动合并；
/// 首帧、尺寸或 DPI 变化时标记为 <see cref="IsFull"/>，调用方据此跳过增量判定。
/// </summary>
public sealed class FrameDamage
{
    private readonly List<Rect> _rects = new();

    /// <summary>整帧重绘（首帧 / resize / DPI 变化）。</summary>
    public bool IsFull { get; private set; }

    public bool IsEmpty => !IsFull && _rects.Count == 0;

    /// <summary>合并后的脏矩形。</summary>
    public IReadOnlyList<Rect> Rects => _rects;

    /// <summary>所有脏矩形的外接矩形。</summary>
    public Rect Bounds { get; private set; }

    /// <summary>合并后的脏区面积（逻辑单位²）。</summary>
    public double Area { get; private set; }

    public void Add(Rect rect)
    {
        if (IsFull || rect.Width <= 0 || rect.Height <= 0) return;

        var merged = rect;
        bool changed;
        do
        {
            changed = false;
            for (int i = _rects.Count - 1; i >= 0; i--)
            {
                if (!Overlaps(_rects[i], merged)) continue;
                merged = Union(_rects[i], merged);
                _rects.RemoveAt(i);
                changed = true;
            }
        } while (changed);

        _rects.Add(merged);
        Recalculate();
    }

    public void AddFull()
    {
        IsFull = true;
        _rects.Clear();
        Bounds = default;
        Area = 0;
    }

    /// <summary>脏区占整幅画面的比例；整帧重绘恒为 1。</summary>
    public double Coverage(Size surface)
    {
        if (IsFull) return 1;
        var total = surface.Width * surface.Height;
        return total <= 0 ? 0 : Math.Min(1, Area / total);
    }

    private void Recalculate()
    {
        if (_rects.Count == 0)
        {
            Bounds = default;
            Area = 0;
            return;
        }

        var bounds = _rects[0];
        double area = 0;
        foreach (var rect in _rects)
        {
            bounds = Union(bounds, rect);
            area += rect.Width * rect.Height;
        }

        Bounds = bounds;
        Area = area;
    }

    private static bool Overlaps(Rect a, Rect b) =>
        a.Left <= b.Right && b.Left <= a.Right &&
        a.Top <= b.Bottom && b.Top <= a.Bottom;

    internal static Rect Union(Rect a, Rect b)
    {
        var left = Math.Min(a.Left, b.Left);
        var top = Math.Min(a.Top, b.Top);
        var right = Math.Max(a.Right, b.Right);
        var bottom = Math.Max(a.Bottom, b.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }
}

/// <summary>
/// 元素树脏区跟踪：对比上一帧的（绝对矩形, RenderVersion）快照。
/// 只统计“可能影响像素的变化”：属性/按下态/焦点版本、布局矩形变化、子树增删。
/// 不在画面内的矩形会被裁剪，避免覆盖率失真。
/// </summary>
internal sealed class DamageTracker
{
    private readonly record struct Entry(Rect Bounds, long Version);

    private Dictionary<Element, Entry> _previous = new();
    private Dictionary<Element, Entry> _current = new();
    private FrameDamage _damage = new();
    private Size _surface;
    private double _scale;
    private Element? _focused;
    private Element? _lastFocused;
    private Size _lastSurface;
    private double _lastScale;
    private bool _hasFrame;
    private bool _focusedChanged;

    public FrameDamage Begin(Size surface, double scale, Element? focused)
    {
        _damage = new FrameDamage();
        _surface = surface;
        _scale = scale;
        _focused = focused;

        if (!_hasFrame || surface != _lastSurface || Math.Abs(scale - _lastScale) > 1e-9)
            _damage.AddFull();

        _focusedChanged = _hasFrame && !ReferenceEquals(focused, _lastFocused);
        if (_focusedChanged && _lastFocused is not null &&
            _previous.TryGetValue(_lastFocused, out var previousFocus))
        {
            AddClamped(previousFocus.Bounds);
        }

        // End() 已清空并保留容量：避免每帧按元素数重新分配字典。
        _current.Clear();
        return _damage;
    }

    public void Visit(Element element, Rect bounds)
    {
        _current[element] = new Entry(bounds, element.RenderVersion);
        if (_damage.IsFull) return;

        if (_previous.TryGetValue(element, out var previous))
        {
            if (previous.Version != element.RenderVersion || previous.Bounds != bounds)
            {
                AddClamped(previous.Bounds);
                AddClamped(bounds);
            }
        }
        else
        {
            AddClamped(bounds);
        }

        if (_focusedChanged && ReferenceEquals(element, _focused))
            AddClamped(bounds);
    }

    public void End()
    {
        if (!_damage.IsFull)
        {
            foreach (var (element, entry) in _previous)
            {
                if (!_current.ContainsKey(element))
                    AddClamped(entry.Bounds);
            }
        }

        _lastFocused = _focused;
        _lastSurface = _surface;
        _lastScale = _scale;
        _hasFrame = true;

        var recycled = _previous;
        _previous = _current;
        _current = recycled;
        _current.Clear();
    }

    /// <summary>丢弃历史：下一帧按整帧重绘处理（WM_PAINT、外部直接构建 display list 后）。</summary>
    public void Reset()
    {
        _hasFrame = false;
        _previous.Clear();
    }

    private void AddClamped(Rect rect)
    {
        var left = Math.Max(rect.Left, 0);
        var top = Math.Max(rect.Top, 0);
        var right = Math.Min(rect.Right, _surface.Width);
        var bottom = Math.Min(rect.Bottom, _surface.Height);
        if (right <= left || bottom <= top) return;
        _damage.Add(new Rect(left, top, right - left, bottom - top));
    }
}
