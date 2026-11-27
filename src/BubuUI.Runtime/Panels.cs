using System;
using System.Collections.Generic;

namespace BubuUI.Elements;

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

    public override IReadOnlyList<Element> VisualChildren => TemplateRoot is not null
        ? base.VisualChildren : _children.IsProjected ? Array.Empty<Element>() : _children;

    protected override void OnDispose()
    {
        foreach (var child in _children.ToArray()) if (!child.IsDisposed) child.Dispose();
        base.OnDispose();
    }
}

/// <summary>垂直堆叠布局（StackPanel 与块协调器共用）。</summary>
public abstract class StackPanelBase : Panel
{
    public static readonly StyledProperty<Orientation> OrientationProperty = StyledProperty<Orientation>.Register<StackPanelBase>("Orientation", Orientation.Vertical, new() { Invalidate = InvalidateKind.Measure });
    public Orientation Orientation { get => GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    private bool Horizontal
    {
        get
        {
            if (!IsLayoutTransparent) return Orientation == Orientation.Horizontal;
            for (var parent = Parent; parent is not null; parent = parent.Parent)
                if (!parent.IsLayoutTransparent) return parent is StackPanelBase panel && panel.Horizontal;
            return false;
        }
    }
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
            child.Measure(Horizontal ? new Size(double.PositiveInfinity, Math.Max(0, available.Height - padding * 2)) : new Size(inner, double.PositiveInfinity));
            if (IsSkipped(child)) continue;
            if (Horizontal) { width += child.DesiredSize.Width; height = Math.Max(height, child.DesiredSize.Height); }
            else { width = Math.Max(width, child.DesiredSize.Width); height += child.DesiredSize.Height; }
            count++;
        }
        if (Horizontal) width += Math.Max(0, count - 1) * gap; else height += Math.Max(0, count - 1) * gap;
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
            // DesiredSize 已受横向约束；局部显式尺寸溢出由 Element.Arrange 处理。
            child.Arrange(Horizontal
                ? new Rect(y, padding, child.DesiredSize.Width, Math.Max(0, final.Height - padding * 2))
                : new Rect(padding, y, Math.Max(innerWidth, child.DesiredSize.Width), child.DesiredSize.Height));
            y += Horizontal ? child.DesiredSize.Width : child.DesiredSize.Height;
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
        !child.IsVisible || child is Popup || child.IsLayoutTransparent && child.DesiredSize.Width == 0 && child.DesiredSize.Height == 0;
}

public class StackPanel : StackPanelBase { }

public class ListBoxItem : StackPanelBase
{
    public override Rect? GetContentClip() => new Rect(Padding, Padding, Math.Max(0, FinalRect.Width - Padding * 2), Math.Max(0, FinalRect.Height - Padding * 2));
    public static readonly StyledProperty<bool> IsSelectedProperty = StyledProperty<bool>.Register<ListBoxItem>("IsSelected", false, new() { Invalidate = InvalidateKind.Render });
    public bool IsSelected { get => GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
    public override bool IsFocusable => true;
    public object? Item { get; internal set; }

    /// <summary>列表项默认内边距：行内文字不贴边（样式系统落地前的最小视觉约定）。</summary>
    public ListBoxItem() { IsTabStop = false; }
}

/// <summary>流式换行方向。</summary>
public enum Orientation
{
    Horizontal,
    Vertical,
}

/// <summary>
/// 流式布局（G4 布局补全第一片）：子元素按期望尺寸沿主轴排列，超出可用空间即换行（换列）。
/// 水平方向逐行从上往下；行内不拉伸子元素（保持 DesiredSize），行高 = 行内最大子高。
/// </summary>
public class WrapPanel : Panel
{
    public static readonly StyledProperty<Orientation> OrientationProperty =
        StyledProperty<Orientation>.Register<WrapPanel>("Orientation", Orientation.Horizontal,
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    /// <summary>与 StackPanel.Gap 同义的条目间距（主轴/换行方向共用一个值）。</summary>
    public static readonly StyledProperty<double> GapProperty =
        StyledProperty<double>.Register<WrapPanel>("Gap", 0d,
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    private double LayoutPadding => IsLayoutTransparent ? 0 : Padding;

    protected internal override Size MeasureCore(Size available)
    {
        var padding = LayoutPadding;
        var gap = Gap;
        var innerWidth = Math.Max(0, available.Width - padding * 2);
        var innerHeight = Math.Max(0, available.Height - padding * 2);

        // 先量所有孩子（主轴给足约束让它量出期望，交叉轴给可用约束），再按主轴装箱。
        var constraints = Orientation == Orientation.Horizontal
            ? new Size(double.PositiveInfinity, innerHeight)
            : new Size(innerWidth, double.PositiveInfinity);

        var wrapLimit = Orientation == Orientation.Horizontal ? innerWidth : innerHeight;
        double maxMain = 0;         // 各行主轴占用的最大值（期望尺寸取最长行）
        double lineMain = 0;        // 当前行主轴已占用
        double lineCross = 0;       // 当前行交叉轴最大尺寸
        double totalCross = 0;      // 换行方向累计
        bool lineHasItem = false;

        foreach (var child in Children)
        {
            child.Measure(constraints);
            var size = child.DesiredSize;

            var childMain = Orientation == Orientation.Horizontal ? size.Width : size.Height;
            var childCross = Orientation == Orientation.Horizontal ? size.Height : size.Width;

            if (lineHasItem && lineMain + gap + childMain > wrapLimit)
            {
                // 装不下：结账当前行，另起一行
                totalCross += lineCross + gap;
                maxMain = Math.Max(maxMain, lineMain);
                lineMain = 0;
                lineCross = 0;
                lineHasItem = false;
            }

            lineMain += (lineHasItem ? gap : 0) + childMain;
            lineCross = Math.Max(lineCross, childCross);
            lineHasItem = true;
        }

        maxMain = Math.Max(maxMain, lineMain); // 最后一行
        totalCross += lineCross;

        return Orientation == Orientation.Horizontal
            ? new Size(maxMain + padding * 2, totalCross + padding * 2)
            : new Size(totalCross + padding * 2, maxMain + padding * 2);
    }

    protected internal override void ArrangeCore(Rect final)
    {
        var padding = LayoutPadding;
        var gap = Gap;
        var innerWidth = Math.Max(0, final.Width - padding * 2);
        var innerHeight = Math.Max(0, final.Height - padding * 2);

        double lineMain = padding;  // 当前行主轴游标（局部坐标）
        double lineCrossStart = padding; // 当前行交叉轴起点
        double lineCross = 0;
        bool lineHasItem = false;

        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            var childMain = Orientation == Orientation.Horizontal ? size.Width : size.Height;
            var childCross = Orientation == Orientation.Horizontal ? size.Height : size.Width;

            if (lineHasItem && lineMain - padding + gap + childMain > (Orientation == Orientation.Horizontal ? innerWidth : innerHeight))
            {
                lineMain = padding;
                lineCrossStart += lineCross + gap;
                lineCross = 0;
                lineHasItem = false;
            }

            if (!lineHasItem)
                lineMain = padding;
            else
                lineMain += gap;
            lineHasItem = true;

            if (Orientation == Orientation.Horizontal)
            {
                child.Arrange(new Rect(lineMain, lineCrossStart, size.Width, size.Height));
                lineMain += size.Width;
            }
            else
            {
                child.Arrange(new Rect(lineCrossStart, lineMain, size.Width, size.Height));
                lineMain += size.Height;
            }
            lineCross = Math.Max(lineCross, childCross);
        }
    }
}
