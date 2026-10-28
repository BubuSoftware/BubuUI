using System;
using System.Collections.Generic;

namespace EclipseUI.Elements;

public abstract class Panel : Element
{
    private readonly ElementCollection _children;

    protected Panel() => _children = new ElementCollection(this);

    public ElementCollection Children => _children;

    /// <summary>内边距（四边等宽）。子元素布局区域收缩该值。</summary>
    public static readonly StyledProperty<double> PaddingProperty =
        StyledProperty<double>.Register<Panel>("Padding", 0d,
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    /// <summary>是否把子内容裁剪到本元素边界（绘制侧生效，默认关）。</summary>
    public static readonly StyledProperty<bool> ClipToBoundsProperty =
        StyledProperty<bool>.Register<Panel>("ClipToBounds", false);

    public double Padding
    {
        get => GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, Math.Max(0, value));
    }

    public bool ClipToBounds
    {
        get => GetValue(ClipToBoundsProperty);
        set => SetValue(ClipToBoundsProperty, value);
    }

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
        var padding = Padding;
        var inner = Math.Max(0, available.Width - padding * 2);

        double width = 0, height = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(inner, double.PositiveInfinity));
            width = Math.Max(width, child.DesiredSize.Width);
            height += child.DesiredSize.Height;
        }
        height += Math.Max(0, Children.Count - 1) * Gap;
        return new Size(width + padding * 2, height + padding * 2);
    }

    // 约定：子元素的 FinalRect 位于父元素的局部坐标系（ arrange 传局部 rect）
    protected internal override void ArrangeCore(Rect final)
    {
        var padding = Padding;
        var innerWidth = Math.Max(0, final.Width - padding * 2);
        double y = padding;
        bool first = true;
        foreach (var child in Children)
        {
            if (!first) y += Gap;
            first = false;
            child.Arrange(new Rect(padding, y, innerWidth, child.DesiredSize.Height));
            y += child.DesiredSize.Height;
        }
    }
}

public class StackPanel : StackPanelBase { }

public class ListBoxItem : StackPanelBase
{
    /// <summary>列表项默认内边距：行内文字不贴边（样式系统落地前的最小视觉约定）。</summary>
    public ListBoxItem() => Padding = 8;
}
