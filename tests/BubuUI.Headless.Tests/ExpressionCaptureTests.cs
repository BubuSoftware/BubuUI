using System.Collections.Generic;
using System.Linq;
using BubuUI.Compiler;
using Xunit;

namespace BubuUI.Headless.Tests;

/// <summary>
/// D11：<c>{expr}</c> / <c>@code</c> / 指令括号的配对扫描必须是 C# 词法感知的。
/// 手写"看到引号就跳到下一个引号"的老实现会在注释、verbatim/raw 字符串上把 <c>{ }</c> 数错，
/// 导致表达式被截断（表现为莫名其妙的 EUI100 或把后半段当文本）。
/// </summary>
public class ExpressionCaptureTests
{
    private static (UiDocument Doc, List<UiDiagnostic> Diags) Parse(string text)
    {
        var diags = new List<UiDiagnostic>();
        return (FrontEnd.Parse(text, "Test.bui", diags), diags);
    }

    /// <summary>取出模板里 TextBlock 内第一个表达式段的原文。</summary>
    private static string CapturedExpression(string ui)
    {
        var (doc, diags) = Parse(ui);
        Assert.Empty(diags);
        var root = Assert.IsType<UiElement>(doc.Nodes.Single(n => n is UiElement));
        var textBlock = Assert.IsType<UiElement>(root.Children.Single());
        var text = Assert.IsType<UiText>(textBlock.Children.Single(c => c is UiText));
        var segment = text.Segments.Single(s => s.Kind == TextSegmentKind.Expression);
        return segment.Text;
    }

    private static string TextBlockWrapped(string expression) =>
        "<StackPanel><TextBlock>{" + expression + "}</TextBlock></StackPanel>";

    [Fact]
    public void BlockComment_WithUnbalancedBrace_DoesNotEndTheExpression()
    {
        Assert.Equal("x /* } */ + y", CapturedExpression(TextBlockWrapped("x /* } */ + y")));
    }

    [Fact]
    public void LineComment_IsSkippedToEndOfLine()
    {
        var expression = "x // } 不是闭合\n + y";
        Assert.Equal(expression, CapturedExpression(TextBlockWrapped(expression)));
    }

    [Fact]
    public void VerbatimString_WithDoubledQuoteAndBrace_IsKeptIntact()
    {
        // C# 源文本：@"a""}""b" —— verbatim 串里 "" 是转义引号，中间的 } 属于字符串内容。
        const string expression = "@\"a\"\"}\"\"b\"";
        Assert.Equal(expression, CapturedExpression(TextBlockWrapped(expression)));
    }

    [Fact]
    public void RawString_WithUnbalancedBrace_IsKeptIntact()
    {
        const string expression = "\"\"\"a}b\"\"\"";
        Assert.Equal(expression, CapturedExpression(TextBlockWrapped(expression)));
    }

    [Fact]
    public void InterpolatedString_WithFormatSpecifier_IsCapturedAsOneExpression()
    {
        Assert.Equal("$\"{price:F2}\"", CapturedExpression(TextBlockWrapped("$\"{price:F2}\"")));
    }

    [Fact]
    public void InterpolatedString_WithNestedStringLiteral_IsCapturedInFull()
    {
        const string expression = "$\"{(flag ? \"a\" : \"b\")}\"";
        Assert.Equal(expression, CapturedExpression(TextBlockWrapped(expression)));
    }

    [Fact]
    public void CharLiteral_WithEscapedQuote_IsSkipped()
    {
        Assert.Equal("c == '\\''", CapturedExpression(TextBlockWrapped("c == '\\''")));
    }

    [Fact]
    public void LambdaBlockBody_StillBalancesBraces()
    {
        const string expression = "items.Where(i => { return i > 0; }).Count()";
        Assert.Equal(expression, CapturedExpression(TextBlockWrapped(expression)));
    }

    [Fact]
    public void UnclosedExpression_ReportsEUI100_AndStopsAtFileEnd()
    {
        var (_, diags) = Parse("<StackPanel><TextBlock>{x + </TextBlock></StackPanel>");

        Assert.Contains(diags, d => d.Code == UiDiagnostics.Syntax
            && d.Message.Contains("unterminated expression"));
    }

    [Fact]
    public void CodeBlock_ContainingCommentWithUnbalancedBrace_IsCapturedWhole()
    {
        const string ui = """
            <StackPanel><TextBlock>x</TextBlock></StackPanel>

            @code {
                // 这里的 } 只是注释
                public string a = "}";
                public int b = 1;
            }
            """;

        var (doc, diags) = Parse(ui);

        Assert.Empty(diags);
        var block = Assert.Single(doc.Nodes.OfType<UiCodeBlock>());
        Assert.Contains("public int b = 1;", block.Code);
        Assert.Contains("// 这里的 } 只是注释", block.Code);
    }

    [Fact]
    public void CodeBlock_ContainingRawString_IsCapturedWhole()
    {
        var ui = "<StackPanel><TextBlock>x</TextBlock></StackPanel>\n@code {\n    public string s = \"\"\"a}b\"\"\";\n    public int b = 2;\n}";

        var (doc, diags) = Parse(ui);

        Assert.Empty(diags);
        var block = Assert.Single(doc.Nodes.OfType<UiCodeBlock>());
        Assert.Contains("public int b = 2;", block.Code);
    }

    [Fact]
    public void IfCondition_WithStringContainingParenthesis_IsCapturedWhole()
    {
        const string ui = """
            <StackPanel>
                @if (text.Contains(")")) {
                    <TextBlock>y</TextBlock>
                }
            </StackPanel>
            """;

        var (doc, diags) = Parse(ui);

        Assert.Empty(diags);
        var root = Assert.IsType<UiElement>(doc.Nodes.Single(n => n is UiElement));
        var ifNode = Assert.IsType<UiIf>(root.Children.Single());
        Assert.Equal("text.Contains(\")\")", ifNode.Condition);
    }

    [Fact]
    public void ForeachHeader_WithCommentContainingParenthesis_IsCapturedWhole()
    {
        const string ui = """
            <StackPanel>
                @foreach (var item in Items /* ) */) {
                    <TextBlock>{item}</TextBlock>
                }
            </StackPanel>
            """;

        var (doc, diags) = Parse(ui);

        Assert.Empty(diags);
        var root = Assert.IsType<UiElement>(doc.Nodes.Single(n => n is UiElement));
        Assert.IsType<UiForeach>(root.Children.Single());
    }
}
