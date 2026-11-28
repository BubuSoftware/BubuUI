using BubuUI.Compiler;
using BubuUI.LanguageServer.Analysis;
using Xunit;

namespace BubuUI.Ide.Tests;

public class IndentationTests
{
    private static string Apply(string text, IReadOnlyList<UiTextEdit> edits)
    {
        foreach (var edit in edits.OrderByDescending(e => e.Span.Start))
            text = text.Remove(edit.Span.Start, edit.Span.Length).Insert(edit.Span.Start, edit.NewText);
        return text;
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(4, false)]
    public void Formatting_HandlesInlineTagsLoopsAndMultilineAttributes(int size, bool spaces)
    {
        const string text = "@namespace Demo\n@using BubuUI.Elements\n<StackPanel>\n<TextBlock>Hello</TextBlock>\n@for (var i=0; i<2; i++) {\n<Button\nLabel={i.ToString()}\n/>\n}\n@do {\n<TextBlock />\n} while (false);\n</StackPanel>\n";
        var (analysis, _) = TestHost.Analyze(text);
        var formatted = Apply(text, EmbeddedCSharpService.Format(analysis, size, spaces));
        string unit = spaces ? new string(' ', size) : "\t";
        Assert.Contains("\n" + unit + "<TextBlock>Hello</TextBlock>\n" + unit + "@for", formatted);
        Assert.Contains("\n" + unit + unit + "<Button\n" + unit + unit + unit + "Label=", formatted);
        Assert.Contains("\n" + unit + unit + "/>\n" + unit + "}\n", formatted);
        Assert.Contains("\n" + unit + "} while (false);\n</StackPanel>\n", formatted);
        var (again, _) = TestHost.Analyze(formatted);
        Assert.Empty(EmbeddedCSharpService.Format(again, size, spaces));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Formatting_PreservesMultilineStringsAndComments(string newline)
    {
        const string source = """"
            @namespace Demo
            @using BubuUI.Elements
            <StackPanel>
            <!-- hello
            preserve this indentation <Panel> { }
            -->
            <TextBlock>{"""
                raw <Button> } {
                """}</TextBlock>
            <TextBlock>after</TextBlock>
            </StackPanel>
            """";
        var text = source.ReplaceLineEndings(newline);
        var (analysis, _) = TestHost.Analyze(text);
        var formatted = Apply(text, EmbeddedCSharpService.Format(analysis));
        Assert.Contains(newline + "preserve this indentation <Panel> { }" + newline + "-->", formatted);
        Assert.Contains(newline + "    raw <Button> } {" + newline + "    \"\"\"}", formatted);
        Assert.Contains(newline + "    <TextBlock>after</TextBlock>" + newline + "</StackPanel>", formatted);
    }

    [Fact]
    public void Newline_IndentsBlankLineAndDedentsClosingTag()
    {
        var lines = TemplateIndentation.Analyze("<StackPanel>\n@for (var i=0; i<2; i++) {\n\n}\n</StackPanel>");
        Assert.Equal(new[] { 0, 1, 2, 1, 0 }, lines.Select(l => l.Depth));
        Assert.All(lines, l => Assert.False(l.Preserve));
    }

    [Fact]
    public void CodeFormatting_HonorsTabsAndCrLfAndIsIdempotent()
    {
        const string text = "@namespace Demo\r\n@using BubuUI.Elements\r\n<StackPanel />\r\n@code {\r\npublic int Value;\r\npublic void M() {\r\nValue++;\r\n}\r\n}\r\n";
        var (analysis, _) = TestHost.Analyze(text);
        var formatted = Apply(text, EmbeddedCSharpService.Format(analysis, 2, false));
        Assert.Contains("\r\n\tpublic int Value;", formatted);
        Assert.DoesNotContain("\n", formatted.Replace("\r\n", ""));
        var (again, _) = TestHost.Analyze(formatted);
        Assert.Empty(EmbeddedCSharpService.Format(again, 2, false));
    }
}
