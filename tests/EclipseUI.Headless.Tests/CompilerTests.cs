using System.Collections.Generic;
using System.Linq;
using EclipseUI.Compiler;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class CompilerTests
{
    private const string CounterUi = """
        <StackPanel Gap="8">
            <TextBlock FontSize="30">You pressed {count} times</TextBlock>
            <CheckBox Checked:bind={showButton}>Button visible</CheckBox>
            @if (showButton) {
                <Button OnClick={() => count.value++}>+1</Button>
            }
        </StackPanel>
        """;

    private static (UiDocument Doc, List<UiDiagnostic> Diags) Parse(string text, bool analyze = false)
    {
        var diags = new List<UiDiagnostic>();
        var doc = FrontEnd.Parse(text, "Test.ui", diags);
        if (analyze) FrontEnd.Analyze(doc, UiCatalog.CreateDefault(), diags);
        return (doc, diags);
    }

    [Fact]
    public void Parses_CounterTemplate()
    {
        var (doc, diags) = Parse(CounterUi, analyze: true);
        Assert.Empty(diags);

        var root = Assert.IsType<UiElement>(doc.Nodes.Single(n => n is UiElement));
        Assert.Equal("StackPanel", root.Tag);
        Assert.Equal("8", root.Attributes.Single(a => a.Name == "Gap").Value);

        var textBlock = Assert.IsType<UiElement>(root.Children[0]);
        Assert.Equal("TextBlock", textBlock.Tag);
        Assert.Equal("30d", textBlock.Attributes.Single(a => a.Name == "FontSize").ConvertedLiteral);
        var text = Assert.IsType<UiText>(textBlock.Children.Single());
        Assert.Equal(3, text.Segments.Count);
        Assert.Equal("You pressed ", text.Segments[0].Text);
        Assert.Equal("count", text.Segments[1].Text);
        Assert.Equal(" times", text.Segments[2].Text);

        var checkbox = Assert.IsType<UiElement>(root.Children[1]);
        var bindAttr = checkbox.Attributes.Single(a => a.Name == "Checked");
        Assert.True(bindAttr.IsBind);
        Assert.Equal("showButton", bindAttr.Value);
        Assert.Equal("Button visible", Assert.IsType<UiText>(checkbox.Children[0]).Segments.Single().Text);

        var ifBlock = Assert.IsType<UiIf>(root.Children[2]);
        Assert.Equal("showButton", ifBlock.Condition);
        var button = Assert.IsType<UiElement>(ifBlock.Children.Single(c => c is UiElement));
        Assert.Equal("() => count.value++", button.Attributes.Single(a => a.Name == "OnClick").Value);
        Assert.Equal("+1", Assert.IsType<UiText>(button.Children[0]).Segments.Single().Text);
    }

    [Fact]
    public void Lambda_WithGt_IsNotTruncated()
    {
        var (doc, diags) = Parse("""<Button OnClick={() => a > b}>x</Button>""", analyze: true);
        Assert.Empty(diags);
        var button = Assert.IsType<UiElement>(doc.Nodes.Single());
        Assert.Equal("() => a > b", button.Attributes[0].Value);
    }

    [Fact]
    public void UnknownTag_ProducesSuggestion()
    {
        var (doc, diags) = Parse("""<StackPanl Gap="8" />""", analyze: true);
        Assert.Contains(diags, d => d.Code == UiDiagnostics.UnknownTag && d.Message.Contains("StackPanel"));
    }

    [Fact]
    public void Literal_FormatError_StatesExpectedFormat()
    {
        var (doc, diags) = Parse("""<StackPanel Gap="eight" />""", analyze: true);
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.LiteralFormat, diag.Code);
        Assert.Contains("number", diag.Message);
    }

    [Fact]
    public void UnknownProperty_IsRejected()
    {
        var (doc, diags) = Parse("""<StackPanel Gapx="8" />""", analyze: true);
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.UnknownProperty, diag.Code);
        Assert.Contains("Gap", diag.Message); // 修复建议
    }

    [Fact]
    public void TextInsideContainer_IsRejected()
    {
        var (doc, diags) = Parse("""<StackPanel>hello</StackPanel>""", analyze: true);
        Assert.Contains(diags, d => d.Code == UiDiagnostics.TextNotAllowed);
    }

    [Fact]
    public void KeyOutsideForeachRoot_IsRejected()
    {
        var (doc, diags) = Parse("""<StackPanel><TextBlock Key={x}>t</TextBlock></StackPanel>""", analyze: true);
        Assert.Contains(diags, d => d.Code == UiDiagnostics.KeyMisuse);
    }

    [Fact]
    public void Literal_IsConvertedToTypedExpression()
    {
        var (doc, diags) = Parse("""<StackPanel Gap="8" />""", analyze: true);
        Assert.Empty(diags);
        var attr = doc.Nodes.OfType<UiElement>().Single().Attributes[0];
        Assert.Equal("8d", attr.ConvertedLiteral);
    }
}
