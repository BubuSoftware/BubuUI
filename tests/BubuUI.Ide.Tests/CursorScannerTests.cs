using BubuUI.LanguageServer.Analysis;
using Xunit;

namespace BubuUI.Ide.Tests;

/// <summary>
/// 光标上下文是补全/悬停的分派依据，也是最容易被"半截 token"打穿的一层。
/// 每个用例都是"文档写到一半 + 光标停在某处"的真实形态。
/// </summary>
public class CursorScannerTests
{
    private static CursorContext At(string textWithMarker)
    {
        var offset = textWithMarker.IndexOf('|');
        Assert.True(offset >= 0, "marker '|' required");
        return CursorScanner.Scan(textWithMarker.Remove(offset, 1), offset);
    }

    [Fact]
    public void TagName_WithPartialWord()
    {
        var context = At("<But|");
        Assert.Equal(CursorKind.TagName, context.Kind);
        Assert.Equal("But", context.Prefix);
        Assert.Equal(1, context.TokenStart);
    }

    [Fact]
    public void AttributeName_AfterWhitespace()
    {
        var context = At("<StackPanel |");
        Assert.Equal(CursorKind.AttributeName, context.Kind);
        Assert.Equal("StackPanel", context.TagName);
        Assert.Equal("", context.Prefix);
    }

    [Fact]
    public void AttributeName_WithPartialWord()
    {
        var context = At("<Button OnCl|");
        Assert.Equal(CursorKind.AttributeName, context.Kind);
        Assert.Equal("OnCl", context.Prefix);
        Assert.Equal("<Button ".Length, context.TokenStart);
    }

    [Theory]
    [InlineData("<Button |Label=\"x\" />", "Label")]
    [InlineData("<Button |OnClick={() => {}} />", "OnClick")]
    [InlineData("<Button |Grid.Row=\"1\" />", "Grid.Row")]
    [InlineData("<CheckBox |@bind-Checked={flag} />", "@bind-Checked")]
    [InlineData("<Section |Title=\"demo\" />", "Title")]
    [InlineData("<MainWindow |W=\"800\" />", "W")]
    [InlineData("<Button Label=\"x\" |Width=\"100\" />", "Width")]
    [InlineData("<Button\r\n\t |Label=\"x\" />", "Label")]
    public void AttributeName_AtFirstCharacterResolvesTheWholeName(string marked, string name)
    {
        var context = At(marked);
        Assert.Equal(CursorKind.AttributeName, context.Kind);
        Assert.Equal(name, context.AttributeName);
        Assert.Equal(marked.IndexOf('|'), context.TokenStart);
        Assert.Empty(context.Prefix);
    }

    [Theory]
    [InlineData("<Button | Label=\"x\" />")]
    [InlineData("<Button |")]
    [InlineData("<Button |/>")]
    public void AttributeName_InWhitespaceKeepsAnEmptyName(string marked)
    {
        var context = At(marked);
        Assert.Equal(CursorKind.AttributeName, context.Kind);
        Assert.Empty(context.AttributeName);
        Assert.Empty(context.Prefix);
    }

    [Fact]
    public void BindAttribute_KeepsThePrefixInTheToken()
    {
        var context = At("<CheckBox @bind-Che|");
        Assert.Equal(CursorKind.AttributeName, context.Kind);
        Assert.Equal("@bind-Che", context.Prefix);
        Assert.Equal("<CheckBox ".Length, context.TokenStart);
    }

    [Fact]
    public void AttributeValue_InsideQuotes()
    {
        var context = At("<Button Label=\"he|llo\" />");
        Assert.Equal(CursorKind.AttributeValue, context.Kind);
        Assert.Equal("Button", context.TagName);
        Assert.Equal("Label", context.AttributeName);
        Assert.Equal("he", context.Prefix);
    }

    [Fact]
    public void AttributeExpression_KnowsItsAttribute()
    {
        var context = At("<Button OnClick={() => cou|nt++}>+1</Button>");
        Assert.Equal(CursorKind.Expression, context.Kind);
        Assert.Equal("Button", context.TagName);
        Assert.Equal("OnClick", context.AttributeName);
        Assert.True(context.IsAttributeExpression);
    }

    [Fact]
    public void TextExpression_IsAnExpression()
    {
        var context = At("<TextBlock>You pressed {cou|nt} times</TextBlock>");
        Assert.Equal(CursorKind.Expression, context.Kind);
        Assert.False(context.IsAttributeExpression);
    }

    [Fact]
    public void CharLiteralInsideExpression_DoesNotEndTheExpression()
    {
        var context = At("<TextBlock>{ '}' + x| }</TextBlock>");
        Assert.Equal(CursorKind.Expression, context.Kind);
    }

    [Fact]
    public void AfterExpression_IsPlainText()
    {
        var context = At("<TextBlock>{1 + 2}b|</TextBlock>");
        Assert.Equal(CursorKind.Text, context.Kind);
    }

    [Fact]
    public void Directive_WithPartialWord()
    {
        var context = At("@i|");
        Assert.Equal(CursorKind.Directive, context.Kind);
        Assert.Equal("@i", context.Prefix);
    }

    [Fact]
    public void UsingDirectiveArgument_IsItsOwnContext()
    {
        var context = At("@using BubuUI.Ele|");
        Assert.Equal(CursorKind.DirectiveArgument, context.Kind);
        Assert.Equal("using", context.Directive);
        Assert.Equal("BubuUI.Ele", context.Prefix);
    }

    [Fact]
    public void IfCondition_IsAnExpression()
    {
        var context = At("@if (cou|nt > 0) { }");
        Assert.Equal(CursorKind.Expression, context.Kind);
        Assert.Equal("if", context.Directive);
    }

    [Fact]
    public void BlockBrace_DoesNotStartAnExpression()
    {
        // 曾经的坑：@if(...) 后面的 '{' 被当成文本插值，导致块内整段失联。
        var context = At("@if (count > 0) {\n    <But|");
        Assert.Equal(CursorKind.TagName, context.Kind);
        Assert.Equal("But", context.Prefix);
    }

    [Fact]
    public void MultiLineBlock_KeepsMarkupInsideTheBlock()
    {
        var context = At("@foreach (var row in rows)\n{\n    <TextBlock>{row.Na|");
        Assert.Equal(CursorKind.Expression, context.Kind);
    }

    [Fact]
    public void ElseContinuation_DoesNotBreakTheFollowingBlock()
    {
        var context = At("@if (a) {\n  <Button/>\n} else {\n  <Butt|");
        Assert.Equal(CursorKind.TagName, context.Kind);
    }

    [Fact]
    public void ClosingTag_ExposesTheOpenElementStack()
    {
        var context = At("<StackPanel>\n    <Button>\n    </|");
        Assert.Equal(CursorKind.EndTagName, context.Kind);
        Assert.Contains("StackPanel", context.OpenTags);
        Assert.Contains("Button", context.OpenTags);
    }

    [Fact]
    public void SelfClosingTag_IsNotPushedOnTheStack()
    {
        var context = At("<StackPanel>\n    <Button />\n    <TextBlock>\n    </|");
        Assert.Equal(CursorKind.EndTagName, context.Kind);
        Assert.Equal(new[] { "StackPanel", "TextBlock" }, context.OpenTags);
    }

    [Fact]
    public void CommentsAreOpaque()
    {
        Assert.Equal(CursorKind.Text, At("<!-- <But| -->").Kind);
        Assert.Equal(CursorKind.Text, At("@* <But| *@").Kind);
    }

    [Fact]
    public void CodeBlockIsIdentifiedNotOpaque()
    {
        // @code 块内是真实 C# 文本（跳转定义在成员树上解析）；@style 仍按文本。
        Assert.Equal(CursorKind.CodeBlock, At("@code {\n    public int cou|nt;\n}").Kind);
        Assert.Equal(CursorKind.Text, At("@style {\n    .a { color: re|d }\n}").Kind);
    }

    [Fact]
    public void OffsetBeyondTheEndIsClamped()
    {
        var context = CursorScanner.Scan("<Button>", 999);
        Assert.Equal(CursorKind.Text, context.Kind);
    }
}
