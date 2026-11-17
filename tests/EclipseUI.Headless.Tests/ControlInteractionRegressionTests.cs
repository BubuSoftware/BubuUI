using EclipseUI.Elements;
using EclipseUI.Themes.Fluent;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class ControlInteractionRegressionTests
{
    private static Point PointIn(Element e, double x = 5, double y = 5) { var zero = e.PointFromRoot(Point.Zero); return new(x - zero.X, y - zero.Y); }
    [Fact]
    public void ClickingButtonContentRaisesClickOnItsButton()
    {
        var inner = new Border { Width = 50, Height = 20 }; var button = new Button { Content = inner }; int clicks = 0; button.AddHandler(Button.ClickEvent, _ => clicks++);
        button.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(button); host.UpdateLayout(); var point = PointIn(inner); host.PointerPress(point); host.PointerRelease(point);
        Assert.Equal(1, clicks); Assert.False(button.IsPressed); Assert.False(inner.IsPressed);
    }
    [Fact]
    public void NumericSpinnerButtonsWorkThroughTheTextEditorInputHandler()
    {
        var number = new NumericUpDown { Width = 120, Height = 32, SpinnerWidth = 20, Value = 5 }; using var host = new HeadlessRoot(number); host.UpdateLayout();
        var up = PointIn(number, 110, 6); host.PointerPress(up); host.PointerRelease(up); Assert.Equal(6, number.Value);
    }
    [Fact]
    public void SingleLineControlCharacterDoesNotDeleteSelection()
    {
        var editor = new TextBox { Text = "keep" }; using var host = new HeadlessRoot(editor); host.UpdateLayout(); host.Focus.Focus(editor); editor.SelectAll(); host.Input.TextInput("\r"); Assert.Equal("keep", editor.Text);
    }
    [Fact]
    public void SelectionAssignedBeforeDataSourceIsAppliedAndNotifiedOnce()
    {
        var list = new ListBox { SelectedIndex = 1 }; int changed = 0; list.AddHandler(ListBox.SelectionChangedEvent, _ => changed++);
        list.ItemsSource = new[] { "a", "b", "c" }; Assert.Equal("b", list.SelectedItem); Assert.Equal(1, changed);
        list.SelectedIndex = 2; Assert.Equal(2, changed); list.SelectedIndex = 2; Assert.Equal(2, changed);
    }
    [Fact]
    public void TooltipWorksWhenTargetContainsOtherElements()
    {
        var grid = new Grid { Width = 300, Height = 200 }; var content = new Border { Width = 80, Height = 20 }; var button = new Button { Content = content, VerticalAlignment = VerticalAlignment.Top };
        var tip = new ToolTip { Target = button, Content = new Border { Width = 100, Height = 30 } }; grid.Children.Add(button); grid.Children.Add(tip);
        grid.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(grid); host.UpdateLayout(); host.PointerMove(PointIn(content)); Assert.True(tip.IsOpen);
        host.PointerMove(new(290, 190)); Assert.False(tip.IsOpen);
    }
    [Fact]
    public void ClickingNestedPopupDoesNotDismissItsAncestor()
    {
        var root = new Grid { Width = 400, Height = 300 }; var outerBody = new StackPanel(); var target = new Button { Label = "owner" }; outerBody.Children.Add(target);
        var innerBody = new Button { Label = "nested", Width = 100 }; var inner = new Popup { Content = innerBody, PlacementTarget = target, Placement = Placement.Right, IsOpen = true }; outerBody.Children.Add(inner);
        var outer = new Popup { Content = outerBody, IsOpen = true }; root.Children.Add(outer);
        root.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(root); host.UpdateLayout(); host.PointerPress(PointIn(innerBody)); Assert.True(outer.IsOpen); Assert.True(inner.IsOpen);
    }
    [Fact]
    public void RadioArrowsSelectNextEnabledPeerAndMoveFocus()
    {
        var root = new StackPanel(); var a = new RadioButton { Label = "A" }; var b = new RadioButton { Label = "B" }; root.Children.Add(a); root.Children.Add(b);
        using var host = new HeadlessRoot(root); host.UpdateLayout(); host.Focus.Focus(a); a.Toggle(); host.KeyPress(Key.Right); Assert.True(b.Checked); Assert.False(a.Checked); Assert.Same(b, host.Focus.Focused);
    }
    [Fact]
    public void DatePickerAltDownIsHandledBeforeTextBoxNavigation()
    {
        var picker = new DatePicker(); picker.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(picker); host.UpdateLayout(); host.Focus.Focus(picker.Descendants().OfType<TextBox>().Single()); host.Input.KeyPress(Key.Down, KeyModifiers.Alt); Assert.True(picker.IsDropDownOpen);
    }
    [Fact]
    public void ClickingEditableDataGridCellSelectsItsRowBeforeTextInputConsumesPointer()
    {
        var grid = new DataGrid { Width = 300, Height = 160, IsReadOnly = false, ItemsSource = new[] { new ControlCatalogTests.Row { Name = "one" }, new ControlCatalogTests.Row { Name = "two" } } };
        grid.SetThemeDefaults(FluentTheme.Light);
        grid.Columns.Add(new() { Header = "Name", Binding = "Name" }); using var host = new HeadlessRoot(grid); host.UpdateLayout(); host.UpdateLayout();
        var editor = grid.Descendants().OfType<TextBox>().Last(); host.PointerPress(PointIn(editor)); Assert.Equal(1, grid.SelectedIndex); Assert.Same(editor, host.Focus.Focused);
    }
    [Fact]
    public void FrameClockAdvancesOnlyActiveVisibleAnimations()
    {
        var root = new StackPanel();
        var progress = new ProgressBar { IsIndeterminate = true, Theme = EclipseUI.Themes.Fluent.StyleBootstrap.FluentProgressBar };
        var idle = new ProgressBar { Theme = EclipseUI.Themes.Fluent.StyleBootstrap.FluentProgressBar };
        root.Children.Add(progress); root.Children.Add(idle);
        using var host = new HeadlessRoot(root); host.UpdateLayout();
        var movingVisual = progress.Descendants().OfType<EclipseUI.Themes.Fluent.FluentProgressBarVisual>().Single();
        var idleVisual = idle.Descendants().OfType<EclipseUI.Themes.Fluent.FluentProgressBarVisual>().Single();
        var moving = movingVisual.RenderVersion; var stationary = idleVisual.RenderVersion;
        FrameAnimations.Advance(root, TimeSpan.FromSeconds(.2)); Assert.True(movingVisual.RenderVersion > moving); Assert.Equal(stationary, idleVisual.RenderVersion);
        progress.IsVisible = false; moving = movingVisual.RenderVersion;
        FrameAnimations.Advance(root, TimeSpan.FromSeconds(.2)); Assert.Equal(moving, movingVisual.RenderVersion);
    }

    [Fact]
    public void DropdownKeyboardNavigatesWithoutClosingUntilEnter()
    {
        var combo = new ComboBox { ItemsSource = new[] { "one", "two", "three" } };
        combo.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(combo); host.UpdateLayout(); host.Focus.Focus(combo);
        host.Input.KeyPress(Key.Down, KeyModifiers.Alt); host.UpdateLayout();
        host.KeyPress(Key.Down); host.KeyPress(Key.Down); Assert.Equal(1, combo.SelectedIndex); Assert.True(combo.IsDropDownOpen);
        host.KeyPress(Key.Enter); Assert.False(combo.IsDropDownOpen); Assert.Same(combo, host.Focus.Focused);
    }

    [Fact]
    public void CalendarArrowNavigationKeepsPopupOpenUntilDateIsChosen()
    {
        var picker = new DatePicker { SelectedDate = new(2026, 11, 14) }; picker.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(picker); host.UpdateLayout();
        host.Focus.Focus(picker.Descendants().OfType<TextBox>().Single()); host.Input.KeyPress(Key.Down, KeyModifiers.Alt); host.UpdateLayout();
        host.KeyPress(Key.Right); Assert.Equal(new DateTime(2026, 11, 15), picker.SelectedDate); Assert.True(picker.IsDropDownOpen);
        host.KeyPress(Key.Enter); Assert.False(picker.IsDropDownOpen);
    }

    [Fact]
    public void SubmenuActionClosesPopupWithoutReopeningItsOwner()
    {
        var root = new Grid { Width = 400, Height = 250 }; var owner = new MenuItem { Label = "File" }; var menu = new ContextMenu(); var action = new MenuItem { Label = "Open" };
        menu.Items.Add(action); owner.Submenu = menu; root.Children.Add(owner); using var host = new HeadlessRoot(root); host.UpdateLayout(); owner.Click(); host.UpdateLayout();
        Assert.True(menu.IsOpen); action.Click(); Assert.False(menu.IsOpen);
    }

    [Fact]
    public void RecyclingAnEditedGridRowCommitsBeforeRebindingItsContainer()
    {
        var data = Enumerable.Range(0, 100).Select(i => new ControlCatalogTests.Row { Count = i }).ToArray();
        var grid = new DataGrid { Width = 300, Height = 160, IsReadOnly = false, ItemsSource = data };
        grid.SetThemeDefaults(FluentTheme.Light);
        grid.Columns.Add(new() { Header = "Count", Binding = "Count" }); using var host = new HeadlessRoot(grid); host.UpdateLayout(); host.UpdateLayout();
        var editor = grid.Descendants().OfType<TextBox>().First(); host.Focus.Focus(editor); editor.SelectAll(); host.Input.TextInput("42");
        grid.Descendants().OfType<ScrollViewer>().Single().ScrollToEnd(); host.UpdateLayout(); Assert.Equal(42, data[0].Count);
    }

    [Fact]
    public void ChangingMultiSelectionToSingleCoercesTheSelectionImmediately()
    {
        var list = new ListBox { ItemsSource = new[] { "one", "two", "three" }, SelectionMode = SelectionMode.Multiple };
        list.Select(0); list.Select(2); Assert.Equal(2, list.SelectedItems.Count);
        list.SelectionMode = SelectionMode.Single; Assert.Single(list.SelectedItems); Assert.Equal("one", list.SelectedItem);
        Assert.False(((ListBoxItem)list.Children[2]).IsSelected);
    }
}
