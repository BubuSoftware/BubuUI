using System.Runtime.InteropServices;
using EclipseUI.Platform.MacOS;
using EclipseUI.Rendering;
using Xunit;

namespace EclipseUI.Platform.MacOS.Tests;

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
        => Assert.Equal(expected, MacOSInput.Key(code));

    [Theory]
    [InlineData(0x0001)]
    [InlineData(0x0002)]
    public void BothSidesOfModifiersWork(int shift)
    {
        Assert.Equal(KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt,
            MacOSInput.Modifiers((ushort)(shift | 0x0040 | 0x0200)));
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Alt, MacOSInput.Modifiers(0x0180));
        Assert.Equal(KeyModifiers.None, MacOSInput.Modifiers(0x6000)); // Lock/mode flags
    }

    [Fact]
    public void WindowCoordinatesUseBothPixelDensityAndContentScale()
    {
        Assert.Equal(new Point(80, 40), MacOSInput.ToLogical(100, 50, 1, 1, 1.25)); // Scaled content
        Assert.Equal(new Point(100, 50), MacOSInput.ToLogical(100, 50, 2, 2, 2)); // Retina backing pixels
        Assert.Equal(60, MacOSInput.WheelDelta(.5f, 0));
        Assert.Equal(-60, MacOSInput.WheelDelta(.5f, 1));
        Assert.Equal(PointerButton.Middle, MacOSInput.Button(2));
        Assert.Null(MacOSInput.Button(4));
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
        var composition = new MacOSComposition();
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
    [InlineData(0x0400)]
    [InlineData(0x0800)]
    public void CommandRemainsDistinctFromControl(int command)
    {
        Assert.Equal(KeyModifiers.Meta, MacOSInput.Modifiers((ushort)command));
        Assert.Equal(KeyModifiers.Meta | KeyModifiers.Control | KeyModifiers.Shift,
            MacOSInput.Modifiers((ushort)(command | 0x0040 | 0x0001)));
    }

    [Fact]
    public void AppKitHandleBorrowsAViewWithoutADisplay()
    {
        var handle = NativeWindowHandle.FromAppKit(123);
        Assert.Equal(NativeWindowKind.AppKit, handle.Kind);
        Assert.Equal((nint)123, handle.Handle);
        Assert.Equal((nint)0, handle.Display);
        Assert.Throws<ArgumentException>(() => NativeWindowHandle.FromAppKit(0));
    }

    [Fact]
    public void LegacySurfaceFactoriesRejectAppKitHandlesInsteadOfReinterpretingThemAsHwnd()
    {
        ISurfaceFactory factory = new LegacyFactory();
        using var presenter = factory.Create(NativeWindowHandle.FromWin32(123), 20, 30);
        Assert.Equal(20, presenter.Width);
        Assert.Throws<PlatformNotSupportedException>(() => factory.Create(NativeWindowHandle.FromAppKit(123), 20, 30));
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
