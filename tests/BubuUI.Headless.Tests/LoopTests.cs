using System;
using System.Linq;
using BubuUI.Compiler;
using BubuUI.Elements;
using Xunit;

namespace BubuUI.Headless.Tests;

public class LoopTests
{
    private static (UiDocument, System.Collections.Generic.List<UiDiagnostic>) Parse(string text)
    {
        var diagnostics = new System.Collections.Generic.List<UiDiagnostic>();
        return (new Parser(text, diagnostics).ParseDocument("Probe.bui"), diagnostics);
    }
    [Fact]
    public void GeneratedLoops_RenderNestedContentAndRefresh()
    {
        using var ui = new LoopProbe();
        ui.EnsureInitialized();
        var loops = ui.Children.Cast<LoopBlock>().ToArray();
        Assert.Equal(3, loops.Length);
        Assert.Equal(new[] { "0:3", "1:2", "2:1" }, loops[0].Children.OfType<Button>().Select(b => b.Label));
        Assert.Equal(new[] { 0, 1, 2 }, loops[0].Children.OfType<LoopBlock>().Select(b => b.Children.Count));
        Assert.Equal(new[] { "2", "1" }, loops[1].Children.OfType<Button>().Select(b => b.Label));
        Assert.Single(loops[2].Children);
        var buttons = loops[0].Children.OfType<Button>().ToArray();
        buttons[0].Click();
        Assert.Equal(30, ui.Selected);
        buttons[2].Click();
        Assert.Equal(12, ui.Selected);
        loops[1].Children.OfType<Button>().Last().Click();
        Assert.Equal(1, ui.Selected);
        var old = loops[0].Children.ToArray();
        ui.Count = 1;
        ui.Remaining = 0;
        ui.Invalidate();
        ui.ProcessInvalidations();
        Assert.All(old, e => Assert.True(e.IsDisposed));
        Assert.Single(loops[0].Children.OfType<Button>());
        Assert.Empty(loops[1].Children);
        Assert.Single(loops[2].Children);
    }

    [Fact]
    public void FailedIteration_CleansAllPreviousRootsAndCanRetry()
    {
        using var block = new LoopBlock();
        Button? root = null;
        bool fail = true;
        block.SetContent(scope =>
        {
            root = new Button();
            scope.AddRoot(root);
            if (fail) throw new InvalidOperationException("failed");
        });
        Assert.Throws<InvalidOperationException>(block.Refresh);
        Assert.True(root!.IsDisposed);
        Assert.Empty(block.Children);
        fail = false;
        block.Refresh();
        Assert.Same(root, Assert.Single(block.Children));
        Assert.True(block.IsLayoutTransparent);
        Assert.True(block.IsVisualTransparent);
    }

    [Theory]
    [InlineData("@do { <TextBlock /> } while (false);", "do")]
    [InlineData("@for (int i = F(\";()\"); i < 3; i++) { <TextBlock /> }", "for")]
    [InlineData("@while (F(/* ) */true)) { <TextBlock /> }", "while")]
    public void Parser_CapturesLoopHeaders(string source, string kind)
    {
        var (doc, diagnostics) = Parse("<StackPanel>" + source + "</StackPanel>");
        Assert.Empty(diagnostics);
        var loop = Assert.IsType<UiLoop>(Assert.Single(Assert.IsType<UiElement>(Assert.Single(doc.Nodes)).Children));
        Assert.Equal(kind, loop.Kind);
    }

    [Theory]
    [InlineData("@for { <TextBlock /> }")]
    [InlineData("@while () { <TextBlock /> }")]
    [InlineData("@do { <TextBlock /> }")]
    [InlineData("@do { <TextBlock /> } while (true)")]
    [InlineData("@do { <TextBlock /> } while ();")]
    public void Parser_ReportsMalformedLoops(string source)
    {
        var (_, diagnostics) = Parse("<StackPanel>" + source + "</StackPanel>");
        Assert.Contains(diagnostics, d => d.Code == UiDiagnostics.Syntax);
    }
}
