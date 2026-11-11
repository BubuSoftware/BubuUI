using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using Xunit;

namespace EclipseUI.Rendering.Tests;

/// <summary>
/// design/15 §15.14.7 第一条：async 续体重算必须断言到「段/像素级」，不能只断言模型。
/// D113 的教训是模型更新 + retained 段复用旧文字会让两边一起绿；这里用记录型文本
/// 绘制器锁住「drain 后 TextBlock 段确实以新文字重建」。
/// </summary>
public class UiSynchronizationContextPaintTests
{
    private static readonly Size Surface = new(360, 240);

    private sealed class AsyncTextComponent : Component
    {
        private TextRun _run = null!;
        private readonly TextBlock _output = new();

        public int Value;
        public Button Trigger { get; private set; } = null!;

        protected override Element Build()
        {
            var panel = new StackPanel();
            _run = _output.AddRun("0");
            panel.Children.Add(_output);
            Trigger = new Button { Label = "go" };
            Trigger.AddHandler(Button.ClickEvent, () => { Load(); Invalidate(); });
            panel.Children.Add(Trigger);
            return panel;
        }

        protected override void Render() => _run.Text = Value.ToString();

        private async void Load()
        {
            await Task.Yield();
            Value += 5;
        }
    }

    [Fact]
    public void AsyncContinuation_RepaintsTextBlockSegment_WithNewText()
    {
        var component = new AsyncTextComponent();
        var host = new HeadlessRoot(component) { AvailableSize = Surface };
        using var ui = host.InstallSynchronizationContext();
        var recorder = new RecordingTextPainter();
        var painter = new ElementPainter(textPainter: recorder);

        host.UpdateLayout();
        _ = painter.Build(component.Root, Surface);
        Assert.Contains("0", recorder.Painted);

        // tick N：点击 → 包装 Invalidate 渲染旧状态；retained 段不该出现 "5"。
        component.Trigger.Click();
        host.UpdateLayout();
        recorder.Painted.Clear();
        _ = painter.Build(component.Root, Surface);
        Assert.DoesNotContain("5", recorder.Painted);

        // tick N+1：drain → 整体失效 → Render 写新文字 → TextBlock 段重建。
        Assert.Equal(1, ui.Drain());
        host.UpdateLayout();
        recorder.Painted.Clear();
        _ = painter.Build(component.Root, Surface);
        Assert.Contains("5", recorder.Painted);
    }

    private sealed class RecordingTextPainter : ITextPainter
    {
        public List<string> Painted { get; } = new();

        public TextLineMetrics Measure(string text, double fontSize)
            => new(fontSize, fontSize * 0.4, text.Length * fontSize * 0.5);

        public void Paint(DisplayList list, string text, double fontSize, Point topLeft, Color color)
            => Painted.Add(text);
    }
}
