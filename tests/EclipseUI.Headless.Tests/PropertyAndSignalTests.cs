using System;
using EclipseUI;
using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class PropertyEngineTests
{
    [Fact]
    public void DefaultValue_IsReturned_WhenNotSet()
    {
        var panel = new StackPanel();
        Assert.Equal(0d, panel.Gap);
    }

    [Fact]
    public void SetValue_ShortCircuits_EqualValues()
    {
        var panel = new StackPanel();
        panel.Gap = 8;
        int notifications = 0;
        panel.PropertyChanged += (_, _) => notifications++;
        panel.Gap = 8;
        Assert.Equal(0, notifications);
        panel.Gap = 12;
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void InheritedProperty_FlowsToChildren()
    {
        var block = new TextBlock();
        var run = new TextRun("hi");
        block.AddRun(run);

        Assert.Equal(14d, run.GetValue(TextBlock.FontSizeProperty));
        block.FontSize = 30;
        Assert.Equal(30d, run.GetValue(TextBlock.FontSizeProperty));
    }

    [Fact]
    public void Registry_RejectsDuplicateRegistration()
    {
        var existing = StackPanelBase.GapProperty; // 确保原注册已发生（类型初始化时注册）
        Assert.NotNull(existing);
        Assert.Throws<InvalidOperationException>(() =>
            StyledProperty<double>.Register<StackPanelBase>("Gap", 0d));
    }
}

public class SignalTests
{
    [Fact]
    public void Handler_Fires_OnChange_Not_OnSameValue()
    {
        var signal = new Signal<int>(0);
        int fired = 0;
        using var subscription = signal.Subscribe(() => fired++);
        signal.Value = 1;
        signal.Value = 1;
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Dispose_StopsNotifications()
    {
        var signal = new Signal<int>(0);
        int fired = 0;
        var subscription = signal.Subscribe(() => fired++);
        signal.Value = 1;
        subscription.Dispose();
        signal.Value = 2;
        Assert.Equal(1, fired);
    }

    [Fact]
    public void ReentrantSet_IsSafe()
    {
        var a = new Signal<int>(0);
        var b = new Signal<int>(0);
        using var _ = a.Subscribe(() => { if (a.Value == 1) b.Value = 10; });
        a.Value = 1;
        Assert.Equal(10, b.Value);
    }

    [Fact]
    public void DisposingDuringNotification_SkipsDisposedHandlers()
    {
        var signal = new Signal<int>(0);
        int firstCount = 0, secondCount = 0;
        IDisposable? second = null;
        var first = signal.Subscribe(() =>
        {
            firstCount++;
            second?.Dispose(); // 通知期间退订下一个处理器
        });
        second = signal.Subscribe(() => secondCount++);

        signal.Value = 1;
        Assert.Equal(1, firstCount);
        Assert.Equal(0, secondCount); // 已退订者不再触发
        signal.Value = 2;
        Assert.Equal(2, firstCount);
        Assert.Equal(0, secondCount);
    }

    [Fact]
    public void SubscriberCount_ReflectsSubscriptions()
    {
        var signal = new Signal<int>(0);
        Assert.Equal(0, signal.SubscriberCount);
        var sub1 = signal.Subscribe(() => { });
        Assert.Equal(1, signal.SubscriberCount);
        var sub2 = signal.Subscribe(() => { });
        Assert.Equal(2, signal.SubscriberCount);
        sub1.Dispose();
        Assert.Equal(1, signal.SubscriberCount);
        sub2.Dispose();
        Assert.Equal(0, signal.SubscriberCount);
    }
}

public class RoutedEventTests
{
    [Fact]
    public void Click_BubblesFromChildToParent()
    {
        var panel = new StackPanel();
        var button = new Button();
        panel.Children.Add(button);

        int hits = 0;
        panel.AddHandler(Button.ClickEvent, () => hits++);

        button.Click();
        Assert.Equal(1, hits);
    }

    [Fact]
    public void HandledEvent_StopsBubbling()
    {
        var panel = new StackPanel();
        var button = new Button();
        panel.Children.Add(button);

        int hits = 0;
        panel.AddHandler(Button.ClickEvent, () => hits++);

        var args = new RoutedEventArgs(Button.ClickEvent) { Handled = true };
        button.RaiseEvent(args);
        Assert.Equal(0, hits);
    }
}
