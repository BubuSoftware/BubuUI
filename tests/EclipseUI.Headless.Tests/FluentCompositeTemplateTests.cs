using System.Collections.ObjectModel;
using EclipseUI.Elements;
using EclipseUI.Themes.Fluent;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class FluentCompositeTemplateTests
{
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
        var calendar = Assert.Single(picker.Descendants().OfType<EclipseUI.Elements.Calendar>());
        Assert.Equal("2026-11-14", editor.Text);
        Assert.Equal(picker.MinimumDate, calendar.MinimumDate);
        picker.Template = new FluentDatePickerTemplate(); host.UpdateLayout();
        var replacement = Assert.Single(picker.Descendants().OfType<EclipseUI.Elements.Calendar>());
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
