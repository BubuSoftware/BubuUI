using System.Collections.ObjectModel;
using BubuUI.Elements;
using BubuUI.Themes.Fluent;
using Xunit;

namespace BubuUI.Headless.Tests;

public class ControlCatalogTests
{
    [Fact]
    public void ListBoxSupportsClickRangeSelectionAndObservableReorder()
    {
        var data = new ObservableCollection<object?> { "a", "b", "c", "d" };
        var list = new ListBox { ItemsSource = data, SelectionMode = SelectionMode.Extended };
        using var host = new HeadlessRoot(list); host.UpdateLayout();
        list.Select(1); list.Select(3, KeyModifiers.Shift); Assert.Equal(new object?[] { "b", "c", "d" }, list.SelectedItems);
        list.SelectedIndex = 2; data.Move(2, 0); Assert.Equal("c", list.SelectedItem); Assert.Equal(0, list.SelectedIndex);
        data.Remove("c"); Assert.Equal(-1, list.SelectedIndex);
    }
    [Fact]
    public void ListBoxHandlesKeyboardAndMarksItsContainers()
    {
        var list = new ListBox { ItemsSource = new[] { "a", "b", "c" } };
        using var host = new HeadlessRoot(list); host.UpdateLayout(); host.Focus.Focus(list);
        host.KeyPress(Key.Down); host.KeyPress(Key.Down); Assert.Equal(1, list.SelectedIndex); Assert.True(((ListBoxItem)list.Children[1]).IsSelected);
        host.KeyPress(Key.End); Assert.Equal("c", list.SelectedItem);
    }
    [Fact]
    public void RadioGroupsAreExclusiveAndCheckboxCyclesThreeStates()
    {
        var panel = new StackPanel(); var a = new RadioButton { GroupName = "g" }; var b = new RadioButton { GroupName = "g" }; panel.Children.Add(a); panel.Children.Add(b);
        a.Toggle(); b.Toggle(); Assert.False(a.Checked); Assert.True(b.Checked); b.Toggle(); Assert.True(b.Checked);
        var check = new CheckBox { IsThreeState = true }; check.Toggle(); Assert.True(check.Checked); check.Toggle(); Assert.True(check.IsIndeterminate); check.Toggle(); Assert.False(check.Checked); Assert.False(check.IsIndeterminate);
    }
    [Fact]
    public void ToggleButtonAndSwitchChangeStateThroughInput()
    {
        var toggle = new ToggleButton { Label = "toggle" }; using var host = new HeadlessRoot(toggle); host.UpdateLayout(); host.Focus.Focus(toggle);
        host.KeyPress(Key.Space); Assert.True(toggle.Checked);
        var control = new ToggleSwitch(); control.Toggle(); Assert.True(control.Checked);
    }
    [Fact]
    public void SliderDragClampsAndSnapsAndKeyboardAdjustsValue()
    {
        var slider = new Slider { Width = 120, Height = 24, ThumbSize = 14, Minimum = 10, Maximum = 20, TickFrequency = 2 };
        using var host = new HeadlessRoot(slider); host.UpdateLayout();
        host.PointerPress(new(60, 12)); Assert.Equal(16, slider.Value); host.PointerMove(new(300, 12)); host.PointerRelease(new(300, 12)); Assert.Equal(20, slider.Value);
        host.KeyPress(Key.Home); Assert.Equal(10, slider.Value); host.KeyPress(Key.Right); Assert.Equal(11, slider.Value);
    }
    [Fact]
    public void NumericEditorCommitsClampsAndRejectsInvalidText()
    {
        var number = new NumericUpDown { Minimum = 0, Maximum = 10, Value = 5 };
        number.Text = "22"; Assert.True(number.Commit()); Assert.Equal(10, number.Value);
        number.Text = "bad"; Assert.False(number.Commit()); Assert.Equal("10", number.Text);
        using var host = new HeadlessRoot(number); host.UpdateLayout(); host.Focus.Focus(number); host.KeyPress(Key.Down); Assert.Equal(9, number.Value);
    }
    [Fact]
    public void ComboBoxKeyboardSelectsAndDropdownTracksOutsideDismissal()
    {
        var combo = new ComboBox { ItemsSource = new[] { "first", "second" } };
        combo.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(combo); host.UpdateLayout(); host.Focus.Focus(combo);
        host.KeyPress(Key.Down); Assert.Equal("first", combo.SelectedItem);
        host.Input.KeyPress(Key.Down, KeyModifiers.Alt); host.UpdateLayout(); Assert.True(combo.IsDropDownOpen);
        host.KeyPress(Key.Escape); Assert.False(combo.IsDropDownOpen);
    }
    [Fact]
    public void TabControlHidesUnselectedContentAndSupportsControlTab()
    {
        var tabs = new TabControl(); var a = new TabItem { Header = "A", Content = new TextBox() }; var b = new TabItem { Header = "B", Content = new Button { Label = "B" } };
        tabs.Children.Add(a); tabs.Children.Add(b); using var host = new HeadlessRoot(tabs); host.UpdateLayout(); host.Focus.Focus(a.Content);
        Assert.False(b.IsVisible); host.Input.KeyPress(Key.Tab, KeyModifiers.Control); Assert.Equal(1, tabs.SelectedIndex); Assert.False(a.IsVisible); Assert.True(b.IsVisible);
    }
    [Fact]
    public void TreeViewKeyboardExpandsNavigatesAndSelectsVisibleChildren()
    {
        var tree = new TreeView(); var parent = new TreeViewItem { Header = "parent" }; var child = new TreeViewItem { Header = "child" }; parent.Children.Add(child); tree.Children.Add(parent);
        using var host = new HeadlessRoot(tree); host.UpdateLayout(); host.Focus.Focus(parent); tree.Select(parent);
        Assert.Single(tree.VisibleItems); host.KeyPress(Key.Right); host.UpdateLayout(); Assert.Equal(2, tree.VisibleItems.Count);
        host.KeyPress(Key.Down); Assert.Same(child, tree.SelectedItem); Assert.Same(child, host.Focus.Focused);
        host.KeyPress(Key.Left); Assert.Same(parent, tree.SelectedItem);
    }
    [Fact]
    public void TreeViewRejectsCyclicDataWithoutReplacingExistingTree()
    {
        var tree = new TreeView(); var original = new TreeViewItem { Header = "existing" }; tree.Children.Add(original);
        var value = new object(); tree.ChildrenSelector = _ => new[] { value };
        Assert.Throws<InvalidOperationException>(() => tree.ItemsSource = new[] { value }); Assert.Same(original, tree.Children[0]);
    }
    public sealed class Row { public string Name { get; set; } = ""; public int Count { get; set; } }
    [Fact]
    public void DataGridSortsTypedValuesAndPreservesSelectedObject()
    {
        var a = new Row { Name = "A", Count = 10 }; var b = new Row { Name = "B", Count = 2 };
        var grid = new DataGrid { ItemsSource = new[] { a, b } }; grid.Columns.Add(new() { Header = "Count", Binding = "Count" }); grid.SelectedIndex = 0;
        grid.Sort(0); Assert.Same(b, grid.View[0]); Assert.Same(a, grid.SelectedItem); grid.Sort(0); Assert.Same(a, grid.View[0]);
    }
    [Fact]
    public void DataGridVirtualizesLargeSourcesAndColumnEditConvertsTypes()
    {
        var data = Enumerable.Range(0, 10000).Select(i => new Row { Name = $"row {i}", Count = i }).ToArray();
        var grid = new DataGrid { ItemsSource = data, Height = 200, Width = 400 }; var column = new DataGridColumn { Header = "Count", Binding = "Count" }; grid.Columns.Add(column);
        grid.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(grid); host.UpdateLayout(); host.UpdateLayout(); Assert.InRange(grid.RealizedRowCount, 1, 30); Assert.True(grid.Descendants().OfType<ScrollViewer>().Single().ViewportHeight > 100);
        Assert.True(column.TrySetValue(data[0], "42")); Assert.Equal(42, data[0].Count); Assert.False(column.TrySetValue(data[0], "bad")); Assert.Equal(42, data[0].Count);
    }
    [Fact]
    public void CalendarHandlesLeapYearsAndRejectsDatesOutsideRange()
    {
        var calendar = new BubuUI.Elements.Calendar { SelectedDate = new(2024, 2, 28), MinimumDate = new(2024, 2, 1), MaximumDate = new(2024, 2, 29) };
        using var host = new HeadlessRoot(calendar); host.UpdateLayout(); host.Focus.Focus(calendar);
        host.KeyPress(Key.Right); Assert.Equal(new DateTime(2024, 2, 29), calendar.SelectedDate); host.KeyPress(Key.Right); Assert.Equal(new DateTime(2024, 2, 29), calendar.SelectedDate);
    }
    [Fact]
    public void DatePickerSynchronizesEditorAndCalendar()
    {
        var picker = new DatePicker { SelectedDate = new(2026, 11, 14) };
        Assert.Empty(picker.Children);
        picker.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(picker); host.UpdateLayout();
        Assert.Equal("2026-11-14", picker.Descendants().OfType<TextBox>().Single().Text);
        var calendar = picker.Descendants().OfType<BubuUI.Elements.Calendar>().Single(); calendar.SelectDate(new(2026, 12, 1)); Assert.Equal(new DateTime(2026, 12, 1), picker.SelectedDate);
    }
    [Fact]
    public void ObservableSourcesReleaseSubscriptionsOnDispose()
    {
        var source = new ObservableCollection<object?> { "a" }; var list = new ListBox { ItemsSource = source }; list.Dispose(); source.Add("b"); Assert.True(list.IsDisposed); Assert.Empty(list.Children);
    }
}
