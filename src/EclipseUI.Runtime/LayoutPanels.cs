namespace EclipseUI.Elements;

public class Canvas : Panel
{
    public static readonly AttachedProperty<double> LeftProperty = new("Left", typeof(Canvas));
    public static readonly AttachedProperty<double> TopProperty = new("Top", typeof(Canvas));
    public static double GetLeft(Element element) => LeftProperty.GetValue(element);
    public static double GetTop(Element element) => TopProperty.GetValue(element);
    public static void SetLeft(Element element, double value) => LeftProperty.SetValue(element, value);
    public static void SetTop(Element element, double value) => TopProperty.SetValue(element, value);
    protected internal override Size MeasureCore(Size available)
    {
        double width = 0, height = 0;
        foreach (var child in Children)
        {
            child.Measure(Size.Infinity);
            if (!child.IsVisible || child is Popup) continue;
            width = Math.Max(width, GetLeft(child) + child.DesiredSize.Width); height = Math.Max(height, GetTop(child) + child.DesiredSize.Height);
        }
        return new(Math.Max(0, width) + Padding * 2, Math.Max(0, height) + Padding * 2);
    }
    protected internal override void ArrangeCore(Rect final)
    {
        foreach (var child in Children) child.Arrange(new(Padding + GetLeft(child), Padding + GetTop(child), child.DesiredSize.Width, child.DesiredSize.Height));
    }
}

public enum Dock { Left, Top, Right, Bottom }
public class DockPanel : Panel
{
    public static readonly AttachedProperty<Dock> DockProperty = new("Dock", typeof(DockPanel));
    public static Dock GetDock(Element element) => DockProperty.GetValue(element);
    public static void SetDock(Element element, Dock value) => DockProperty.SetValue(element, value);
    public static readonly StyledProperty<bool> LastChildFillProperty = StyledProperty<bool>.Register<DockPanel>("LastChildFill", true, new() { Invalidate = InvalidateKind.Measure });
    public bool LastChildFill { get => GetValue(LastChildFillProperty); set => SetValue(LastChildFillProperty, value); }
    protected internal override Size MeasureCore(Size available)
    {
        double usedWidth = 0, usedHeight = 0, width = 0, height = 0;
        foreach (var child in Children)
        {
            child.Measure(new(Math.Max(0, available.Width - Padding * 2 - usedWidth), Math.Max(0, available.Height - Padding * 2 - usedHeight)));
            if (!child.IsVisible || child is Popup) continue;
            if (GetDock(child) is Dock.Left or Dock.Right)
            { height = Math.Max(height, usedHeight + child.DesiredSize.Height); usedWidth += child.DesiredSize.Width; }
            else { width = Math.Max(width, usedWidth + child.DesiredSize.Width); usedHeight += child.DesiredSize.Height; }
        }
        return new(Math.Max(width, usedWidth) + Padding * 2, Math.Max(height, usedHeight) + Padding * 2);
    }
    protected internal override void ArrangeCore(Rect final)
    {
        double x = Padding, y = Padding, w = Math.Max(0, final.Width - Padding * 2), h = Math.Max(0, final.Height - Padding * 2);
        var active = Children.Where(c => c.IsVisible && c is not Popup).ToArray();
        foreach (var child in Children)
        {
            if (!child.IsVisible || child is Popup) { child.Arrange(new(x, y, 0, 0)); continue; }
            if (LastChildFill && ReferenceEquals(child, active.LastOrDefault())) { child.Arrange(new(x, y, w, h)); continue; }
            var cw = Math.Min(w, child.DesiredSize.Width); var ch = Math.Min(h, child.DesiredSize.Height);
            switch (GetDock(child))
            {
                case Dock.Left: child.Arrange(new(x, y, cw, h)); x += cw; w -= cw; break;
                case Dock.Right: child.Arrange(new(x + w - cw, y, cw, h)); w -= cw; break;
                case Dock.Top: child.Arrange(new(x, y, w, ch)); y += ch; h -= ch; break;
                case Dock.Bottom: child.Arrange(new(x, y + h - ch, w, ch)); h -= ch; break;
            }
        }
    }
}
