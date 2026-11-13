using EclipseUI.Elements;

namespace EclipseUI.Headless.Tests;

/// <summary>纯 C# 控件，与 .ui 生成控件使用同一基类与子项集合。</summary>
public class CodeCard : StackPanel
{
    public CodeCard()
    {
        Output = new TextBlock();
        Output.AddRun("code: 0");
        Trigger = new Button { Label = "code increment" };
        Trigger.AddHandler(Button.ClickEvent, () =>
        {
            Count++;
            Invalidate();
        });
        Children.Add(Output);
        Children.Add(Trigger);
    }

    public int Count { get; private set; }
    public TextBlock Output { get; }
    public Button Trigger { get; }

    protected override void Render() => Output.Runs[0].Text = $"code: {Count}";
}
