using BubuUI.Elements;
using BubuUI.Rendering;

namespace BubuUI.Themes.Fluent;

/// <summary>Overlay indicators expand only at the scrollbar; hit geometry never animates.</summary>
public class FluentScrollBarVisual : FluentVisual<ScrollViewer>, IFrameAnimation
{
    private sealed class BarState
    {
        public double Opacity = 1, Expansion, Hold = 2, HoverTime, Contract;
        public bool Available, Hovered, Pressed, Enabled = true;
    }
    private readonly BarState[] _bars = [new(), new()];
    private long _activity;
    public FluentScrollBarVisual() => IsHitTestVisible = false;

    public void Advance(TimeSpan elapsed)
    {
        var settings = Palette.InteractionPreferences.Current;
        var animate = settings.AnimationsEnabled;
        for (Element? node = Owner; node is not null; node = node.Parent) animate &= node.AnimationsEnabled;
        var activity = _activity != Owner.ScrollActivityVersion;
        _activity = Owner.ScrollActivityVersion;
        var seconds = Math.Max(0, elapsed.TotalSeconds);
        for (var i = 0; i < 2; i++)
        {
            var state = _bars[i]; var horizontal = i == 1;
            var available = Owner.TryGetScrollBarGeometry(horizontal, out _);
            var hovered = Owner.IsScrollBarHovered(horizontal);
            var pressed = Owner.IsScrollBarPressed(horizontal);
            var enabled = Owner.IsEffectiveEnabled;
            var persistent = !settings.AutoHideScrollBars ||
                (horizontal ? Owner.HorizontalScrollBarVisibility : Owner.VerticalScrollBarVisibility) == ScrollBarVisibility.Visible;
            var reveal = persistent || activity || Owner.IsPointerWithinScrollArea || pressed || available && !state.Available;
            if (reveal) state.Hold = 2;
            else state.Hold = Math.Max(0, state.Hold - seconds);
            state.HoverTime = hovered ? state.HoverTime + seconds : 0;
            state.Contract = hovered || pressed ? .5 : Math.Max(0, state.Contract - seconds);
            var targetOpacity = available && (reveal || state.Hold > 0) ? 1d : 0d;
            var targetExpansion = persistent || pressed || hovered && (state.HoverTime >= .4 || !animate) || state.Contract > 0 && state.Expansion > 0 ? 1d : 0d;
            var opacity = Approach(state.Opacity, targetOpacity, animate ? seconds / .083 : 1);
            var expansion = Approach(state.Expansion, targetExpansion, animate && !persistent && !pressed ? seconds / .167 : 1);
            if (state.Opacity != opacity || state.Expansion != expansion || state.Hovered != hovered || state.Pressed != pressed || state.Enabled != enabled)
                InvalidateRender();
            state.Opacity = opacity; state.Expansion = expansion; state.Available = available;
            state.Hovered = hovered; state.Pressed = pressed; state.Enabled = enabled;
        }
    }

    private static double Approach(double value, double target, double step) => value < target ? Math.Min(target, value + step) : Math.Max(target, value - step);
    private static Color Fade(Color color, double opacity) => color with { A = (byte)Math.Round(color.A * Math.Clamp(opacity, 0, 1)) };

    public override void Render(IElementDrawingContext context)
    {
        var settings = Palette.InteractionPreferences.Current;
        for (var i = 0; i < 2; i++)
        {
            var horizontal = i == 1; var state = _bars[i];
            if (!Owner.TryGetScrollBarGeometry(horizontal, out var bar)) continue;
            var persistent = !settings.AutoHideScrollBars ||
                (horizontal ? Owner.HorizontalScrollBarVisibility : Owner.VerticalScrollBarVisibility) == ScrollBarVisibility.Visible;
            var opacity = persistent ? 1 : state.Opacity;
            var expansion = persistent ? 1 : state.Expansion;
            if (opacity <= 0) continue;
            var enabled = Owner.IsEffectiveEnabled && (horizontal ? Owner.ScrollableWidth : Owner.ScrollableHeight) > 0;
            if (!enabled) opacity *= .5;
            var color = enabled ? Palette.ScrollBarThumb : Palette.ScrollBarDisabled;
            if (expansion > 0)
            {
                var track = horizontal
                    ? new Rect(bar.DecreaseButton.X, bar.Track.Y, bar.Track.Width + bar.DecreaseButton.Width + bar.IncreaseButton.Width, bar.Track.Height)
                    : new Rect(bar.Track.X, bar.DecreaseButton.Y, bar.Track.Width, bar.Track.Height + bar.DecreaseButton.Height + bar.IncreaseButton.Height);
                context.FillRectangle(track, ColorOf(Fade(Palette.ScrollBarTrack, opacity * expansion)), 6);
                PaintArrow(context, bar.DecreaseButton, horizontal, false, enabled, opacity * expansion);
                PaintArrow(context, bar.IncreaseButton, horizontal, true, enabled, opacity * expansion);
            }
            if (!enabled) continue; // WinUI hides disabled thumbs, retaining muted arrow buttons.
            var width = Math.Min(2, Owner.ScrollBarThickness) + Math.Max(0, Owner.ScrollBarThickness - 2) * expansion;
            var shift = 2 * (1 - expansion);
            var thumb = horizontal
                ? new Rect(bar.Thumb.X, bar.Thumb.Y + (bar.Thumb.Height - width) / 2 + shift, bar.Thumb.Width, width)
                : new Rect(bar.Thumb.X + (bar.Thumb.Width - width) / 2 + shift, bar.Thumb.Y, width, bar.Thumb.Height);
            context.FillRectangle(thumb, ColorOf(Fade(color, opacity)), width / 2);
        }
    }

    private void PaintArrow(IElementDrawingContext context, Rect rect, bool horizontal, bool increase, bool enabled, double opacity)
    {
        if (rect.Width < 6 || rect.Height < 6) return;
        var pressed = Owner.IsScrollBarButtonPressed(horizontal, increase);
        var hovered = Owner.IsScrollBarButtonHovered(horizontal, increase);
        var color = ColorOf(Fade(!enabled ? Palette.ScrollBarDisabled : hovered || pressed ? Palette.MutedForeground : Palette.ScrollBarThumb, opacity));
        var scale = pressed ? .875 : 1;
        var direction = increase ? 1 : -1;
        // WinUI's 8pt solid caret has four pixels of padding at the outside end.
        var center = new Point(rect.X + rect.Width / 2 - (horizontal ? direction * 2 : 0), rect.Y + rect.Height / 2 - (horizontal ? 0 : direction * 2));
        var a = horizontal ? new Point(center.X - direction * 1.5 * scale, center.Y - 3 * scale) : new Point(center.X - 3 * scale, center.Y - direction * 1.5 * scale);
        var b = horizontal ? new Point(center.X + direction * 1.5 * scale, center.Y) : new Point(center.X, center.Y + direction * 1.5 * scale);
        var c = horizontal ? new Point(a.X, center.Y + 3 * scale) : new Point(center.X + 3 * scale, a.Y);
        context.Path(FormattableString.Invariant($"M {a.X},{a.Y} L {b.X},{b.Y} L {c.X},{c.Y} Z"), color, UiColor.Transparent, 0);
    }
}
