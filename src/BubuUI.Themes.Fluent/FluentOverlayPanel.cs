using BubuUI.Elements;

namespace BubuUI.Themes.Fluent;

/// <summary>Sizes template layers to their content, then stretches them over the final viewport.</summary>
public class FluentOverlayPanel : Panel
{
    protected override Size MeasureCore(Size available)
    {
        double width = 0, height = 0;
        foreach (var child in Children)
        {
            child.Measure(available);
            width = Math.Max(width, child.DesiredSize.Width);
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new(width, height);
    }

    protected override void ArrangeCore(Rect final)
    {
        foreach (var child in Children)
            child.Arrange(new(Point.Zero, final.Size));
    }
}
