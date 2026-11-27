using BubuUI.LanguageServer.Analysis;
using BubuUI.LanguageServer.Workspace;
using Xunit;

namespace BubuUI.Ide.Tests;

public sealed class LegacyStyleLanguageTests
{
    [Fact]
    public async Task StyleClassesNavigateCompleteAndRenameWithComponentShadowing()
    {
        var (service, style, offset) = Open(".pri|mary { Background: #fff; }");
        var global = DocumentStore.PathToUri(Path.Combine(Path.GetTempPath(), "legacy-style-tests", "Global.bui"));
        var local = DocumentStore.PathToUri(Path.Combine(Path.GetTempPath(), "legacy-style-tests", "Local.bui"));
        service.Documents.Open(global, "@namespace Demo\n@using BubuUI.Elements\n<Button Class=\"primary\" />", 1);
        service.Documents.Open(local, "@namespace Demo\n@using BubuUI.Elements\n@style { .primary { Background: #111; } }\n<Button Class=\"primary\" />", 1);
        var rename = Assert.IsType<UiRenameResult>(service.Rename(style, offset, "accent"));
        Assert.Equal(2, rename.Files.Count);
        Assert.DoesNotContain(rename.Files, f => f.Path!.EndsWith("Local.bui"));
        var text = service.Documents.All.Single(d => d.Uri == local).Text;
        var at = text.LastIndexOf("primary") + 3;
        Assert.Contains((await service.CompleteAsync(local, at)).Items, i => i.Label == "primary");
        var definition = Assert.IsType<UiDefinition>(service.Definition(local, at));
        Assert.Null(definition.Uri);
        Assert.True(definition.Span.Start < at);
        rename = Assert.IsType<UiRenameResult>(await service.RenameAsync(local, at, "local-accent"));
        Assert.Equal(2, Assert.Single(rename.Files).Edits.Count);
    }

    [Fact]
    public void ImportedThemeModulesUseTypedPropertiesForClassSelectors()
    {
        var (service, uri, offset) = Open("@using BubuUI.Elements\n@theme Rounded for Button {}\n.old { CornerRadius: th|; }");
        Assert.Equal("theme", Assert.Single(service.Complete(uri, offset).Items).Label);
    }

    private static (LanguageService Service, string Uri, int Offset) Open(string marked, bool ui = false)
    {
        var service = TestHost.Service();
        var uri = DocumentStore.PathToUri(Path.Combine(Path.GetTempPath(), "legacy-style-tests", ui ? "Widget.bui" : "styles.bss"));
        var offset = marked.IndexOf('|');
        service.Documents.Open(uri, marked.Replace("|", ""), 1);
        return (service, uri, offset);
    }

    [Theory]
    [InlineData(".x { Back| }", "Background")]
    [InlineData(".x { Background: th|; }", "theme")]
    [InlineData("@states { checked; } .x { &:che| { Background: #fff; } }", "checked")]
    [InlineData(".x { &:focus-v| { Background: #fff; } }", "focus-visible")]
    [InlineData(".panel { > But| {} }", "Button")]
    [InlineData("$accent: #123456; .x { Background: $acc|; }", "$accent")]
    public async Task LegacyCompletionAlsoWorksInsideUiBlock(string source, string expected)
    {
        foreach (var ui in new[] { false, true })
        {
            var marked = ui ? "@namespace Demo\r\n@style {\r\n" + source + "\r\n}\r\n<Border />" : source;
            var (service, uri, offset) = Open(marked, ui);
            var completion = await service.CompleteAsync(uri, offset);
            Assert.Contains(completion.Items, i => i.Label == expected);
            Assert.Equal(service.Complete(uri, offset).Replacement, completion.Replacement);
            var old = marked.Replace("|", "").Substring(completion.Replacement.Start, completion.Replacement.Length);
            Assert.True(expected.StartsWith(old), old);
        }
    }

    [Fact]
    public void VariablesRespectDeclarationOrderAndBlockScope()
    {
        var (service, uri, offset) = Open("$tone: #111; .a { Background: $to|ne; } $tone: #222; .b { Background: $tone; }");
        var definition = Assert.IsType<UiDefinition>(service.Definition(uri, offset));
        Assert.Equal(0, definition.Span.Start);
        Assert.Contains("#111", service.Hover(uri, offset)!.Markdown);
        var rename = Assert.IsType<UiRenameResult>(service.Rename(uri, offset, "accent"));
        Assert.Equal(2, Assert.Single(rename.Files).Edits.Count);
        Assert.All(rename.Files[0].Edits, e => Assert.Equal("tone", rename.Files[0].Text.Substring(e.Span.Start, e.Span.Length)));

        (service, uri, offset) = Open("@namespace Demo\n@style { $tone: #111; }\n@style { .x { Background: $to|; } }", true);
        Assert.DoesNotContain(service.Complete(uri, offset).Items, i => i.Label == "$tone");
        (service, uri, offset) = Open(".x { Background: $la|; } $later: #111;");
        Assert.Empty(service.Complete(uri, offset).Items);
    }

    [Fact]
    public async Task RegisteredStatesNavigateAndRenameAcrossOpenStyleSources()
    {
        var (service, uri, offset) = Open(".a:vali|d { Background: #111; } // valid\n.a { Label: \"valid\"; }");
        var other = DocumentStore.PathToUri(Path.Combine(Path.GetTempPath(), "legacy-style-tests", "States.bui"));
        service.Documents.Open(other, "@namespace Demo\n@style { @states { valid; } .b:valid { Background: #fff; } }", 1);
        var definition = Assert.IsType<UiDefinition>(service.Definition(uri, offset));
        Assert.EndsWith("States.bui", definition.Uri);
        Assert.Equal("valid", definition.Text!.Substring(definition.Span.Start, definition.Span.Length));
        Assert.Contains("valid", service.Hover(uri, offset)!.Markdown);
        var rename = Assert.IsType<UiRenameResult>(await service.RenameAsync(uri, offset, "accepted"));
        Assert.Equal(2, rename.Files.Count);
        Assert.Equal(3, rename.Files.Sum(f => f.Edits.Count));
        Assert.Null(service.Rename(uri, offset, "hover"));
    }

    [Theory]
    [InlineData("@namespace Demo\n@style { .x { Backgrou|nd: #fff; } }\n<Border />")]
    [InlineData("@namespace Demo\n@using BubuUI.Elements\n<Border Style=\"Backgrou|nd: #fff;\" />")]
    public void PropertyMetadataAndDefinitionsUseOriginalUiRanges(string marked)
    {
        var (service, uri, offset) = Open(marked, true);
        var hover = Assert.IsType<UiHoverResult>(service.Hover(uri, offset));
        Assert.Contains("Color", hover.Markdown);
        Assert.Equal("Background", marked.Replace("|", "").Substring(hover.Span.Start, hover.Span.Length));
        var definition = Assert.IsType<UiDefinition>(service.Definition(uri, offset));
        Assert.Contains("Background", definition.Text!);
    }

    [Theory]
    [InlineData("<Border Style=\"Back|\" />", "Background")]
    [InlineData("<Border Style=\"Background: th|;\" />", "theme")]
    [InlineData("@style { .a { Back|", "Background")]
    public void IncompleteIslandsComplete(string marked, string expected)
    {
        var (service, uri, offset) = Open("@namespace Demo\n@using BubuUI.Elements\n" + marked, true);
        Assert.Contains(service.Complete(uri, offset).Items, i => i.Label == expected);
    }

    [Fact]
    public async Task EmbeddedFunctionsTokensFixesAndFormattingKeepHostCoordinates()
    {
        var marked = "@namespace Demo\r\n@style {\r\n$x: #111;\r\n.a {\r\nBackgroun: theme(#111, |#fff);\r\n}\r\n    }\r\n<Border />";
        var (service, uri, offset) = Open(marked, true);
        Assert.Equal(1, service.SignatureHelp(uri, offset)!.ActiveParameter);
        Assert.NotEmpty(await service.SemanticTokensAsync(uri));
        Assert.Contains(service.GetSymbols(uri), s => s.Name.StartsWith("$x"));
        var fix = Assert.Single(service.CodeActions(uri), a => a.Title == "Replace with 'Background'");
        var edit = Assert.Single(fix.Edits);
        Assert.Equal("Backgroun", marked.Replace("|", "").Substring(edit.Span.Start, edit.Span.Length));
        var text = marked.Replace("|", "");
        foreach (var e in service.Format(uri).OrderByDescending(e => e.Span.Start))
            text = text.Remove(e.Span.Start, e.Span.Length).Insert(e.Span.Start, e.NewText);
        Assert.Contains("\r\n        Backgroun", text);
        Assert.EndsWith("}\r\n<Border />", text);
        service.Documents.Change(uri, text, 2);
        Assert.Empty(service.Format(uri));
    }

    [Theory]
    [InlineData(".x { // Back|\n }")]
    [InlineData(".x { Label: \"Back|\"; }")]
    public void CommentsAndQuotedTextDoNotOfferCompletions(string marked)
    {
        var (service, uri, offset) = Open(marked);
        Assert.Empty(service.Complete(uri, offset).Items);
    }

    [Fact]
    public void InlineStylesDoNotOfferUnsupportedVariablesOrThemeForNumbers()
    {
        var (service, uri, offset) = Open("@namespace Demo\n@style { $radius: 4; }\n<Border Style=\"CornerRadius: $ra|;\" />", true);
        Assert.Empty(service.Complete(uri, offset).Items);
        (service, uri, offset) = Open(".x { CornerRadius: th|; }");
        Assert.Empty(service.Complete(uri, offset).Items);
    }
}
