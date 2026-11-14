namespace EclipseUI.Elements;

internal static class ControlIcons
{
    internal static void Chevron(IElementDrawingContext c, Point center, bool up = false, bool right = false, double size = 4)
    {
        if (right)
        {
            c.Line(new(center.X - size / 2, center.Y - size), new(center.X + size / 2, center.Y), c.Foreground, 1.5);
            c.Line(new(center.X + size / 2, center.Y), new(center.X - size / 2, center.Y + size), c.Foreground, 1.5);
        }
        else
        {
            var y = up ? -size / 2 : size / 2;
            c.Line(new(center.X - size, center.Y - y), new(center.X, center.Y + y), c.Foreground, 1.5);
            c.Line(new(center.X, center.Y + y), new(center.X + size, center.Y - y), c.Foreground, 1.5);
        }
    }
}

internal class DropDownButton : Button
{
    public override Rect GetLabelBounds() => new(HorizontalPadding, VerticalPadding,
        Math.Max(0, FinalRect.Width - HorizontalPadding * 2 - 16), Math.Max(0, FinalRect.Height - VerticalPadding * 2));
    public override void Render(IElementDrawingContext c) => ControlIcons.Chevron(c, new(FinalRect.Width - 12, FinalRect.Height / 2));
    protected internal override Size MeasureCore(Size available) { var size = base.MeasureCore(available); return new(size.Width + 16, size.Height); }
}
