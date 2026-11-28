using BubuUI.LanguageServer.Analysis;
using Xunit;

namespace BubuUI.Ide.Tests;

public class LoopLanguageTests
{
    [Fact]
    public void DoContinuation_AllowsTemplateComments()
    {
        const string text = "@do { <TextBlock /> } @* next *@ while <!-- condition --> (Flag);";
        var context = CursorScanner.Scan(text, text.IndexOf("Flag", StringComparison.Ordinal) + 2);
        Assert.Equal(CursorKind.Expression, context.Kind);
        Assert.Equal("do while", context.Directive);
    }

    [Fact]
    public void ForHeader_HasMemberCompletion()
    {
        var (analysis, offset) = TestHost.Analyze("@namespace Demo\n@using BubuUI.Elements\n<StackPanel>@for (var i=0; i.To| < 3; i++) { <TextBlock /> }</StackPanel>");
        Assert.Contains(CompletionProvider.Complete(analysis, TestHost.Catalog, offset).Items, i => i.Label == "ToString");
    }

    [Fact]
    public void ForLocal_HasCompletionAndDefinition()
    {
        var (analysis, offset) = TestHost.Analyze("""
            @namespace Demo
            @using BubuUI.Elements
            <StackPanel>
                @for (var i = 0; i < 4; i++) {
                    <TextBlock>{i.|}</TextBlock>
                }
            </StackPanel>
            """);
        var context = CursorScanner.Scan(analysis.Text, offset);
        var env = ExpressionEnvironment.Build(analysis.Ast!, context.OpenForeaches, analysis.Probe!, analysis.Probe!.Compilation);
        Assert.Equal("int", Assert.Single(env).Type.ToDisplayString());
        Assert.Contains(CompletionProvider.Complete(analysis, TestHost.Catalog, offset).Items, i => i.Label == "ToString");
    }

    [Theory]
    [InlineData("var i| = 0")]
    [InlineData("var i = 0")]
    public void Rename_UpdatesDeclarationConditionIncrementAndBody(string declaration)
    {
        var marked = "@namespace Demo\n@using BubuUI.Elements\n<StackPanel>@for (" + declaration +
            "; i < 4; i++) { <TextBlock>{" + (declaration.Contains('|') ? "i" : "i|") + "}</TextBlock> }</StackPanel>";
        var (analysis, offset) = TestHost.Analyze(marked);
        var result = RenameProvider.Rename(analysis, offset, "index", null);
        var edits = Assert.Single(Assert.IsType<UiRenameResult>(result).Files).Edits;
        Assert.Equal(4, edits.Count);
        Assert.All(edits, e => Assert.Equal("i", analysis.Text.Substring(e.Span.Start, e.Span.Length)));
    }

    [Fact]
    public void NestedForAndForeach_InferBothScopes()
    {
        var (analysis, offset) = TestHost.Analyze("""
            @namespace Demo
            @using BubuUI.Elements
            <StackPanel>
                @for (var i = 0; i < 3; i++) {
                    @foreach (var s in i.ToString().Split(',')) {
                        <TextBlock>{s.|}</TextBlock>
                    }
                }
            </StackPanel>
            """);
        Assert.Contains(CompletionProvider.Complete(analysis, TestHost.Catalog, offset).Items, i => i.Label == "Length");
    }

    [Fact]
    public void DoCondition_IsAnExpressionAndLoopLocalsDoNotLeak()
    {
        var context = CursorScanner.Scan("@do { <TextBlock /> } while (Flag);", 32);
        Assert.Equal(CursorKind.Expression, context.Kind);
        Assert.Equal("do while", context.Directive);
        var outside = CursorScanner.Scan("@for (var i=0; i<3; i++) { <TextBlock/> } <TextBlock>{x}", 52);
        Assert.Empty(outside.OpenForeaches);
    }
}
