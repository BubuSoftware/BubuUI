using System.Collections.ObjectModel;
using EclipseUI.Elements;
using EclipseUI.Themes.Fluent;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class ListVirtualizationTests
{
    private static void Layout(HeadlessRoot host) { for (var i = 0; i < 4; i++) host.UpdateLayout(); }

    [Fact]
    public void LargeListCreatesOnlyViewportRowsAndKeyboardSelectionReachesLastItem()
    {
        var created = 0;
        using var root = new ScrollViewer { Width = 260, Height = 200 };
        var list = new ListBox
        {
            ItemsSource = Enumerable.Range(0, 10_000).ToArray(),
            ItemTemplate = item => { created++; return new Border { Height = 24 }; }
        };
        root.Children.Add(list); root.SetThemeDefaults(FluentTheme.Light);
        Assert.True(list.IsVirtualizing);
        Assert.Equal(0, created);
        using var host = new HeadlessRoot(root); Layout(host);
        Assert.InRange(created, 1, 30);
        Assert.InRange(list.RealizedItemCount, 1, 30);
        host.Focus.Focus(list); host.KeyPress(Key.End); Layout(host);
        Assert.Equal(9_999, list.SelectedIndex);
        Assert.Equal(9_999, list.SelectedItem);
        var last = Assert.IsAssignableFrom<ListBoxItem>(list.ContainerFromIndex(9_999));
        Assert.True(last.IsSelected);
        var top = root.PointFromRoot(Point.Zero).Y - last.PointFromRoot(Point.Zero).Y;
        Assert.InRange(top, 0, root.ViewportHeight - last.FinalRect.Height + .001);
        Assert.InRange(list.RealizedItemCount, 1, 30);
        host.KeyPress(Key.Home); Layout(host);
        Assert.Equal(0, list.SelectedIndex);
        Assert.Equal(0, root.VerticalOffset, 6);
    }

    [Fact]
    public void LargeComboBoxRemainsLazyAndPointerSelectionClosesItsPopup()
    {
        var created = 0;
        using var root = new Canvas { Width = 500, Height = 400 };
        var combo = new ComboBox
        {
            Width = 220, Height = 32, ItemsSource = Enumerable.Range(0, 10_000).ToArray(),
            ItemTemplate = _ => { created++; return new Border { Height = 24 }; }
        };
        root.Children.Add(combo); root.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(root); Layout(host);
        Assert.Equal(0, created);
        combo.IsDropDownOpen = true; Layout(host);
        var list = Assert.Single(combo.Descendants().OfType<ListBox>());
        Assert.InRange(created, 1, 30);
        host.KeyPress(Key.End); Layout(host);
        var last = Assert.IsAssignableFrom<ListBoxItem>(list.ContainerFromIndex(9_999));
        var origin = last.PointFromRoot(Point.Zero);
        var point = new Point(last.FinalRect.Width / 2 - origin.X, last.FinalRect.Height / 2 - origin.Y);
        host.PointerPress(point); host.PointerRelease(point); Layout(host);
        Assert.Equal(9_999, combo.SelectedItem);
        Assert.False(combo.IsDropDownOpen);
        Assert.InRange(list.RealizedItemCount, 1, 30);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(10_000)]
    public void OpeningComboBoxShowsItsInitiallySelectedLastItem(int count)
    {
        using var root = new Canvas { Width = 500, Height = 400 };
        var combo = new ComboBox { Width = 220, Height = 32, ItemsSource = Enumerable.Range(0, count).ToArray(), SelectedIndex = count - 1 };
        root.Children.Add(combo); root.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(root); Layout(host);
        combo.IsDropDownOpen = true; Layout(host);
        var list = Assert.Single(combo.Descendants().OfType<ListBox>());
        var viewer = Assert.Single(combo.Descendants().OfType<ScrollViewer>());
        var last = Assert.IsAssignableFrom<ListBoxItem>(list.ContainerFromIndex(count - 1));
        Assert.True(last.IsSelected);
        var top = viewer.PointFromRoot(Point.Zero).Y - last.PointFromRoot(Point.Zero).Y;
        Assert.InRange(top, viewer.Padding - .001, viewer.Padding + viewer.ViewportHeight - last.FinalRect.Height + .001);
    }

    [Fact]
    public void VirtualRowsRetireAndCollectionChangesPreserveTheSelectedDataItem()
    {
        var data = new ObservableCollection<object?>(Enumerable.Range(0, 500).Select(i => (object?)new Row(i)));
        var selected = data[499];
        using var root = new ScrollViewer { Width = 260, Height = 200 };
        var list = new ListBox { ItemsSource = data, ItemTemplate = _ => new Border { Height = 24 } };
        root.Children.Add(list); root.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(root); Layout(host);
        var retired = list.ContainerFromIndex(1)!;
        list.Select(499); Layout(host);
        Assert.True(retired.IsDisposed);
        data.RemoveAt(0); Layout(host);
        Assert.Same(selected, list.SelectedItem);
        Assert.Equal(498, list.SelectedIndex);
        list.BringItemIntoView(498); Layout(host);
        Assert.True(((ListBoxItem)list.ContainerFromIndex(498)!).IsSelected);
        list.Template = new FluentItemsControlTemplate(); Layout(host);
        Assert.Same(selected, list.SelectedItem);
        Assert.InRange(list.RealizedItemCount, 1, 30);
    }

    [Fact]
    public void ElementItemsAndSmallListsKeepTheirExistingContainerContract()
    {
        var elements = Enumerable.Range(0, 150).Select(_ => new ListBoxItem { Height = 20 }).ToArray();
        using var list = new ListBox { ItemsSource = elements };
        Assert.False(list.IsVirtualizing);
        Assert.Equal(elements.Length, list.Children.Count);
        Assert.Same(elements[100], list.Children[100]);
        using var small = new ListBox { ItemsSource = new[] { "one", "two" } };
        Assert.False(small.IsVirtualizing);
        Assert.Equal(2, small.Children.Count);
    }

    [Fact]
    public void ChangingLayoutOptionsDoesNotClearCallerAuthoredChildren()
    {
        using var list = new ListBox();
        var first = new ListBoxItem(); var second = new ListBoxItem();
        list.Children.Add(first); list.Children.Add(second); list.SelectedIndex = 1;
        list.Orientation = Orientation.Horizontal;
        list.IsVirtualizationEnabled = false;
        list.IsVirtualizationEnabled = true;
        Assert.Equal(2, list.Children.Count);
        Assert.Same(first, list.Children[0]); Assert.Same(second, list.Children[1]);
        Assert.Same(second, list.SelectedItem);
        Assert.False(first.IsDisposed); Assert.False(second.IsDisposed);
    }

    [Fact]
    public void MatchingRetainsReferenceIdentityAmongEqualValuesAndNullDuplicates()
    {
        var first = new Row(1); var second = new Row(1);
        using var list = new ListBox { ItemsSource = new object?[] { first, null, second, null }, SelectedIndex = 2 };
        var firstContainer = list.Children[0]; var secondContainer = list.Children[2];
        var nullContainers = new[] { list.Children[1], list.Children[3] };
        list.ItemsSource = new object?[] { second, null, first, null };
        Assert.Same(secondContainer, list.Children[0]);
        Assert.Same(firstContainer, list.Children[2]);
        Assert.Same(nullContainers[0], list.Children[1]); Assert.Same(nullContainers[1], list.Children[3]);
        Assert.Same(second, list.SelectedItem);
        Assert.Equal(0, list.SelectedIndex);
    }

    [Fact]
    public void ANewEqualValueCannotTakeASurvivingReferencesEditorOrFocus()
    {
        var first = new Row(1); var second = new Row(1); var inserted = new Row(1);
        using var list = new ListBox { ItemsSource = new[] { first, second }, ItemTemplate = _ => new TextBox { Height = 24 }, SelectedIndex = 0 };
        using var host = new HeadlessRoot(list); Layout(host);
        var original = list.ContainerFromIndex(0)!; var other = list.ContainerFromIndex(1)!;
        var editor = Assert.Single(original.Descendants().OfType<TextBox>());
        editor.Text = "keep this edit"; editor.CaretIndex = editor.Text.Length;
        host.Focus.Focus(editor);
        list.ItemsSource = new[] { inserted, first }; Layout(host);
        Assert.Same(original, list.ContainerFromIndex(1)); Assert.Same(other, list.ContainerFromIndex(0));
        Assert.Same(first, list.SelectedItem); Assert.Equal(1, list.SelectedIndex);
        Assert.Same(editor, host.Focus.Focused); Assert.Equal("keep this edit", editor.Text);
        Assert.Equal(editor.Text.Length, editor.CaretIndex);

        var selection = new SelectionModel { Mode = SelectionMode.Multiple };
        selection.SetItems(new[] { second, first }); selection.Select(1);
        selection.SetItems(new[] { inserted, first });
        Assert.Same(first, selection.SelectedItem); Assert.Equal(1, selection.SelectedIndex);
    }

    [Fact]
    public void LargeListWithoutScrollViewerStillLaysOutAllRows()
    {
        using var list = new ListBox { ItemsSource = Enumerable.Range(0, 150).ToArray(), ItemTemplate = _ => new Border { Height = 20 } };
        using var host = new HeadlessRoot(list) { AvailableSize = new(260, double.PositiveInfinity) }; Layout(host);
        Assert.Equal(150, list.RealizedItemCount);
        Assert.True(list.DesiredSize.Height >= 3_000);
    }

    [Fact]
    public void AppendingAndMovingDataKeepTheVisibleEditorAndItsFocus()
    {
        var data = new ObservableCollection<object?>(Enumerable.Range(0, 500).Select(i => (object?)new Row(i)));
        using var root = new ScrollViewer { Width = 260, Height = 200 };
        var list = new ListBox { ItemsSource = data, ItemTemplate = _ => new TextBox { Height = 24 } };
        root.Children.Add(list); root.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(root); Layout(host);
        var container = list.ContainerFromIndex(2)!;
        var editor = Assert.Single(container.Descendants().OfType<TextBox>());
        editor.Text = "uncommitted edit"; editor.CaretIndex = editor.Text.Length;
        host.Focus.Focus(editor);
        data.Add(new Row(500)); Layout(host);
        Assert.Same(container, list.ContainerFromIndex(2));
        Assert.Same(editor, host.Focus.Focused);
        Assert.Equal("uncommitted edit", editor.Text);
        data.Move(2, 3); Layout(host);
        Assert.Same(container, list.ContainerFromIndex(3));
        Assert.Same(editor, host.Focus.Focused);
        Assert.Equal(editor.Text.Length, editor.CaretIndex);
        list.IsVirtualizationEnabled = false; Layout(host);
        Assert.Same(container, list.ContainerFromIndex(3));
        Assert.Same(editor, host.Focus.Focused);
        Assert.False(list.IsVirtualizing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnablingVirtualizationRetiresUnusedRowsAndPreservesTheVisibleEditor(bool customContainers)
    {
        using var root = new ScrollViewer { Width = 260, Height = 200 };
        var list = customContainers ? new CustomContainerList() : new ListBox();
        list.IsVirtualizationEnabled = false;
        list.ItemTemplate = _ => new TextBox { Height = 24 };
        list.ItemsSource = Enumerable.Range(0, 500).ToArray();
        root.Children.Add(list); root.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(root); Layout(host);
        Assert.Equal(500, list.RealizedItemCount);
        var retained = list.ContainerFromIndex(2)!; var retired = list.ContainerFromIndex(400)!;
        var editor = Assert.Single(retained.Descendants().OfType<TextBox>());
        editor.Text = "uncommitted"; editor.CaretIndex = editor.Text.Length;
        host.Focus.Focus(editor);
        list.IsVirtualizationEnabled = true; Layout(host);
        Assert.True(list.IsVirtualizing); Assert.InRange(list.RealizedItemCount, 1, 30);
        Assert.True(retired.IsDisposed); Assert.Null(list.ContainerFromIndex(400));
        Assert.Same(retained, list.ContainerFromIndex(2)); Assert.Same(editor, host.Focus.Focused);
        Assert.Equal("uncommitted", editor.Text); Assert.Equal(editor.Text.Length, editor.CaretIndex);
        list.Select(499); Layout(host);
        Assert.True(retained.IsDisposed); Assert.Null(list.ContainerFromIndex(2));
        var last = list.ContainerFromIndex(499)!;
        list.Select(0); Layout(host);
        Assert.True(last.IsDisposed); Assert.Null(list.ContainerFromIndex(499));
        Assert.InRange(list.RealizedItemCount, 1, 30);
        var cached = Assert.IsAssignableFrom<System.Collections.IDictionary>(typeof(ItemsControl)
            .GetField("_containerEntries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(list));
        Assert.InRange(cached.Count, 1, 31);
        Assert.All(cached.Keys.Cast<Element>(), row => Assert.False(row.IsDisposed));
    }

    [Fact]
    public void ReversingEqualDataMatchesContainersWithoutQuadraticEqualitySearches()
    {
        using var items = new ItemsControl { ItemTemplate = _ => new Border(), ItemsSource = Enumerable.Range(0, 1_000).Select(i => new CountedRow(i)).ToArray() };
        var first = items.Children[0]; var last = items.Children[^1];
        CountedRow.Comparisons = 0;
        items.ItemsSource = Enumerable.Range(0, 1_000).Reverse().Select(i => new CountedRow(i)).ToArray();
        Assert.Same(last, items.Children[0]); Assert.Same(first, items.Children[^1]);
        Assert.InRange(CountedRow.Comparisons, 1, 10_000);
    }

    [Fact]
    public void UpdatingAnEntireSelectedRangeUsesIndexedOccurrenceMatching()
    {
        var old = Enumerable.Range(0, 2_000).Select(i => (object?)new CountedRow(i)).ToArray();
        var selection = new SelectionModel { Mode = SelectionMode.Extended };
        selection.SetItems(old); selection.Select(0); selection.Select(old.Length - 1, extend: true);
        CountedRow.Comparisons = 0;
        selection.SetItems(Enumerable.Range(0, 2_001).Reverse().Select(i => (object?)new CountedRow(i)).ToArray());
        Assert.Equal(old.Length, selection.SelectedItems.Count);
        Assert.Equal(1, selection.SelectedIndex);
        Assert.False(selection.IsSelected(0));
        Assert.InRange(CountedRow.Comparisons, 1, 20_000);
    }

    private sealed record Row(int Id);
    private sealed class CustomContainerList : ListBox
    {
        protected override Element CreateContainer(object? item)
        {
            var container = new ListBoxItem();
            container.Children.Add(MakeItemContent(item));
            return container;
        }
    }
    private sealed class CountedRow(int id)
    {
        internal static int Comparisons;
        public override int GetHashCode() => id;
        public override bool Equals(object? obj) { Comparisons++; return obj is CountedRow other && id == other.Id; }
        private int Id => id;
    }
}
