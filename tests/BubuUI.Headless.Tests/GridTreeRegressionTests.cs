using System.Collections.ObjectModel;
using BubuUI.Elements;
using BubuUI.Themes.Fluent;
using Xunit;

namespace BubuUI.Headless.Tests;

public class GridTreeRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DataGridKeyboardNavigationShowsRowsUsingTheirMeasuredHeights(bool variable)
    {
        using var grid = new DataGrid { Width = 400, Height = 180, ItemsSource = Enumerable.Range(0, 100).ToArray() };
        grid.Columns.Add(new() { Header = "Value", CellTemplate = item => new Border { Height = variable ? 20 + (int)item! % 3 * 40 : 80 } });
        grid.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(grid);
        Settle(host);
        host.Focus.Focus(grid, FocusOrigin.Keyboard);
        foreach (var (key, selected) in new[] { (Key.End, 99), (Key.Up, 98), (Key.Home, 0), (Key.Down, 1) })
        {
            host.KeyPress(key);
            Settle(host);
            Assert.Equal(selected, grid.SelectedIndex);
            Assert.Same(grid, host.Focus.Focused);
            var row = Assert.IsType<ListBoxItem>(grid.RowsHost.GetRealizedItem(selected));
            var viewer = Assert.Single(grid.Descendants().OfType<ScrollViewer>());
            var top = viewer.PointFromRoot(Point.Zero).Y - row.PointFromRoot(Point.Zero).Y;
            Assert.InRange(top, viewer.Padding - .001, viewer.Padding + viewer.ViewportHeight);
            Assert.True(top + row.FinalRect.Height <= viewer.Padding + viewer.ViewportHeight + .001);
            Assert.InRange(grid.RealizedRowCount, 1, 15);
        }
    }

    [Fact]
    public void VirtualPanelCanBringAnUnmeasuredItemIntoViewAndHonorsLaterUserScroll()
    {
        var panel = new VirtualizingStackPanel { ItemCount = 100, IsVariableHeight = true, ItemFactory = index => new Border { Height = index == 99 ? 80 : 20 } };
        using var viewer = new ScrollViewer { Height = 100, Width = 200 };
        viewer.Children.Add(panel);
        panel.BringIndexIntoView(99);
        using var host = new HeadlessRoot(viewer);
        Settle(host);
        var last = Assert.IsType<Border>(panel.GetRealizedItem(99));
        var top = viewer.PointFromRoot(Point.Zero).Y - last.PointFromRoot(Point.Zero).Y;
        Assert.True(top >= -.001 && top + last.FinalRect.Height <= viewer.ViewportHeight + .001);

        panel.BringIndexIntoView(0);
        viewer.ScrollToEnd();
        Settle(host);
        Assert.NotNull(panel.GetRealizedItem(99));
        Assert.Null(panel.GetRealizedItem(0));
    }

    [Fact]
    public void VirtualPanelBoundsIncludeExactHeightProviderPaddingAndGap()
    {
        var panel = new VirtualizingStackPanel { ItemCount = 100, Padding = 3, Gap = 2, ItemHeightProvider = index => index + 10,
            ItemFactory = index => new Border { Height = index + 10 } };
        using var viewer = new ScrollViewer { Height = 120, Width = 200 };
        viewer.Children.Add(panel);
        using var host = new HeadlessRoot(viewer); Settle(host);
        var bounds = panel.GetItemBounds(3);
        Assert.Equal(3 + 10 + 11 + 12 + 3 * 2, bounds.Y, 6);
        Assert.Equal(13, bounds.Height, 6);
        Assert.Throws<ArgumentOutOfRangeException>(() => panel.GetItemBounds(100));
        Assert.Throws<ArgumentOutOfRangeException>(() => panel.BringIndexIntoView(-1));
    }

    [Fact]
    public void ClearingTreeDataDisposesGeneratedNodesClearsSelectionAndUnsubscribes()
    {
        var source = new ObservableCollection<string> { "one", "two" };
        using var tree = new TreeView { ItemsSource = source };
        var generated = tree.Children.Cast<TreeViewItem>().ToArray();
        tree.SelectedItem = generated[1];
        var changed = 0; tree.SelectedItemChanged = _ => changed++;
        tree.ItemsSource = null;
        Assert.Empty(tree.Children);
        Assert.Null(tree.SelectedItem);
        Assert.Equal(1, changed);
        Assert.All(generated, item => Assert.True(item.IsDisposed));
        source.Add("third");
        Assert.Empty(tree.Children);
    }

    [Fact]
    public void TreeWithoutDataSourceKeepsCallerOwnedNodesWhenRefreshedOrCleared()
    {
        using var tree = new TreeView();
        var manual = new TreeViewItem { Header = "manual" };
        tree.Children.Add(manual); tree.SelectedItem = manual;
        tree.RefreshItems(); tree.ItemsSource = null;
        Assert.Same(manual, Assert.Single(tree.Children));
        Assert.Same(manual, tree.SelectedItem);
        Assert.False(manual.IsDisposed);
    }

    [Fact]
    public void ClearingTreeDataKeepsManualNodesAddedAlongsideGeneratedNodes()
    {
        using var tree = new TreeView { ItemsSource = new[] { "generated" } };
        var manual = new TreeViewItem { Header = "manual" };
        tree.Children.Add(manual); tree.SelectedItem = manual;
        tree.ItemsSource = null;
        Assert.Same(manual, Assert.Single(tree.Children));
        Assert.Same(manual, tree.SelectedItem);
        Assert.False(manual.IsDisposed);
    }

    private static void Settle(HeadlessRoot host) { for (var i = 0; i < 6; i++) host.UpdateLayout(); }
}
