using System.Collections.Generic;
using EclipseUI.Elements;

namespace EclipseUI.Headless.Tests;

public class ContentInheritanceCard : StackPanel
{
    private IReadOnlyList<Element> _body = CreateDefaultBody();

    [UiContent]
    public IReadOnlyList<Element> Body
    {
        get => _body;
        set
        {
            BodyWrites++;
            _body = value;
        }
    }

    public int BodyWrites { get; private set; }

    protected override void InitializeUi()
    {
        base.InitializeUi();
        foreach (var child in Body) Children.Add(child);
    }

    private static IReadOnlyList<Element> CreateDefaultBody()
    {
        var text = new TextBlock();
        text.AddRun("default content");
        return new Element[] { text };
    }
}
