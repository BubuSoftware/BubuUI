using System.Runtime.InteropServices;
using EclipseUI.Platform.Linux;
using EclipseUI.Rendering;
using Xunit;

namespace EclipseUI.Platform.Linux.Tests;

public class InputTests
{
    [Theory]
    [InlineData(97u, Key.A)]
    [InlineData(122u, Key.Z)]
    [InlineData(65u, Key.A)]
    [InlineData(127u, Key.Delete)]
    [InlineData(0x40000050u, Key.Left)]
    [InlineData(0x4000004fu, Key.Right)]
    [InlineData(0x4000003bu, Key.F2)]
    [InlineData(0x40000058u, Key.Enter)]
    [InlineData(0x400000e1u, Key.None)]
    public void SdlKeycodesUseTheRuntimeKeyModel(uint code, Key expected)
        => Assert.Equal(expected, LinuxInput.Key(code));

    [Theory]
    [InlineData(0x0001)]
    [InlineData(0x0002)]
    public void BothSidesOfModifiersWork(int shift)
    {
        Assert.Equal(KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt,
            LinuxInput.Modifiers((ushort)(shift | 0x0040 | 0x0200)));
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Alt, LinuxInput.Modifiers(0x0180));
        Assert.Equal(KeyModifiers.None, LinuxInput.Modifiers(0x6000)); // Lock/mode flags
    }

    [Fact]
    public void WindowCoordinatesUseBothPixelDensityAndContentScale()
    {
        Assert.Equal(new Point(80, 40), LinuxInput.ToLogical(100, 50, 1, 1, 1.25)); // X11 scale
        Assert.Equal(new Point(100, 50), LinuxInput.ToLogical(100, 50, 2, 2, 2)); // Wayland density
        Assert.Equal(60, LinuxInput.WheelDelta(.5f, 0));
        Assert.Equal(-60, LinuxInput.WheelDelta(.5f, 1));
        Assert.Equal(PointerButton.Middle, LinuxInput.Button(2));
        Assert.Null(LinuxInput.Button(4));
    }

    [Fact]
    public void NativeEventLayoutMatchesSdl3Headers()
    {
        Assert.Equal(128, Marshal.SizeOf<SdlNative.Event>());
        Assert.Equal(16, Marshal.OffsetOf<SdlNative.Event>(nameof(SdlNative.Event.WindowId)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<SdlNative.KeyboardEvent>(nameof(SdlNative.KeyboardEvent.Key)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<SdlNative.KeyboardEvent>(nameof(SdlNative.KeyboardEvent.Mod)).ToInt32());
        Assert.Equal(37, Marshal.OffsetOf<SdlNative.KeyboardEvent>(nameof(SdlNative.KeyboardEvent.Repeat)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<SdlNative.TextEvent>(nameof(SdlNative.TextEvent.Text)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<SdlNative.MotionEvent>(nameof(SdlNative.MotionEvent.X)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<SdlNative.ButtonEvent>(nameof(SdlNative.ButtonEvent.X)).ToInt32());
        Assert.Equal(36, Marshal.OffsetOf<SdlNative.WheelEvent>(nameof(SdlNative.WheelEvent.MouseX)).ToInt32());
    }

    [Fact]
    public void CompositionCommitsUnicodeExactlyOnceAndCanCancel()
    {
        var composition = new LinuxComposition();
        var events = new List<TextCompositionEventArgs>();
        composition.Edit("zhong", events.Add);
        composition.Edit("中文😀", events.Add);
        Assert.True(composition.Commit("中文😀", events.Add));
        Assert.False(composition.Commit("next", events.Add));
        Assert.False(composition.IsActive);
        Assert.Equal(new[] { CompositionStage.Started, CompositionStage.Updated, CompositionStage.Updated, CompositionStage.Committed },
            events.Select(e => e.Stage));
        Assert.Equal("中文😀", events[^1].Text);
        composition.Edit("cancel", events.Add);
        composition.Edit("", events.Add);
        Assert.Equal(CompositionStage.Cancelled, events[^1].Stage);
        Assert.False(composition.IsActive);
    }

    [Theory]
    [InlineData(65u, 1, "A")]
    [InlineData(0x20acu, 0x4200, "€")]
    [InlineData(0x1f600u, 0, "😀")]
    [InlineData(13u, 0, null)]
    [InlineData(127u, 0, null)]
    [InlineData(0x40000050u, 0, null)]
    [InlineData(99u, 0x40, null)]
    [InlineData(99u, 0x100, null)]
    [InlineData(99u, 0x400, null)]
    public void BasicWaylandTextKeepsUnicodeAndDoesNotInsertNavigationOrShortcuts(uint keycode, int modifiers, string? expected)
        => Assert.Equal(expected, LinuxInput.BasicText(keycode, (ushort)modifiers));

    [Fact]
    public void NativeHandlesKeepDisplayAndWindowDistinct()
    {
        var x11 = NativeWindowHandle.FromXlib(12, 34, 2);
        Assert.Equal(NativeWindowKind.Xlib, x11.Kind);
        Assert.Equal((nint)12, x11.Handle);
        Assert.Equal((nint)34, x11.Display);
        Assert.Equal(2, x11.Screen);
        Assert.Equal(NativeWindowKind.Wayland, NativeWindowHandle.FromWayland(12, 34).Kind);
        Assert.Throws<ArgumentException>(() => NativeWindowHandle.FromXlib(12, 0));
        Assert.Throws<ArgumentException>(() => NativeWindowHandle.FromWayland(0, 34));
    }

    [Fact]
    public void LegacySurfaceFactoriesRejectLinuxHandlesInsteadOfReinterpretingThemAsHwnd()
    {
        ISurfaceFactory factory = new LegacyFactory();
        using var presenter = factory.Create(NativeWindowHandle.FromWin32(123), 20, 30);
        Assert.Equal(20, presenter.Width);
        Assert.Throws<PlatformNotSupportedException>(() => factory.Create(NativeWindowHandle.FromWayland(12, 34), 20, 30));
        Assert.Throws<PlatformNotSupportedException>(() => factory.Create(NativeWindowHandle.FromXlib(12, 34), 20, 30));
    }

    private sealed class LegacyFactory : ISurfaceFactory
    {
        public ISurfacePresenter Create(nint handle, int width, int height)
        {
            Assert.Equal((nint)123, handle);
            var presenter = new TestPresenter();
            presenter.Resize(width, height);
            return presenter;
        }
    }
}
