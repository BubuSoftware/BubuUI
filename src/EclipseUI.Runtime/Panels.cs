using System;
using System.Collections.Generic;

namespace EclipseUI.Elements;

public abstract class Panel : Element
{
    private readonly ElementCollection _children;

    protected Panel() => _children = new ElementCollection(this);

    public ElementCollection Children => _children;

    internal override IEnumerable<Element> VisualChildren => _children;
}

/// <summary>垂直堆叠布局（StackPanel 与块协调器共用）。</summary>
public abstract class StackPanelBase : Panel
{
    public static readonly StyledProperty<double> GapProperty =
        StyledProperty<double>.Register<StackPanelBase>("Gap", 0d,
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    protected internal override Size MeasureCore(Size available)
    {
        double width = 0, height = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(available.Width, double.PositiveInfinity));
            width = Math.Max(width, child.DesiredSize.Width);
            height += child.DesiredSize.Height;
        }
        height += Math.Max(0, Children.Count - 1) * Gap;
        return new Size(width, height);
    }

    // 约定：子元素的 FinalRect 位于父元素的局部坐标系（ arrange 传局部 rect）
    protected internal override void ArrangeCore(Rect final)
    {
        double y = 0;
        bool first = true;
        foreach (var child in Children)
        {
            if (!first) y += Gap;
            first = false;
            child.Arrange(new Rect(0, y, final.Width, child.DesiredSize.Height));
            y += child.DesiredSize.Height;
        }
    }
}

public class StackPanel : StackPanelBase { }

public class ListBoxItem : StackPanelBase { }
