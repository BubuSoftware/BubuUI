using System.Collections.ObjectModel;
using BubuUI.Elements;
using BubuUI.Themes.Fluent;
using Xunit;

namespace BubuUI.Headless.Tests;

public class FluentCompositeTemplateTests
{
    private static Point PointIn(Element element, double x, double y)
    {
        var origin = element.PointFromRoot(Point.Zero);
        return new(x - origin.X, y - origin.Y);
    }

    private static void Click(HeadlessRoot host, Point point)
    {
        host.PointerPress(point);
        host.PointerRelease(point);
        host.UpdateLayout();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComboBoxButtonTogglesDropdownAndOutsideClickStillActivatesItsTarget(bool dark)
    {
        using var root = new Grid { Width = 600, Height = 600 };
        var combo = new ComboBox
        {
            Width = 180, Height = 32, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, ItemsSource = new[] { "first", "second" }
        };
        var outside = new Button
        {
            Width = 100, Height = 32, Label = "Outside", HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        var outsideClicks = 0;
        outside.AddHandler(Button.ClickEvent, _ => outsideClicks++);
        root.Children.Add(combo);
        root.Children.Add(outside);
        root.SetThemeDefaults(dark ? FluentTheme.Dark : FluentTheme.Light);
        using var host = new HeadlessRoot(root);
        host.UpdateLayout();
        var button = Assert.Single(combo.Descendants().OfType<DropDownButton>());
        var visual = Assert.Single(button.Descendants().OfType<FluentDropDownButtonVisual>());
        var popup = Assert.Single(combo.Descendants().OfType<Popup>());
        var labelPoint = PointIn(visual, 24, visual.FinalRect.Height / 2);
        var arrowPoint = PointIn(visual, visual.FinalRect.Width - 12, visual.FinalRect.Height / 2);
        Assert.Same(visual, host.Input.HitTest(labelPoint));
        Assert.Same(visual, host.Input.HitTest(arrowPoint));

        Click(host, labelPoint);
        Assert.True(combo.IsDropDownOpen);
        Assert.True(popup.IsOpen);
        Click(host, arrowPoint);
        Assert.False(combo.IsDropDownOpen);
        Assert.False(popup.IsOpen);
        Click(host, arrowPoint);
        Assert.True(combo.IsDropDownOpen);
        Assert.True(popup.IsOpen);
        Click(host, PointIn(outside, outside.FinalRect.Width / 2, outside.FinalRect.Height / 2));
        Assert.False(combo.IsDropDownOpen);
        Assert.False(popup.IsOpen);
        Assert.Equal(1, outsideClicks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposingExpandedOrSelectedComboBoxDoesNotWriteBackItsBindingOrNotifySelection(bool selectItem)
    {
        using var root = new Grid { Width = 600, Height = 600 };
        var combo = new ComboBox
        {
            Width = 180, Height = 32, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, ItemsSource = new[] { "system", "light", "dark" }
        };
        root.Children.Add(combo);
        root.SetThemeDefaults(FluentTheme.Light);
        var businessValue = 0;
        var writes = 0;
        var invalidations = 0;
        var selectionChanges = 0;
        using var binding = UiBinding.TwoWay(combo, ComboBox.SelectedIndexProperty,
            () => combo.SelectedIndex, value => combo.SelectedIndex = value,
            () => businessValue, value => { businessValue = value; writes++; }, () => invalidations++);
        combo.AddHandler(ComboBox.SelectionChangedEvent, _ => selectionChanges++);
        using var host = new HeadlessRoot(root);
        host.UpdateLayout();
        var button = Assert.Single(combo.Descendants().OfType<DropDownButton>());

        Click(host, PointIn(button, button.FinalRect.Width / 2, button.FinalRect.Height / 2));
        Assert.True(combo.IsDropDownOpen);
        var list = Assert.Single(combo.Descendants().OfType<ListBox>());
        if (selectItem)
        {
            var item = Assert.IsAssignableFrom<Element>(list.ContainerFromIndex(2));
            Click(host, PointIn(item, item.FinalRect.Width / 2, item.FinalRect.Height / 2));
            Assert.Equal(2, businessValue);
            Assert.False(combo.IsDropDownOpen);
            Assert.True(writes > 0);
            Assert.True(selectionChanges > 0);
        }
        var selectedValue = businessValue;
        writes = 0;
        invalidations = 0;
        selectionChanges = 0;

        // Keep the binding alive while the owner recursively disposes the template's selected list.
        root.Dispose();

        Assert.True(combo.IsDisposed);
        Assert.True(list.IsDisposed);
        Assert.Equal(selectedValue, businessValue);
        Assert.Equal(0, writes);
        Assert.Equal(0, invalidations);
        Assert.Equal(0, selectionChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DatePickerButtonTogglesDropdownAndEditorClickDismissesCalendar(bool dark)
    {
        using var root = new Grid { Width = 600, Height = 600 };
        var picker = new DatePicker
        {
            Width = 180, Height = 32, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, SelectedDate = new(2026, 11, 14)
        };
        root.Children.Add(picker);
        root.SetThemeDefaults(dark ? FluentTheme.Dark : FluentTheme.Light);
        using var host = new HeadlessRoot(root);
        host.UpdateLayout();
        var button = Assert.Single(picker.Descendants().OfType<DropDownButton>());
        var visual = Assert.Single(button.Descendants().OfType<FluentDropDownButtonVisual>());
        var popup = Assert.Single(picker.Descendants().OfType<Popup>());
        var editor = Assert.Single(picker.Descendants().OfType<TextBox>());
        var buttonPoint = PointIn(visual, visual.FinalRect.Width - 12, visual.FinalRect.Height / 2);
        Assert.Same(visual, host.Input.HitTest(buttonPoint));

        Click(host, buttonPoint);
        Assert.True(picker.IsDropDownOpen);
        Assert.True(popup.IsOpen);
        Click(host, buttonPoint);
        Assert.False(picker.IsDropDownOpen);
        Assert.False(popup.IsOpen);
        Click(host, buttonPoint);
        Assert.True(picker.IsDropDownOpen);
        Assert.True(popup.IsOpen);
        Click(host, PointIn(editor, editor.FinalRect.Width / 2, editor.FinalRect.Height / 2));
        Assert.False(picker.IsDropDownOpen);
        Assert.False(popup.IsOpen);
        Assert.Same(editor, host.Focus.Focused);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ListBoxPointerSelectionFindsItsItemWithOrWithoutATemplate(bool templated)
    {
        using var list = new ListBox
        {
            Width = 180, Height = 200, ItemsSource = new[] { "first", "second", "third" },
            ItemTemplate = _ => new Border { Width = 100, Height = 24 }
        };
        if (templated) list.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(list);
        host.UpdateLayout();
        var item = list.Children[2];
        Assert.Same(list, item.ContentParent);
        if (templated) Assert.NotSame(list, item.Parent);
        else Assert.Same(list, item.Parent);
        var content = Assert.Single(item.Descendants().OfType<Border>());
        var origin = content.PointFromRoot(Point.Zero);
        var point = new Point(content.FinalRect.Width / 2 - origin.X, content.FinalRect.Height / 2 - origin.Y);
        host.PointerPress(point);
        host.PointerRelease(point);
        Assert.Equal(2, list.SelectedIndex);
        Assert.Equal("third", list.SelectedItem);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComboBoxShortDropdownFitsItsItemsAndKeepsPopupInsets(bool dark)
    {
        using var root = new Grid { Width = 600, Height = 600 };
        var combo = new ComboBox
        {
            Width = 180, Height = 32, VerticalAlignment = VerticalAlignment.Top,
            ItemsSource = new[] { "first", "second", "third" }, SelectedIndex = 0
        };
        root.Children.Add(combo);
        root.SetThemeDefaults(dark ? FluentTheme.Dark : FluentTheme.Light);
        using var host = new HeadlessRoot(root);
        host.UpdateLayout();
        var buttonOrigin = combo.PointFromRoot(Point.Zero);
        var buttonPoint = new Point(combo.FinalRect.Width / 2 - buttonOrigin.X, combo.FinalRect.Height / 2 - buttonOrigin.Y);
        host.PointerPress(buttonPoint);
        host.PointerRelease(buttonPoint);
        host.UpdateLayout();

        var popup = Assert.Single(combo.Descendants().OfType<Popup>());
        var viewer = Assert.Single(combo.Descendants().OfType<ScrollViewer>());
        var list = Assert.Single(combo.Descendants().OfType<ListBox>());
        Assert.True(combo.IsDropDownOpen);
        Assert.Same(list, host.Focus.Focused);
        Assert.False(list.IsFocusVisible);
        var itemsHeight = list.Children.Sum(item => item.FinalRect.Height);
        Assert.True(itemsHeight > 0);
        Assert.Equal(itemsHeight + viewer.Padding * 2, viewer.FinalRect.Height, 6);
        Assert.True(viewer.FinalRect.Height < viewer.MaxHeight);
        Assert.Equal(0, viewer.ScrollableHeight, 6);

        var viewerOrigin = viewer.PointFromRoot(Point.Zero);
        var listOrigin = list.PointFromRoot(Point.Zero);
        Assert.Equal(4, viewerOrigin.X - listOrigin.X, 6);
        Assert.Equal(4, viewerOrigin.Y - listOrigin.Y, 6);
        Assert.Equal(viewer.FinalRect.Width - 8, list.FinalRect.Width, 6);
        Assert.Equal(viewer.FinalRect.Height, popup.TemplateRoot!.FinalRect.Height, 6);

        host.KeyPress(Key.Escape);
        Assert.False(combo.IsDropDownOpen);
        host.Input.KeyPress(Key.Down, KeyModifiers.Alt);
        host.UpdateLayout();
        Assert.True(combo.IsDropDownOpen);
        Assert.Same(list, host.Focus.Focused);
        Assert.True(list.IsFocusVisible);
        host.KeyPress(Key.End);
        Assert.Equal(2, combo.SelectedIndex);
        Assert.True(combo.IsDropDownOpen);
        host.KeyPress(Key.Enter);
        Assert.False(combo.IsDropDownOpen);
        Assert.False(popup.IsOpen);
        Assert.Same(combo, host.Focus.Focused);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComboBoxLongDropdownKeepsABoundedViewportAndCanChooseTheLastItem(bool dark)
    {
        using var root = new Grid { Width = 600, Height = 600 };
        var items = Enumerable.Range(0, 30).Select(index => $"Item {index}").ToArray();
        var combo = new ComboBox
        {
            Width = 180, Height = 32, VerticalAlignment = VerticalAlignment.Top,
            ItemsSource = items, SelectedIndex = 0
        };
        root.Children.Add(combo);
        root.SetThemeDefaults(dark ? FluentTheme.Dark : FluentTheme.Light);
        using var host = new HeadlessRoot(root);
        host.UpdateLayout();
        combo.IsDropDownOpen = true;
        host.UpdateLayout();

        var popup = Assert.Single(combo.Descendants().OfType<Popup>());
        var viewer = Assert.Single(combo.Descendants().OfType<ScrollViewer>());
        var list = Assert.Single(combo.Descendants().OfType<ListBox>());
        Assert.InRange(viewer.FinalRect.Height, 1, 240);
        Assert.InRange(viewer.ViewportHeight, 1, 232);
        Assert.True(viewer.ScrollableHeight > 0);
        Assert.True(viewer.ExtentHeight > viewer.ViewportHeight);

        viewer.ScrollToEnd();
        host.UpdateLayout();
        Assert.Equal(viewer.ScrollableHeight, viewer.VerticalOffset, 6);
        var last = list.Children[^1];
        var origin = last.PointFromRoot(Point.Zero);
        var point = new Point(last.FinalRect.Width / 2 - origin.X, last.FinalRect.Height / 2 - origin.Y);
        host.PointerPress(point);
        host.PointerRelease(point);
        host.UpdateLayout();
        Assert.Equal(items.Length - 1, combo.SelectedIndex);
        Assert.Equal(items[^1], combo.SelectedItem);
        Assert.False(combo.IsDropDownOpen);
        Assert.False(popup.IsOpen);

        combo.SelectedIndex = 0;
        viewer.ScrollToHome();
        host.Focus.Focus(combo, FocusOrigin.Keyboard);
        host.Input.KeyPress(Key.Down, KeyModifiers.Alt);
        host.UpdateLayout();
        Assert.True(combo.IsDropDownOpen);
        Assert.Same(list, host.Focus.Focused);
        Assert.True(list.IsFocusVisible);
        host.KeyPress(Key.End);
        host.UpdateLayout();
        Assert.Equal(items.Length - 1, combo.SelectedIndex);
        Assert.Equal(viewer.ScrollableHeight, viewer.VerticalOffset, 6);
        Assert.True(combo.IsDropDownOpen);
        host.KeyPress(Key.Enter);
        Assert.False(combo.IsDropDownOpen);
        Assert.False(popup.IsOpen);
        Assert.Same(combo, host.Focus.Focused);
    }

    [Fact]
    public void ComboBoxAcceptsDataBeforeTemplateAndKeepsSelectionWhenRetemplated()
    {
        var items = new ObservableCollection<object?> { "first", "second" };
        using var combo = new ComboBox { ItemsSource = items, SelectedIndex = 1 };
        Assert.Empty(combo.Children);
        Assert.Equal("second", combo.SelectedItem);
        combo.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(combo); host.UpdateLayout();
        var original = Assert.Single(combo.Descendants().OfType<ListBox>());
        Assert.Equal(1, original.SelectedIndex);
        Assert.Equal("second", Assert.Single(combo.Descendants().OfType<DropDownButton>()).Label);
        combo.Template = new FluentComboBoxTemplate(); host.UpdateLayout();
        var replacement = Assert.Single(combo.Descendants().OfType<ListBox>());
        Assert.NotSame(original, replacement);
        Assert.True(original.IsDisposed);
        Assert.Equal("second", replacement.SelectedItem);
        items.Add("third"); host.UpdateLayout();
        Assert.Equal(3, replacement.ItemValues.Count);
        replacement.SelectedIndex = 2;
        Assert.Equal("third", combo.SelectedItem);
    }

    [Fact]
    public void InvalidCompositeTemplateRestoresThePreviousPartsAndInteraction()
    {
        using var combo = new ComboBox { ItemsSource = new[] { "one", "two" }, SelectedIndex = 0 };
        combo.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(combo); host.UpdateLayout();
        var oldRoot = combo.TemplateRoot;
        var oldList = Assert.Single(combo.Descendants().OfType<ListBox>());
        Assert.Throws<InvalidOperationException>(() => combo.Template = new FuncControlTemplate<ComboBox>(_ => new TemplateInstance(new Grid())));
        Assert.Same(oldRoot, combo.TemplateRoot);
        Assert.False(oldList.IsDisposed);
        combo.ClearLocalValue(Element.TemplateProperty);
        oldList.SelectedIndex = 1;
        Assert.Equal("two", combo.SelectedItem);
    }

    [Fact]
    public void DatePickerTemplatePartsSynchronizeAndSurviveTemplateReplacement()
    {
        using var picker = new DatePicker { SelectedDate = new(2026, 11, 14), MinimumDate = new(2026, 1, 1), MaximumDate = new(2026, 12, 31) };
        Assert.Empty(picker.Children);
        picker.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(picker); host.UpdateLayout();
        var editor = Assert.Single(picker.Descendants().OfType<TextBox>());
        var calendar = Assert.Single(picker.Descendants().OfType<BubuUI.Elements.Calendar>());
        Assert.Equal("2026-11-14", editor.Text);
        Assert.Equal(picker.MinimumDate, calendar.MinimumDate);
        picker.Template = new FluentDatePickerTemplate(); host.UpdateLayout();
        var replacement = Assert.Single(picker.Descendants().OfType<BubuUI.Elements.Calendar>());
        Assert.True(editor.IsDisposed);
        Assert.True(calendar.IsDisposed);
        replacement.SelectDate(new(2026, 12, 1));
        Assert.Equal(new DateTime(2026, 12, 1), picker.SelectedDate);
        Assert.Equal("2026-12-01", Assert.Single(picker.Descendants().OfType<TextBox>()).Text);
    }

    [Fact]
    public void DataGridRetemplatesItsSurfaceWithoutReplacingDataHostsOrSelection()
    {
        using var grid = new DataGrid { ItemsSource = new[] { "one", "two" }, SelectedIndex = 1, Width = 240, Height = 160 };
        grid.Columns.Add(new() { Header = "Value" });
        Assert.Empty(grid.Children);
        grid.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(grid); host.UpdateLayout(); host.UpdateLayout();
        var header = grid.HeaderHost; var rows = grid.RowsHost;
        Assert.InRange(grid.RealizedRowCount, 1, 2);
        grid.Template = new FluentDataGridTemplate(); host.UpdateLayout(); host.UpdateLayout();
        Assert.Same(header, grid.HeaderHost); Assert.Same(rows, grid.RowsHost);
        Assert.False(header.IsDisposed); Assert.False(rows.IsDisposed);
        Assert.Equal("two", grid.SelectedItem);
        Assert.InRange(grid.RealizedRowCount, 1, 2);
    }

    [Fact]
    public void TreeAndTabContentKeepIdentityAcrossThemeTemplates()
    {
        using var root = new StackPanel();
        var tree = new TreeView(); var parent = new TreeViewItem { Header = "parent", IsExpanded = true };
        var child = new TreeViewItem { Header = "child" }; parent.Children.Add(child); tree.Children.Add(parent);
        var tabs = new TabControl(); var first = new TabItem { Header = "first", Content = new TextBox() }; var second = new TabItem { Header = "second", Content = new Button() };
        tabs.Children.Add(first); tabs.Children.Add(second); root.Children.Add(tree); root.Children.Add(tabs);
        root.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(root); host.UpdateLayout();
        Assert.Same(child, parent.Children[0]);
        Assert.Equal(28, parent.HeaderHeight); Assert.Equal(34, tabs.HeaderHeight);
        parent.Template = new FluentTreeViewItemTemplate(); tabs.Template = new FluentTabControlTemplate(); host.UpdateLayout();
        Assert.Same(child, parent.Children[0]); Assert.False(child.IsDisposed);
        Assert.Same(first, tabs.Children[0]); Assert.False(first.IsDisposed);
        tabs.SelectedIndex = 1; host.UpdateLayout();
        Assert.False(first.IsVisible); Assert.True(second.IsVisible);
        host.Focus.Focus(child); host.KeyPress(Key.Left);
        Assert.Same(parent, host.Focus.Focused);
    }
}
