using System.Collections.Generic;
using System.Linq;
using EclipseUI.Compiler;
using EclipseUI.TestKit;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class CompilerTests
{
    private const string CounterUi = """
        @using EclipseUI.Elements
        <StackPanel Gap="8">
            <TextBlock FontSize="30">You pressed {count} times</TextBlock>
            <CheckBox @bind-Checked={showButton}>Button visible</CheckBox>
            @if (showButton) {
                <Button OnClick={() => count++}>+1</Button>
            }
        </StackPanel>
        """;

    private static (UiDocument Doc, List<UiDiagnostic> Diags) Parse(string text, bool analyze = false)
    {
        var diags = new List<UiDiagnostic>();
        var doc = FrontEnd.Parse(text, "Test.ui", diags);
        if (analyze) FrontEnd.Analyze(doc, UiTestCatalogs.BuiltIn(), diags);
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
        Assert.Equal("() => count++", button.Attributes.Single(a => a.Name == "OnClick").Value);
        Assert.Equal("+1", Assert.IsType<UiText>(button.Children[0]).Segments.Single().Text);
    }

    [Fact]
    public void Lambda_WithGt_IsNotTruncated()
    {
        var (doc, diags) = Parse("@using EclipseUI.Elements\n" + """<Button OnClick={() => a > b}>x</Button>""", analyze: true);
        Assert.Empty(diags);
        var button = Assert.IsType<UiElement>(doc.Nodes.OfType<UiElement>().Single());
        Assert.Equal("() => a > b", button.Attributes[0].Value);
    }

    [Fact]
    public void UnknownTag_ProducesSuggestion()
    {
        var (doc, diags) = Parse("@using EclipseUI.Elements\n" + """<StackPanl Gap="8" />""", analyze: true);
        Assert.Contains(diags, d => d.Code == UiDiagnostics.UnknownTag && d.Message.Contains("StackPanel"));
    }

    [Fact]
    public void Literal_FormatError_StatesExpectedFormat()
    {
        var (doc, diags) = Parse("@using EclipseUI.Elements\n" + """<StackPanel Gap="eight" />""", analyze: true);
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.LiteralFormat, diag.Code);
        Assert.Contains("number", diag.Message);
    }

    [Fact]
    public void UnknownProperty_IsRejected()
    {
        var (doc, diags) = Parse("@using EclipseUI.Elements\n" + """<StackPanel Gapx="8" />""", analyze: true);
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.UnknownProperty, diag.Code);
        Assert.Contains("Gap", diag.Message); // 修复建议
    }

    [Fact]
    public void TextInsideContainer_IsRejected()
    {
        var (doc, diags) = Parse("@using EclipseUI.Elements\n" + """<StackPanel>hello</StackPanel>""", analyze: true);
        Assert.Contains(diags, d => d.Code == UiDiagnostics.TextNotAllowed);
    }

    [Fact]
    public void KeyOutsideForeachRoot_IsRejected()
    {
        var (doc, diags) = Parse("@using EclipseUI.Elements\n" + """<StackPanel><TextBlock Key={x}>t</TextBlock></StackPanel>""", analyze: true);
        Assert.Contains(diags, d => d.Code == UiDiagnostics.KeyMisuse);
    }

    [Fact]
    public void KeyInsideIf_IsRejected()
    {
        var (_, diags) = Parse(
            "@using EclipseUI.Elements\n" + """<StackPanel>@if (show) { <TextBlock Key={x}>a</TextBlock> }</StackPanel>""", analyze: true);
        Assert.Contains(diags, d => d.Code == UiDiagnostics.KeyMisuse);
    }

    [Fact]
    public void EmptyKey_IsRejected()
    {
        var (_, diags) = Parse(
            "@using EclipseUI.Elements\n" + """<StackPanel>@foreach (var t in ts) { <TextBlock Key={}>a</TextBlock> }</StackPanel>""", analyze: true);
        Assert.Contains(diags, d => d.Code == UiDiagnostics.KeyMisuse);
    }

    [Fact]
    public void TextContent_AndExplicitSameTarget_IsRejected()
    {
        var (_, diags) = Parse("@using EclipseUI.Elements\n" + """<Button Label="go">go</Button>""", analyze: true);
        Assert.Contains(diags, d => d.Code == UiDiagnostics.SameTargetDuplicate);
    }

    [Fact]
    public void PlainValue_AndBind_OnSameTarget_IsRejected()
    {
        var (_, diags) = Parse("@using EclipseUI.Elements\n" + """<CheckBox Checked={x} @bind-Checked={x}>c</CheckBox>""", analyze: true);
        Assert.Contains(diags, d => d.Code == UiDiagnostics.SameTargetDuplicate);
    }

    [Fact]
    public void BindNewSyntax_AndSameTargetPlainValue_IsRejected()
    {
        var (doc, diags) = Parse("@using EclipseUI.Elements\n" + """<CheckBox Checked={x} @bind-Checked={y}>c</CheckBox>""", analyze: true);
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.SameTargetDuplicate, diag.Code);
        Assert.True(doc.Nodes.OfType<UiElement>().Single().Attributes.Where(a => a.Name == "Checked").Last().IsBind);
    }

    [Fact]
    public void BindNewSyntax_IsParsedAsBindingOnStrippedName()
    {
        var (doc, diags) = Parse("@using EclipseUI.Elements\n" + """<CheckBox @bind-Checked={showButton}>c</CheckBox>""", analyze: true);
        Assert.Empty(diags);
        var attr = doc.Nodes.OfType<UiElement>().Single().Attributes.Single();
        Assert.Equal("Checked", attr.Name);
        Assert.Equal(UiBindForm.TwoWay, attr.BindForm);
        Assert.True(attr.IsBind);
        Assert.Equal(AttrValueKind.Expression, attr.Kind);
        Assert.Equal("showButton", attr.Value);
    }

    [Fact]
    public void LegacyBind_IsEquivalentToNewSyntax_ButWarns()
    {
        var (doc, diags) = Parse("@using EclipseUI.Elements\n" + """<CheckBox Checked:bind={showButton}>c</CheckBox>""", analyze: true);
        var attr = doc.Nodes.OfType<UiElement>().Single().Attributes.Single();
        Assert.Equal("Checked", attr.Name);
        Assert.True(attr.IsBind);
        Assert.True(attr.IsLegacy);
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.LegacySyntaxRemoved, diag.Code);
        Assert.Equal(DiagnosticSeverity.Error, diag.Severity);
        Assert.Contains("@bind-Checked", diag.Message);
    }

    [Fact]
    public void LegacyValue_IsAliasOfPlainAssignment_AndWarns()
    {
        var (doc, diags) = Parse("@using EclipseUI.Elements\n" + """<StackPanel Gap:value="8" />""", analyze: true);
        var attr = doc.Nodes.OfType<UiElement>().Single().Attributes.Single();
        Assert.Equal("Gap", attr.Name);
        Assert.False(attr.IsBind);
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.LegacySyntaxRemoved, diag.Code);
        Assert.Equal(DiagnosticSeverity.Error, diag.Severity);
        Assert.Contains(":value", diag.Message);
    }

    [Fact]
    public void Bind_WithLiteralValue_IsRejected()
    {
        var (_, diags) = Parse("@using EclipseUI.Elements\n" + """<CheckBox @bind-Checked="true">c</CheckBox>""", analyze: true);
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.BindSourceInvalid, diag.Code);
        Assert.Equal(DiagnosticSeverity.Error, diag.Severity);
    }

    [Fact]
    public void Bind_OnEvent_IsRejected()
    {
        var (_, diags) = Parse("@using EclipseUI.Elements\n" + """<Button @bind-OnClick={() => x()}>go</Button>""", analyze: true);
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.BindTargetInvalid, diag.Code);
        Assert.Contains("event", diag.Message);
    }

    [Fact]
    public void Bind_OnReservedAttribute_IsRejected()
    {
        var (_, diags) = Parse("@using EclipseUI.Elements\n" + """<StackPanel><TextBlock @bind-Key={x}>t</TextBlock></StackPanel>""", analyze: true);
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.BindTargetInvalid, diag.Code);
    }

    [Fact]
    public void Bind_OnUnknownProperty_IsUnknownProperty()
    {
        var (_, diags) = Parse("@using EclipseUI.Elements\n" + """<StackPanel @bind-Widths={x} />""", analyze: true);
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.UnknownProperty, diag.Code);
    }

    [Fact]
    public void Bind_IsCaseSensitive()
    {
        var (_, diags) = Parse("@using EclipseUI.Elements\n" + """<CheckBox @bind-checked={x}>c</CheckBox>""", analyze: true);
        Assert.Contains(diags, d => d.Code == UiDiagnostics.UnknownProperty);
    }

    [Fact]
    public void Bind_WithBlankBeforeName_IsSyntaxError()
    {
        var (_, diags) = Parse("@using EclipseUI.Elements\n" + """<CheckBox @bind- Checked={x}>c</CheckBox>""", analyze: true);
        Assert.Contains(diags, d => d.Code == UiDiagnostics.Syntax && d.Message.Contains("@bind-"));
    }

    [Fact]
    public void Bind_WithLegacyModifier_IsSyntaxError()
    {
        var (_, diags) = Parse("@using EclipseUI.Elements\n" + """<CheckBox @bind-Checked:bind={x}>c</CheckBox>""", analyze: true);
        Assert.Contains(diags, d => d.Code == UiDiagnostics.Syntax && d.Message.Contains("no legacy modifier"));
    }

    [Fact]
    public void Bind_Attribute_ScansOnlyInMarkupMode()
    {
        // 文本模式里的 @bind 仍是未知指令，不是属性形态
        var (_, diags) = Parse("@using EclipseUI.Elements\n" + """<TextBlock>@bind(Checked)</TextBlock>""", analyze: true);
        Assert.Contains(diags, d => d.Code == UiDiagnostics.UnknownDirective);
    }

    [Fact]
    public void Literal_IsConvertedToTypedExpression()
    {
        var (doc, diags) = Parse("@using EclipseUI.Elements\n" + """<StackPanel Gap="8" />""", analyze: true);
        Assert.Empty(diags);
        var attr = doc.Nodes.OfType<UiElement>().Single().Attributes[0];
        Assert.Equal("8d", attr.ConvertedLiteral);
    }
}
