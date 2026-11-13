using System.Collections.Generic;
using EclipseUI.Elements;

namespace EclipseUI.Headless.Tests;

/// <summary>基类初始化会消费模板传入的属性与内容，验证根与子控件采用相同输入顺序。</summary>
public class RootInputCard : StackPanel
{
    [UiParameter] public string Title { get; set; } = "default";
    [UiContent] public IReadOnlyList<Element>? Body { get; set; }
    public string? CapturedTitle { get; private set; }

    protected override void InitializeUi()
    {
        base.InitializeUi();
        CapturedTitle = Title;
        if (Body is not null)
            foreach (var child in Body) Children.Add(child);
    }
}

public class RootSlotCard : StackPanel
{
    [UiSlot("Header")] public Element? Header { get; set; }

    protected override void InitializeUi()
    {
        base.InitializeUi();
        if (Header is not null) Children.Add(Header);
    }
}
