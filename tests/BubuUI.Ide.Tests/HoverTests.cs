using BubuUI.Compiler;
using BubuUI.LanguageServer.Analysis;
using Xunit;

namespace BubuUI.Ide.Tests;

/// <summary>悬停要把「这个名字是什么」答完整：CLR 类型、内容模型、属性类型、事件签名。</summary>
public class HoverTests
{
    private const string Using = "@using BubuUI.Elements\n\n";

    [Fact]
    public void TagHover_ShowsTheClrTypeAndContentModel()
    {
        var (service, uri, offset) = TestHost.Open(Using + "<Butt|on>+1</Button>");
        var hover = service.Hover(uri, offset);

        Assert.NotNull(hover);
        Assert.Contains("BubuUI.Elements.Button", hover!.Markdown);
        Assert.Contains("control", hover.Markdown);
        Assert.Contains("content: text", hover.Markdown);
    }

    [Fact]
    public void TagHover_CoversTheWholeName()
    {
        var (service, uri, offset) = TestHost.Open(Using + "<Butt|on>+1</Button>");
        var hover = service.Hover(uri, offset)!;

        Assert.Equal(Using.Length + 1, hover.Span.Start);
        Assert.Equal("Button".Length, hover.Span.Length);
    }

    [Fact]
    public void EventHover_ExplainsTheHandler()
    {
        var (service, uri, offset) = TestHost.Open(Using + "<Button OnCli|ck={() => 1}>+1</Button>");
        var hover = service.Hover(uri, offset);

        Assert.NotNull(hover);
        Assert.Contains("OnClick", hover!.Markdown);
        Assert.Contains("event", hover.Markdown);
        Assert.Contains("RoutedEventArgs", hover.Markdown);
    }

    [Fact]
    public void PropertyHover_ShowsTheType()
    {
        var (service, uri, offset) = TestHost.Open(Using + "<Button Lab|el=\"x\" />");
        var hover = service.Hover(uri, offset);

        Assert.NotNull(hover);
        Assert.Contains("Label", hover!.Markdown);
        Assert.Contains("string", hover.Markdown);
    }

    [Fact]
    public void AttachedPropertyHover_ShowsTheOwner()
    {
        var (service, uri, offset) = TestHost.Open(Using + "<Button Grid.R|ow=\"1\" />");
        var hover = service.Hover(uri, offset);

        Assert.NotNull(hover);
        Assert.Contains("Grid.Row", hover!.Markdown);
        Assert.Contains("attached property", hover.Markdown);
    }

    [Fact]
    public void EnumValueHover_ListsTheMembers()
    {
        var catalog = TestHost.EnumCatalog(out _);
        var text = "<Box Align=\"Le|\" />";
        var offset = text.IndexOf('|');
        var analysis = DocumentAnalysis.Create(text.Remove(offset, 1), "Probe.bui", catalog);

        var hover = HoverProvider.Hover(analysis, catalog, offset);
        Assert.NotNull(hover);
        Assert.Contains("Center, Left, Right", hover!.Markdown);
    }

    /// <summary>design/19 §9：Style 值位置悬停给值形态——enum 属性列出成员表。</summary>
    [Fact]
    public void StylePropertyHover_DescribesKindAndEnumMembers()
    {
        var tone = new StylePropertyDef("Tone", StyleValueKind.Enum,
            "global::Probe.BadgeStyles.ToneProperty", ResourceField: null,
            "global::Probe.BadgeStyles", IsBuiltIn: false,
            "global::Probe.Tone", new[] { "Danger", "Neutral", "Success" });
        var catalog = new UiCatalog(TestHost.Catalog.Elements, Array.Empty<UiHiddenTag>(),
            Array.Empty<UiMetadataProblem>(),
            styleProperties: new StylePropertyCatalog(new[] { tone }, Array.Empty<UiMetadataProblem>()));

        var text = Using + "<Button Style=\"Tone|: 6\" />";
        var offset = text.IndexOf('|');
        var analysis = DocumentAnalysis.Create(text.Remove(offset, 1), "Probe.bui", catalog);

        var hover = HoverProvider.Hover(analysis, catalog, offset);
        Assert.NotNull(hover);
        Assert.Contains("Style:Tone", hover!.Markdown);
        Assert.Contains("one of: Danger, Neutral, Success", hover.Markdown);
    }

    [Fact]
    public void DirectiveHover_DocumentsTheBlock()
    {
        var (service, uri, offset) = TestHost.Open("@i|f (true) { }");
        var hover = service.Hover(uri, offset);

        Assert.NotNull(hover);
        Assert.Contains("@if", hover!.Markdown);
        Assert.Contains("else", hover.Markdown);
    }

    [Fact]
    public void UnknownTag_HasNoHover()
    {
        var (service, uri, offset) = TestHost.Open(Using + "<Nope| />");
        Assert.Null(service.Hover(uri, offset));
    }

    [Fact]
    public void PlainText_HasNoHover()
    {
        var (service, uri, offset) = TestHost.Open(Using + "<Button>he|llo</Button>");
        Assert.Null(service.Hover(uri, offset));
    }
}
