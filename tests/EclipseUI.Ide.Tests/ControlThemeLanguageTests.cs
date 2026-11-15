using EclipseUI.LanguageServer.Analysis;
using Xunit;

namespace EclipseUI.Ide.Tests;

public class ControlThemeLanguageTests
{
    private const string Imports = "@using EclipseUI.Elements;\n@using Demo;\n";
    private const string Skin = """
        @namespace Demo
        @using EclipseUI.Elements
        @template Button
        <Border Class="chrome" Background={Owner.Background}>
            <ContentPresenter Part="PART_Content" Content={Owner.Content} />
        </Border>
        """;
    private const string Themes = Imports + "@theme Rounded for Button { Template: template(ButtonSkin); Background: #ffffff; }\n";

    private static (LanguageService Service, string Uri, int Offset) Open(string marked, bool themes = true)
    {
        var service = TestHost.Service();
        var directory = Path.Combine(Path.GetTempPath(), "eclipseui-style-tests", Guid.NewGuid().ToString("N"));
        void Add(string name, string text) => service.Documents.Open(DocumentStore.PathToUri(Path.Combine(directory, name)), text, 1);
        Add("ButtonSkin.ui", Skin);
        if (themes) Add("themes.eustyle", Themes);
        var offset = marked.IndexOf('|');
        var uri = DocumentStore.PathToUri(Path.Combine(directory, "use.eustyle"));
        service.Documents.Open(uri, marked.Replace("|", ""), 1);
        return (service, uri, offset < 0 ? marked.Length : offset);
    }

    [Theory]
    [InlineData("Button { Back|ground: #ffffff; }", "Background")]
    [InlineData("Button { | }", "Background")]
    [InlineData("@theme NewTheme for But|ton { }", "Button")]
    [InlineData("@theme NewTheme for Button { | }", "Background")]
    [InlineData("Button { Theme: resource(Rou|nded); }", "Rounded")]
    [InlineData("@theme NewTheme for Button { Template: template(But|tonSkin); }", "ButtonSkin")]
    [InlineData("@theme NewTheme for Button { Template: template(Demo.But|tonSkin); }", "Demo.ButtonSkin")]
    [InlineData("Button:hov|er { }", "hover")]
    [InlineData("Button { &:focus-v|isible { } }", "focus-visible")]
    [InlineData("Button /template/ Bor|der { }", "Border")]
    [InlineData("Button /template/ Border.chr|ome { }", "chrome")]
    [InlineData("Button /template/ Border { BorderT|hickness: 1; }", "BorderThickness")]
    [InlineData("Button { LabelAlignment: Le|ft; }", "Left")]
    [InlineData("Button { IsVisible: tr|ue; }", "true")]
    public async Task CompletionUsesTheTargetAndReplacesTheWholeToken(string marked, string expected)
    {
        var (service, uri, offset) = Open(Imports + marked);
        var result = await service.CompleteAsync(uri, offset);
        var item = Assert.Single(result.Items, i => i.Label == expected);
        if (result.Replacement.Length > 0)
            Assert.Equal(expected, service.Documents.All.Single(d => d.Uri == uri).Text.Substring(result.Replacement.Start, result.Replacement.Length));
        Assert.False(item.IsSnippet);
    }

    [Theory]
    [InlineData("// resource(Rou|nded)")]
    [InlineData("/* @theme Fake for But|ton { } */")]
    [InlineData("Button { Label: \"resource(Rou|nded)\"; }")]
    [InlineData("Border { Theme: resource(Rou|nded); }")]
    [InlineData("@theme Bad for TextBox { Template: template(But|tonSkin); }")]
    public void CompletionDoesNotInventIncompatibleOrCommentReferences(string marked)
    {
        var (service, uri, offset) = Open(Imports + marked);
        Assert.Empty(service.Complete(uri, offset).Items);
    }

    [Fact]
    public void ResourceDefinitionAndRenameUseUnsavedModuleDocuments()
    {
        var (service, uri, offset) = Open(Imports + "Button { Theme: resource(Rou|nded); } // resource(Rounded)");
        var definition = Assert.IsType<UiDefinition>(service.Definition(uri, offset));
        Assert.EndsWith("themes.eustyle", definition.Uri);
        Assert.Equal("Rounded", definition.Text!.Substring(definition.Span.Start, definition.Span.Length));
        Assert.Contains("Button", service.Hover(uri, offset)!.Markdown);
        Assert.Equal("Rounded", service.PrepareRename(uri, offset)!.Placeholder);
        var rename = Assert.IsType<UiRenameResult>(service.Rename(uri, offset, "Modern"));
        Assert.Equal(2, rename.Files.Count);
        Assert.All(rename.Files, f => Assert.Single(f.Edits));
        Assert.Null(service.Rename(uri, offset, "class"));
        Assert.Null(service.Rename(uri, offset, "Apply"));
        var themes = service.Documents.All.Single(d => d.FileName == "themes.eustyle");
        service.Documents.Change(themes.Uri, Themes.Replace("Rounded", "Changed"), 2);
        Assert.Null(service.Definition(uri, offset));
    }

    [Fact]
    public void TemplateReferenceAndPartClassNavigateToActualUiSource()
    {
        var (service, uri, offset) = Open(Imports + "@theme NewTheme for Button { Template: template(But|tonSkin); }");
        var template = Assert.IsType<UiDefinition>(service.Definition(uri, offset));
        Assert.EndsWith("ButtonSkin.ui", template.Uri);
        Assert.Equal("@template", template.Text!.Substring(template.Span.Start, template.Span.Length));
        var text = Imports + "Button /template/ Border.chrome { BorderThickness: 2; }";
        service.Documents.Change(uri, text, 2);
        var part = Assert.IsType<UiDefinition>(service.Definition(uri, text.IndexOf("chrome") + 2));
        Assert.EndsWith("ButtonSkin.ui", part.Uri);
        Assert.Equal("chrome", part.Text!.Substring(part.Span.Start, part.Span.Length));
    }

    [Fact]
    public void PropertyHoverAndDefinitionUsePartType()
    {
        var (service, uri, offset) = Open(Imports + "Button /template/ Border { BorderTh|ickness: 2; }");
        Assert.Contains("Border", service.Hover(uri, offset)!.Markdown);
        var definition = Assert.IsType<UiDefinition>(service.Definition(uri, offset));
        Assert.Contains("BorderThickness", definition.Text!);
    }

    [Fact]
    public async Task OutlineTokensAndFormattingPreserveCommentsAndStrings()
    {
        var text = Themes + "Button {\r\nBackground: #ffffff;\r\n// braces { }\r\nLabel: \"{hello}\";\r\n&:hover {\r\nBackground: #eeeeee;\r\n}\r\n}";
        var (service, uri, _) = Open(text, false);
        Assert.Equal(2, service.GetSymbols(uri).Count);
        Assert.NotEmpty(await service.SemanticTokensAsync(uri));
        var formatted = text;
        foreach (var edit in service.Format(uri).OrderByDescending(e => e.Span.Start))
            formatted = formatted.Remove(edit.Span.Start, edit.Span.Length).Insert(edit.Span.Start, edit.NewText);
        Assert.Contains("    Label: \"{hello}\";\r\n", formatted);
        Assert.Contains("        Background: #eeeeee;\r\n", formatted);
        service.Documents.Change(uri, formatted, 2);
        Assert.Empty(service.Format(uri));
    }

    [Fact]
    public void StandaloneDiagnosticsRunTheSameCompilerAndUpdateAfterEdits()
    {
        var (service, uri, _) = Open(Imports + "Button { Theme: resource(Missing); }");
        Assert.Contains(service.GetDiagnostics(uri), d => d.Code == "EUI050" && d.Message.Contains("Missing"));
        service.Documents.Change(uri, Imports + "Button { Theme: resource(Rounded); }", 2);
        Assert.Empty(service.GetDiagnostics(uri));
    }

    [Fact]
    public void DuplicateThemeNamesDisableAmbiguousNavigationAndRename()
    {
        var (service, uri, offset) = Open(Themes + "Button { Theme: resource(Rou|nded); }");
        Assert.Null(service.Definition(uri, offset));
        Assert.Null(service.PrepareRename(uri, offset));
    }

    [Fact]
    public void TemplateTargetHasCompletionHoverAndDefinition()
    {
        var (service, uri, offset) = TestHost.Open("@namespace Demo\n@using EclipseUI.Elements\n@template But|ton\n<Border />");
        Assert.Contains(service.Complete(uri, offset).Items, i => i.Label == "Button");
        Assert.DoesNotContain(service.Complete(uri, offset).Items, i => i.Label == "Border");
        Assert.Contains("Button", service.Hover(uri, offset)!.Markdown);
        Assert.NotNull(service.Definition(uri, offset));
    }

    [Theory]
    [InlineData("Button { Background: theme(#ffffff, |); }", "theme(dark, light)", 1)]
    [InlineData("Button { Theme: resource(|); }", "resource(name)", 0)]
    [InlineData("@theme New for Button { Template: template(|); }", "template(type)", 0)]
    public void FunctionHelpTracksTheCurrentArgument(string marked, string label, int parameter)
    {
        var (service, uri, offset) = Open(Imports + marked);
        var help = Assert.IsType<UiSignatureHelp>(service.SignatureHelp(uri, offset));
        Assert.Equal(label, Assert.Single(help.Signatures).Label);
        Assert.Equal(parameter, help.ActiveParameter);
    }

    [Fact]
    public void StyleQuickFixesProduceValidImportsAndPropertyNames()
    {
        var (service, uri, _) = Open("@using Demo;\nButton { Backgroun: #ffffff; }");
        var import = Assert.Single(service.CodeActions(uri), a => a.Title == "Add @using EclipseUI.Elements");
        var doc = service.Documents.All.Single(d => d.Uri == uri);
        var edit = Assert.Single(import.Edits);
        var text = doc.Text.Insert(edit.Span.Start, edit.NewText);
        service.Documents.Change(uri, text, 2);
        var fix = Assert.Single(service.CodeActions(uri), a => a.Title == "Replace with 'Background'");
        edit = Assert.Single(fix.Edits);
        service.Documents.Change(uri, text.Remove(edit.Span.Start, edit.Span.Length).Insert(edit.Span.Start, edit.NewText), 3);
        Assert.Empty(service.GetDiagnostics(uri));
    }

    [Fact]
    public void ApplicationClassNavigatesFromUiToTypedStyleRule()
    {
        var (service, uri, _) = Open(Imports + "Button.primary { Background: #ffffff; }");
        var uiUri = service.Documents.All.Single(d => d.FileName == "ButtonSkin.ui").Uri;
        var ui = "@namespace Demo\n@using EclipseUI.Elements\n<Button Class=\"primary\" />";
        service.Documents.Change(uiUri, ui, 2);
        var definition = service.Definition(uiUri, ui.IndexOf("primary", StringComparison.Ordinal) + 2);
        Assert.NotNull(definition);
        Assert.Equal(DocumentStore.UriToPath(uri), definition.Uri);
        Assert.Equal("primary", definition.Text!.Substring(definition.Span.Start, definition.Span.Length));
    }

    [Theory]
    [InlineData("Button /template/ Border.chr|", "chrome")]
    [InlineData("Button /temp|", "/template/")]
    [InlineData("Button { Back|", "Background")]
    [InlineData("Button { Theme: resource(Rou|", "Rounded")]
    [InlineData("@theme NewTheme for Button { Template: template(But|", "ButtonSkin")]
    [InlineData("@theme NewTheme for Button { &:hover /template/ Bor|", "Border")]
    [InlineData("@us|ing EclipseUI.Elements;", "@using")]
    [InlineData("@using EclipseUI.El|ements;", "EclipseUI.Elements")]
    public void IncompleteInputStillOffersUsefulCandidates(string marked, string expected)
    {
        var (service, uri, offset) = Open(Imports + marked);
        Assert.Contains(service.Complete(uri, offset).Items, item => item.Label == expected);
    }

    [Fact]
    public void TemplateTargetAliasesRemainCompatibleWithThemeCompletion()
    {
        var (service, uri, offset) = Open(Imports + "@theme NewTheme for Button { Template: template(But|tonSkin); }");
        var template = service.Documents.All.Single(d => d.FileName == "ButtonSkin.ui");
        service.Documents.Change(template.Uri, Skin.Replace("@template Button", "@using Btn = EclipseUI.Elements.Button\n@template Btn"), 2);
        Assert.Contains(service.Complete(uri, offset).Items, item => item.Label == "ButtonSkin");
        Assert.Empty(service.GetDiagnostics(uri));
        var uiText = service.Documents.All.Single(d => d.Uri == template.Uri).Text;
        Assert.Contains(service.Complete(template.Uri, uiText.IndexOf("@template Btn", StringComparison.Ordinal) + 12).Items, item => item.Label == "Btn");
    }

    [Theory]
    [InlineData("@using Demo; Button { Label: template.Na|")]
    [InlineData("@using Demo; Button { Theme: resource.| }")]
    public void MalformedFunctionReferencesDoNotCrashCompletion(string marked)
    {
        var (service, uri, offset) = Open(marked);
        Assert.NotNull(service.Complete(uri, offset));
    }
}
