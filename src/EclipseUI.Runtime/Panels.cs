using System;
using System.Collections.Generic;

namespace EclipseUI.Elements;

[UiContent(UiContentKind.Children)]
public abstract class Panel : Element
{
    private readonly ElementCollection _children;

    protected Panel() => _children = new ElementCollection(this);

    public ElementCollection Children => _children;

    /// <summary>内边距（四边等宽）。子元素布局区域收缩该值。</summary>
    public static readonly StyledProperty<double> PaddingProperty =
        StyledProperty<double>.Register<Panel>("Padding", 0d,
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    /// <summary>是否把子内容裁剪到本元素边界（绘制侧生效，默认关）。裁剪影响像素，变更必须重绘。</summary>
    public static readonly StyledProperty<bool> ClipToBoundsProperty =
        StyledProperty<bool>.Register<Panel>("ClipToBounds", false,
            new PropertyOptions { Invalidate = InvalidateKind.Render });

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
        var padding = LayoutPadding;
        var gap = LayoutGap;
        var inner = Math.Max(0, available.Width - padding * 2);

        double width = 0, height = 0;
        int count = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(inner, double.PositiveInfinity));
            if (IsSkipped(child)) continue;
            width = Math.Max(width, child.DesiredSize.Width);
            height += child.DesiredSize.Height;
            count++;
        }
        height += Math.Max(0, count - 1) * gap;
        return new Size(width + padding * 2, height + padding * 2);
    }

    // 约定：子元素的 FinalRect 位于父元素的局部坐标系（ arrange 传局部 rect）
    protected internal override void ArrangeCore(Rect final)
    {
        var padding = LayoutPadding;
        var gap = LayoutGap;
        var innerWidth = Math.Max(0, final.Width - padding * 2);
        double y = padding;
        bool first = true;
        foreach (var child in Children)
        {
            if (IsSkipped(child))
            {
                // 不占间距槽，但仍要 arrange：否则 FinalRect 残留上一轮有内容时的位置，
                // 命中测试与脏区会读到过期矩形。
                child.Arrange(new Rect(padding, y, 0, 0));
                continue;
            }
            if (!first) y += gap;
            first = false;
            child.Arrange(new Rect(padding, y, innerWidth, child.DesiredSize.Height));
            y += child.DesiredSize.Height;
        }
    }

    // 布局透明节点（块协调器）不引入自己的内边距/间距：条目按最近布局祖先的 Gap 排布，
    // 等价于把条目摊进父容器（spec/01 §4.10/§4.11，D77）。
    private double LayoutPadding => IsLayoutTransparent ? 0 : Padding;

    private double LayoutGap
    {
        get
        {
            if (!IsLayoutTransparent) return Gap;
            for (var node = Parent; node is not null; node = node.Parent)
            {
                if (node.IsLayoutTransparent) continue;
                return node is StackPanelBase panel ? panel.Gap : 0;
            }
            return 0;
        }
    }

    /// <summary>空的透明块整体退出布局：不贡献尺寸，也不消耗父容器的间距槽（嵌套空块同样递归为零）。</summary>
    private static bool IsSkipped(Element child) =>
        child.IsLayoutTransparent && child.DesiredSize.Width == 0 && child.DesiredSize.Height == 0;
}

public class StackPanel : StackPanelBase { }

public class ListBoxItem : StackPanelBase
{
    /// <summary>列表项默认内边距：行内文字不贴边（样式系统落地前的最小视觉约定）。</summary>
    public ListBoxItem() => Padding = 8;
}
