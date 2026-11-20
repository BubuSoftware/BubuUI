using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class ControlLayoutTests
{
    [Fact]
    public void HorizontalStackPreservesGapAndCollapsedChildrenDoNotConsumeSpace()
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Gap = 5 };
        var a = new Border { Width = 20, Height = 10 }; var hidden = new Border { Width = 50, Height = 50, IsVisible = false }; var b = new Border { Width = 30, Height = 12 };
        stack.Children.Add(a); stack.Children.Add(hidden); stack.Children.Add(b);
        using var host = new HeadlessRoot(stack); host.UpdateLayout(); Assert.Equal(55, stack.DesiredSize.Width); Assert.Equal(25, b.FinalRect.X); Assert.Equal(12, stack.DesiredSize.Height);
    }
    [Fact]
    public void GridSpansIncludeIntermediateTracksAndGaps()
    {
        var grid = new Grid { ColumnDefinitions = "40,60,80", RowDefinitions = "20,30", Gap = 4 };
        var child = new Border { BorderThickness = 0 }; Grid.SetColumnSpan(child, 2); Grid.SetRowSpan(child, 2); grid.Children.Add(child);
        using var host = new HeadlessRoot(grid); host.UpdateLayout(); Assert.Equal(104, child.FinalRect.Width); Assert.Equal(54, child.FinalRect.Height);
    }
    [Fact]
    public void GridSpanningAutoTracksMeasureTheWholeChild()
    {
        var grid = new Grid { ColumnDefinitions = "Auto,Auto" }; var child = new Border { Width = 100, Height = 20 };
        Grid.SetColumnSpan(child, 2); grid.Children.Add(child); using var host = new HeadlessRoot(grid); host.UpdateLayout(); Assert.Equal(100, grid.DesiredSize.Width); Assert.Equal(100, child.FinalRect.Width);
    }
    [Fact]
    public void CanvasPositionsChildrenAndAttachedChangesInvalidateLayout()
    {
        var canvas = new Canvas(); var child = new Border { Width = 20, Height = 30 }; Canvas.SetLeft(child, 10); Canvas.SetTop(child, 5); canvas.Children.Add(child);
        using var host = new HeadlessRoot(canvas); host.UpdateLayout(); Assert.Equal(30, canvas.DesiredSize.Width); Assert.Equal(5, child.FinalRect.Y);
        Canvas.SetLeft(child, 50); host.UpdateLayout(); Assert.Equal(50, child.FinalRect.X); Assert.Equal(70, canvas.DesiredSize.Width);
    }
    [Fact]
    public void DockPanelReservesEdgesAndFillsRemainingSpace()
    {
        var dock = new DockPanel { Width = 200, Height = 100 }; var left = new Border { Width = 40 }; var top = new Border { Height = 20 }; var fill = new Border();
        DockPanel.SetDock(top, Dock.Top); dock.Children.Add(left); dock.Children.Add(top); dock.Children.Add(fill);
        using var host = new HeadlessRoot(dock); host.UpdateLayout(); Assert.Equal(new Rect(40, 20, 160, 80), fill.FinalRect);
    }
    [Fact]
    public void ButtonContentParticipatesInRealTreeAndLayout()
    {
        var content = new Border { Width = 40, Height = 20 }; var button = new Button { Content = content, HorizontalPadding = 12, VerticalPadding = 6 };
        using var host = new HeadlessRoot(button); host.UpdateLayout(); Assert.Same(button, content.Parent); Assert.Equal(64, button.DesiredSize.Width); Assert.Equal(new Rect(12, 6, 40, 20), content.FinalRect);
    }
    [Fact]
    public void ScrollBarOwnsHitTestingAndDraggingChangesOffset()
    {
        var viewer = new ScrollViewer { Width = 100, Height = 100, ScrollBarInset = 4, ScrollBarThickness = 6, ScrollBarHitThickness = 14 }; viewer.Children.Add(new Border { Height = 1000 });
        using var host = new HeadlessRoot(viewer); host.UpdateLayout(); Assert.Same(viewer, host.HitTest(new(94, 8)));
        host.PointerPress(new(94, 8)); host.PointerMove(new(94, 50)); host.PointerRelease(new(94, 50)); host.UpdateLayout(); Assert.InRange(viewer.VerticalOffset, 400, 600);
    }
    [Fact]
    public void PopupOverflowsClippingAncestorButIsHitTestedAboveNormalContent()
    {
        var root = new Grid { Width = 300, Height = 200 }; var owner = new StackPanel { Width = 60, Height = 30, ClipToBounds = true };
        var body = new Button { Label = "popup", Width = 120 }; var popup = new Popup { Content = body, PlacementTarget = owner, IsOpen = true };
        owner.Children.Add(popup); root.Children.Add(owner); using var host = new HeadlessRoot(root); host.UpdateLayout();
        var point = body.PointFromRoot(Point.Zero); var click = new Point(-point.X + 80, -point.Y + 10);
        Assert.Same(body, host.HitTest(click)); host.PointerPress(new(250, 150)); Assert.False(popup.IsOpen);
    }
    [Fact]
    public void WindowPopupTemplateFallsBackToInteractiveClampedOverlayInHeadlessHost()
    {
        using var page = new WindowPopupPage();
        using var host = new HeadlessRoot(page) { AvailableSize = new(160, 100) };
        host.UpdateLayout();
        Assert.Equal(PopupHostMode.Window, page.Details.HostMode);
        Assert.False(page.Details.IsWindowHosted);
        Assert.Equal(new Rect(60, 60, 100, 40), page.Details.Content!.FinalRect);
        Assert.Same(page.Details.Content, host.HitTest(new(70, 70)));
        host.KeyPress(Key.Escape);
        Assert.False(page.Details.IsOpen);
    }
    [Fact]
    public void EscapeClosesTopPopupAndClosedChildrenCannotReceiveFocus()
    {
        var root = new Grid { Width = 300, Height = 200 }; var editor = new TextBox(); var popup = new Popup { IsOpen = true, Content = editor }; root.Children.Add(popup);
        using var host = new HeadlessRoot(root); host.UpdateLayout(); Assert.True(host.Focus.Focus(editor)); host.KeyPress(Key.Escape); host.UpdateLayout(); Assert.False(popup.IsOpen); Assert.Null(host.Focus.Focused); Assert.False(host.Focus.Focus(editor));
    }
    [Fact]
    public void VariableHeightVirtualizationUsesExactProviderOffsets()
    {
        var panel = new VirtualizingStackPanel { ItemCount = 1000, ItemHeightProvider = i => i % 2 == 0 ? 20 : 40, ItemFactory = i => new Border { Height = i % 2 == 0 ? 20 : 40 } };
        var scroll = new ScrollViewer { Width = 200, Height = 100 }; scroll.Children.Add(panel);
        using var host = new HeadlessRoot(scroll); host.UpdateLayout(); Assert.Equal(30000, scroll.ExtentHeight); Assert.InRange(panel.RealizedCount, 3, 10);
        scroll.ScrollToVerticalOffset(300); host.UpdateLayout(); Assert.Equal(260, panel.Children[0].FinalRect.Y); Assert.Equal(40, panel.Children[0].FinalRect.Height);
    }
    [Fact]
    public void RecyclingRebindsDetachedContainersAndRefreshDisposesPool()
    {
        var created = new List<Border>(); var updates = new List<int>();
        var panel = new VirtualizingStackPanel { ItemCount = 1000, ItemFactory = i => { var row = new Border { Height = 20 }; created.Add(row); return row; }, ItemUpdater = (row, i) => updates.Add(i) };
        var scroll = new ScrollViewer { Width = 200, Height = 100 }; scroll.Children.Add(panel);
        using var host = new HeadlessRoot(scroll); host.UpdateLayout(); var initial = created.Count; scroll.ScrollToVerticalOffset(400); host.UpdateLayout();
        Assert.NotEmpty(updates); Assert.InRange(created.Count, initial, initial + 2); panel.Refresh(); Assert.All(created, row => Assert.True(row.IsDisposed));
    }
}
