using BubuUI.Elements;
using BubuUI.Themes.Fluent;
using Xunit;

namespace BubuUI.Headless.Tests;

public sealed class ModalPopupTests
{
    [Fact]
    public void ModalBlocksOutsidePointerAndWheelWithoutDismissingOrDisablingItsContents()
    {
        using var fixture = new Fixture();
        var clicks = 0; var wheels = 0;
        fixture.OwnerButton.AddHandler(Button.ClickEvent, _ => clicks++);
        fixture.OwnerButton.AddHandler(Element.PointerWheelChangedEvent, _ => wheels++);
        fixture.Open();

        fixture.Click(fixture.OwnerButton);
        fixture.Host.Input.PointerMove(PointIn(fixture.OwnerButton));
        fixture.Host.Input.PointerWheel(PointIn(fixture.OwnerButton), 3);

        Assert.True(fixture.Popup.IsOpen);
        Assert.Equal(0, clicks);
        Assert.Equal(0, wheels);
        Assert.Null(fixture.Host.Input.Captured);
        Assert.NotSame(fixture.OwnerButton, fixture.Host.Input.Hovered);
        Assert.True(fixture.Editor.IsEffectiveEnabled);
        var modalClicks = 0;
        fixture.First.AddHandler(Button.ClickEvent, _ => modalClicks++);
        fixture.Click(fixture.First);
        Assert.Equal(1, modalClicks);
    }

    [Fact]
    public void ModalFocusRejectsProgrammaticEscapeAndTabCyclesOnlyInsideTheDialog()
    {
        using var fixture = new Fixture();
        fixture.Open();
        Assert.Same(fixture.Editor, fixture.Host.Focus.Focused);
        Assert.False(fixture.Host.Focus.Focus(fixture.OwnerEditor));
        for (var index = 0; index < 8; index++)
        {
            fixture.Host.Input.KeyPress(Key.Tab, index % 2 == 0 ? KeyModifiers.None : KeyModifiers.Shift);
            Assert.Contains(fixture.Host.Focus.Focused, new Element[] { fixture.Editor, fixture.First, fixture.Second });
        }
        fixture.Popup.Close();
        Assert.Same(fixture.OwnerEditor, fixture.Host.Focus.Focused);
        Assert.True(fixture.Host.Focus.Focus(fixture.OwnerButton));
    }

    [Fact]
    public void ModalRoutesTextCompositionAndCommandsInsideItsBoundary()
    {
        using var fixture = new Fixture();
        var rootKeys = 0; var rootPreview = 0; var rootClicks = 0;
        var modalKeys = 0; var modalClicks = 0;
        fixture.Root.AddHandler(Element.KeyDownEvent, _ => rootKeys++);
        fixture.Root.AddHandler(Element.PreviewKeyDownEvent, _ => rootPreview++);
        fixture.Root.AddHandler(Button.ClickEvent, _ => rootClicks++);
        fixture.Body.AddHandler(Element.KeyDownEvent, _ => modalKeys++);
        fixture.First.AddHandler(Button.ClickEvent, _ => modalClicks++);
        fixture.Open();

        fixture.Host.Input.TextInput("x");
        fixture.Host.Input.TextComposition("中", CompositionStage.Started);
        fixture.Host.Input.TextComposition("中文", CompositionStage.Updated);
        fixture.Host.Input.TextComposition("中文", CompositionStage.Committed);
        fixture.Host.Input.KeyPress(Key.O, KeyModifiers.Control);
        fixture.Click(fixture.First);

        Assert.Equal("owner", fixture.OwnerEditor.Text);
        Assert.Contains("x", fixture.Editor.Text);
        Assert.Contains("中文", fixture.Editor.Text);
        Assert.True(modalKeys > 0);
        Assert.Equal(1, modalClicks);
        Assert.Equal(0, rootKeys);
        Assert.Equal(0, rootPreview);
        Assert.Equal(0, rootClicks);
    }

    [Fact]
    public void OpeningModalCancelsAnOwnersPendingPointerCapture()
    {
        using var fixture = new Fixture();
        var clicks = 0;
        fixture.OwnerButton.AddHandler(Button.ClickEvent, _ => clicks++);
        fixture.Host.Input.PointerPress(PointIn(fixture.OwnerButton));
        Assert.NotNull(fixture.Host.Input.Captured);

        fixture.Open();

        Assert.Null(fixture.Host.Input.Captured);
        Assert.False(fixture.OwnerButton.IsPressed);
        fixture.Host.Input.PointerRelease(PointIn(fixture.OwnerButton));
        Assert.Equal(0, clicks);
    }

    [Fact]
    public void EscapeDismissesTheModalAndDoesNotReleaseTheKeyToItsOwner()
    {
        using var fixture = new Fixture();
        var ownerKeys = 0;
        fixture.Root.AddHandler(Element.KeyDownEvent, _ => ownerKeys++);
        fixture.Root.AddHandler(Element.KeyUpEvent, _ => ownerKeys++);
        fixture.Open();

        fixture.Host.Input.KeyPress(Key.Escape);
        fixture.Host.Input.KeyRelease(Key.Escape);

        Assert.False(fixture.Popup.IsOpen);
        Assert.Same(fixture.OwnerEditor, fixture.Host.Focus.Focused);
        Assert.Equal(0, ownerKeys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeldEscapeDismissesOnlyOneModalUntilTheKeyIsReleased(bool nativeWindowPreDispatch)
    {
        using var fixture = new Fixture();
        fixture.Open();
        var inner = new Popup
        {
            IsModal = true, HostMode = PopupHostMode.Overlay,
            Content = new TextBox { Width = 120, Height = 30 },
        };
        fixture.Body.Children.Add(inner);
        inner.Open();
        fixture.Host.UpdateLayout();
        var ownerKeys = 0;
        var ownerCloseRequests = 0;
        fixture.Root.AddHandler(Element.KeyDownEvent, _ => ownerKeys++);
        fixture.Root.AddHandler(Element.KeyUpEvent, _ => ownerKeys++);

        PressEscape();
        Assert.False(inner.IsOpen);
        Assert.True(fixture.Popup.IsOpen);
        PressEscape(isRepeat: true);
        Assert.True(fixture.Popup.IsOpen);

        fixture.Host.Input.KeyRelease(Key.Escape);
        PressEscape();
        Assert.False(fixture.Popup.IsOpen);
        PressEscape(isRepeat: true);
        fixture.Host.Input.KeyRelease(Key.Escape);
        Assert.Equal(0, ownerKeys);
        Assert.Equal(0, ownerCloseRequests);

        void PressEscape(bool isRepeat = false)
        {
            // WindowHost checks popup dismissal before its CloseOnEscape behavior and KeyPress.
            if (nativeWindowPreDispatch)
            {
                if (fixture.Host.Input.TryDismissPopup(isRepeat)) return;
                ownerCloseRequests++;
                return;
            }
            fixture.Host.Input.KeyPress(Key.Escape, isRepeat: isRepeat);
        }
    }

    [Fact]
    public void StaysOpenModalConsumesEscapeWithoutClosingItsOwnerOrItself()
    {
        using var fixture = new Fixture();
        fixture.Popup.StaysOpen = true;
        fixture.Open();
        var ownerKeys = 0;
        fixture.Root.AddHandler(Element.KeyDownEvent, _ => ownerKeys++);

        Assert.True(fixture.Host.Input.TryDismissPopup());
        fixture.Host.Input.KeyPress(Key.Escape);
        fixture.Click(fixture.OwnerButton);

        Assert.True(fixture.Popup.IsOpen);
        Assert.Equal(0, ownerKeys);
        Assert.Same(fixture.Editor, fixture.Host.Focus.Focused);
    }

    [Fact]
    public void NestedModalWinsEvenWhenItWasOpenedBeforeItsOuterModal()
    {
        using var fixture = new Fixture();
        var innerEditor = new TextBox { Width = 120, Height = 30, Text = "inner" };
        var inner = new Popup
        {
            IsModal = true, IsOpen = true, HostMode = PopupHostMode.Overlay,
            Placement = Placement.Pointer, PointerPosition = new(190, 170), Content = innerEditor,
        };
        fixture.Body.Children.Add(inner);
        fixture.Open();
        var outerClicks = 0;
        fixture.First.AddHandler(Button.ClickEvent, _ => outerClicks++);

        fixture.Click(fixture.First);
        Assert.Equal(0, outerClicks);
        Assert.Same(innerEditor, fixture.Host.Focus.Focused);
        Assert.False(fixture.Host.Focus.Focus(fixture.Editor));
        fixture.Host.Input.KeyPress(Key.Escape);
        Assert.False(inner.IsOpen);
        Assert.True(fixture.Popup.IsOpen);
        Assert.Contains(fixture.Host.Focus.Focused, new Element[] { fixture.Editor, fixture.First, fixture.Second });
        fixture.Popup.Close();
        Assert.Same(fixture.OwnerEditor, fixture.Host.Focus.Focused);
    }

    [Fact]
    public void SiblingModalsUseOpeningOrderAndClosingTheLowerOneDoesNotStealFocus()
    {
        using var fixture = new Fixture();
        var topEditor = new TextBox { Width = 100, Height = 32 };
        var top = new Popup { IsModal = true, HostMode = PopupHostMode.Overlay, Content = topEditor };
        // Add before the existing modal so declaration order and activation order disagree.
        fixture.Root.Children.Remove(fixture.Popup);
        fixture.Root.Children.Add(top);
        fixture.Root.Children.Add(fixture.Popup);
        fixture.Open();
        top.Open();
        fixture.Host.UpdateLayout();
        Assert.Same(topEditor, fixture.Host.Focus.Focused);
        fixture.Popup.Close();
        Assert.Same(topEditor, fixture.Host.Focus.Focused);
        top.Close();
        Assert.Same(fixture.OwnerEditor, fixture.Host.Focus.Focused);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingOrDisposingAModalRestoresTheOwnersFocus(bool dispose)
    {
        using var fixture = new Fixture();
        fixture.Open();
        if (dispose) fixture.Popup.Dispose();
        else fixture.Root.Children.Remove(fixture.Popup);

        Assert.Same(fixture.OwnerEditor, fixture.Host.Focus.Focused);
        Assert.True(fixture.Host.Focus.Focus(fixture.OwnerButton));
        fixture.Popup.Dispose();
    }

    [Fact]
    public void ChangingIsModalWhileOpenReleasesAndRestoresTheFocusBoundary()
    {
        using var fixture = new Fixture();
        fixture.Popup.IsModal = false;
        fixture.Open();
        fixture.Host.Focus.Focus(fixture.Editor);
        fixture.Popup.IsModal = true;
        Assert.False(fixture.Host.Focus.Focus(fixture.OwnerEditor));
        fixture.Popup.IsModal = false;
        Assert.Same(fixture.OwnerEditor, fixture.Host.Focus.Focused);
        Assert.True(fixture.Host.Focus.Focus(fixture.OwnerButton));
    }

    [Fact]
    public void ADropdownInsideAModalClosesBeforeTheModalAndNeverNotifiesOwnerCommands()
    {
        using var fixture = new Fixture();
        var combo = new ComboBox { Width = 150, Height = 32, ItemsSource = new[] { "one", "two" }, SelectedIndex = 0 };
        fixture.Body.Children.Add(combo);
        fixture.Open();
        combo.IsDropDownOpen = true;
        fixture.Host.UpdateLayout();
        var ownerKeys = 0;
        fixture.Root.AddHandler(Element.KeyDownEvent, _ => ownerKeys++);

        fixture.Host.Input.KeyPress(Key.Escape);

        Assert.False(combo.IsDropDownOpen);
        Assert.True(fixture.Popup.IsOpen);
        fixture.Host.Input.KeyPress(Key.Escape);
        Assert.False(fixture.Popup.IsOpen);
        Assert.Equal(0, ownerKeys);
    }

    [Fact]
    public void PopupDeclaredBesideModalCanUseAModalPlacementTargetWithoutLeakingEvents()
    {
        using var fixture = new Fixture();
        fixture.Open();
        var childButton = new Button { Width = 100, Height = 32, Label = "child" };
        var child = new Popup
        {
            PlacementTarget = fixture.First, Content = childButton,
            Placement = Placement.Right, HostMode = PopupHostMode.Overlay,
        };
        fixture.Root.Children.Add(child);
        child.Open();
        fixture.Host.UpdateLayout();
        var childClicks = 0; var ownerClicks = 0;
        childButton.AddHandler(Button.ClickEvent, _ => childClicks++);
        fixture.Root.AddHandler(Button.ClickEvent, _ => ownerClicks++);

        Assert.True(fixture.Host.Focus.Focus(childButton));
        fixture.Click(childButton);

        Assert.Equal(1, childClicks);
        Assert.Equal(0, ownerClicks);
        Assert.True(fixture.Popup.IsOpen);
        fixture.Host.Input.KeyPress(Key.Escape);
        Assert.False(child.IsOpen);
        Assert.True(fixture.Popup.IsOpen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosingOrRemovingModalAlsoClosesItsAssociatedSiblingPopup(bool remove)
    {
        using var fixture = new Fixture();
        fixture.Open();
        var child = new Popup
        {
            PlacementTarget = fixture.First, Content = new Button { Width = 80, Height = 30 },
            IsOpen = true, HostMode = PopupHostMode.Overlay,
        };
        fixture.Root.Children.Add(child);
        fixture.Host.UpdateLayout();
        if (remove) fixture.Root.Children.Remove(fixture.Popup);
        else fixture.Popup.Close();
        Assert.False(child.IsOpen);
        Assert.Same(fixture.OwnerEditor, fixture.Host.Focus.Focused);
        fixture.Popup.Dispose();
    }

    [Fact]
    public void InitiallyOpenModalAddedToAHostClosesAssociatedPopupWhenRemoved()
    {
        using var fixture = new Fixture();
        fixture.Root.Children.Remove(fixture.Popup);
        fixture.Popup.IsOpen = true; // No opening-focus manager exists at this point.
        fixture.Root.Children.Add(fixture.Popup);
        fixture.Host.UpdateLayout();
        var child = new Popup
        {
            PlacementTarget = fixture.First, IsOpen = true, HostMode = PopupHostMode.Overlay,
            Content = new Button { Width = 80, Height = 30 },
        };
        fixture.Root.Children.Add(child);
        fixture.Host.UpdateLayout();
        fixture.Root.Children.Remove(fixture.Popup);
        Assert.False(child.IsOpen);
        Assert.Same(fixture.OwnerEditor, fixture.Host.Focus.Focused);
        fixture.Popup.Dispose();
    }

    [Fact]
    public void OrdinaryPopupStillDismissesAndLetsOutsideClicksActivateTheirTarget()
    {
        using var fixture = new Fixture();
        fixture.Popup.IsModal = false;
        fixture.Open();
        var clicks = 0;
        fixture.OwnerButton.AddHandler(Button.ClickEvent, _ => clicks++);

        fixture.Click(fixture.OwnerButton);

        Assert.False(fixture.Popup.IsOpen);
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void ClosingModalInPreviewDoesNotLetTheSameKeyReachTheOwner()
    {
        using var fixture = new Fixture();
        var ownerKeys = 0;
        fixture.Root.AddHandler(Element.KeyDownEvent, _ => ownerKeys++);
        fixture.Root.AddHandler(Element.KeyUpEvent, _ => ownerKeys++);
        fixture.Open();
        fixture.Editor.AddHandler(Element.PreviewKeyDownEvent, _ => fixture.Popup.Close());

        fixture.Host.Input.KeyPress(Key.O, KeyModifiers.Control);
        fixture.Host.Input.KeyPress(Key.O, KeyModifiers.Control, isRepeat: true);
        fixture.Host.Input.KeyRelease(Key.O, KeyModifiers.Control);

        Assert.False(fixture.Popup.IsOpen);
        Assert.Equal(0, ownerKeys);
    }

    [Fact]
    public void FreshPressAfterALostDropdownKeyReleaseIsNotSuppressedByAModal()
    {
        using var fixture = new Fixture();
        fixture.Popup.IsModal = false;
        fixture.Open();
        Assert.True(fixture.Host.Focus.Focus(fixture.First));
        fixture.First.AddHandler(Button.ClickEvent, _ => fixture.Popup.Close());
        fixture.Host.Input.KeyPress(Key.Enter);
        Assert.False(fixture.Popup.IsOpen);
        // Its native window has closed, so the dropdown's matching KeyUp never arrives.

        fixture.Popup.IsModal = true;
        fixture.Open();
        Assert.True(fixture.Host.Focus.Focus(fixture.Second));
        var clicks = 0;
        fixture.Second.AddHandler(Button.ClickEvent, _ => clicks++);
        fixture.Host.Input.KeyPress(Key.Enter, isRepeat: true);
        Assert.Equal(0, clicks);
        fixture.Host.Input.KeyPress(Key.Enter);
        fixture.Host.Input.KeyRelease(Key.Enter);
        Assert.Equal(1, clicks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshEscapeAfterALostReleaseRestartsItsKeyLifecycle(bool nativeWindowPreDispatch)
    {
        using var fixture = new Fixture();
        fixture.Popup.IsModal = false;
        fixture.Open();
        fixture.Host.Input.KeyPress(Key.Escape);
        Assert.False(fixture.Popup.IsOpen);
        // Losing this dropdown's KeyUp must not consume the next complete Escape press.
        var keyDowns = 0;
        var keyUps = 0;
        fixture.Root.AddHandler(Element.KeyDownEvent, args =>
        {
            if (args is KeyEventArgs { Key: Key.Escape }) keyDowns++;
        });
        fixture.Root.AddHandler(Element.KeyUpEvent, args =>
        {
            if (args is KeyEventArgs { Key: Key.Escape }) keyUps++;
        });

        if (nativeWindowPreDispatch)
        {
            Assert.False(fixture.Host.Input.TryDismissPopup());
            // The native owner's CloseOnEscape check must also remain eligible on repeat.
            Assert.False(fixture.Host.Input.TryDismissPopup(isRepeat: true));
        }
        fixture.Host.Input.KeyPress(Key.Escape);
        fixture.Host.Input.KeyRelease(Key.Escape);

        Assert.Equal(1, keyDowns);
        Assert.Equal(1, keyUps);
    }

    private static Point PointIn(Element element)
    {
        var zero = element.PointFromRoot(Point.Zero);
        return new(element.FinalRect.Width / 2 - zero.X, element.FinalRect.Height / 2 - zero.Y);
    }

    private sealed class Fixture : IDisposable
    {
        public Canvas Root { get; } = new() { Width = 420, Height = 340 };
        public Button OwnerButton { get; } = new() { Width = 90, Height = 32, Label = "owner button" };
        public TextBox OwnerEditor { get; } = new() { Width = 90, Height = 32, Text = "owner" };
        public StackPanel Body { get; } = new() { Width = 180, Height = 180, Gap = 4 };
        public TextBox Editor { get; } = new() { Width = 160, Height = 32, Text = "modal" };
        public Button First { get; } = new() { Width = 160, Height = 32, Label = "first" };
        public Button Second { get; } = new() { Width = 160, Height = 32, Label = "second" };
        public Popup Popup { get; }
        public HeadlessRoot Host { get; }
        public Fixture()
        {
            Canvas.SetLeft(OwnerButton, 10); Canvas.SetTop(OwnerButton, 10);
            Canvas.SetLeft(OwnerEditor, 10); Canvas.SetTop(OwnerEditor, 55);
            Root.Children.Add(OwnerButton); Root.Children.Add(OwnerEditor);
            Body.Children.Add(Editor); Body.Children.Add(First); Body.Children.Add(Second);
            Popup = new Popup
            {
                IsModal = true, HostMode = PopupHostMode.Overlay, Content = Body,
                Placement = Placement.Pointer, PointerPosition = new(140, 110),
            };
            Root.Children.Add(Popup);
            Root.SetThemeDefaults(FluentTheme.Light);
            Host = new HeadlessRoot(Root) { AvailableSize = new(420, 340) };
            Host.UpdateLayout();
            Assert.True(Host.Focus.Focus(OwnerEditor));
        }
        public void Open() { Popup.Open(); Host.UpdateLayout(); }
        public void Click(Element element)
        {
            var point = PointIn(element);
            Host.Input.PointerPress(point); Host.Input.PointerRelease(point);
            Host.UpdateLayout();
        }
        public void Dispose() { Host.Dispose(); Root.Dispose(); }
    }
}
