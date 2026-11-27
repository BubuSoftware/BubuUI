using BubuUI.LanguageServer.Analysis;
using BubuUI.LanguageServer.Workspace;
using Xunit;

namespace BubuUI.Ide.Tests;

public class ThemeResourceLanguageTests
{
    [Fact]
    public async Task TypedResourcesOfferCompatibleFieldsAndNavigateToTheirSource()
    {
        var root = RepositoryRoot();
        var workspace = new ProjectWorkspace(new CatalogOptions { ProbeRoot = root });
        var legacy = new CatalogProvider(new CatalogOptions
        {
            RuntimePath = Path.Combine(AppContext.BaseDirectory, "BubuUI.Runtime.dll"),
            IncludeSiblingAssemblies = false,
        });
        var service = new LanguageService(legacy, new DocumentStore(), workspace);
        var directory = Path.Combine(root, "src", "BubuUI.Themes.Fluent");
        var resourcePath = Path.Combine(directory, "FluentResources.cs");
        var source = File.ReadAllText(resourcePath) + """

            public static class CompletionResources {
                public static readonly ThemeResource<UiColor> Color = new(e => default);
                public static readonly ThemeResource<double> Padding = new(e => 12);
                public static readonly UiColor Raw = default;
                public static ThemeResource<UiColor> Property => Color;
                private static readonly ThemeResource<UiColor> Hidden = new(e => default);
                public static class Nested { public static readonly ThemeResource<UiColor> Color = new(e => default); }
            }
            """;
        var resourceUri = DocumentStore.PathToUri(resourcePath);
        service.Documents.Open(resourceUri, source, 1); service.ApplyBuffer(resourceUri, source);
        var styleUri = DocumentStore.PathToUri(Path.Combine(directory, "text-controls.bss"));
        const string imports = "@using BubuUI.Elements\n@using BubuUI.Themes.Fluent\n";
        var style = imports + "Button { Background: resource(CompletionResources.Color); }";
        service.Documents.Open(styleUri, style, 1); service.ApplyBuffer(styleUri, style);
        var offset = style.IndexOf("CompletionResources", StringComparison.Ordinal) + 3;
        var completion = await service.CompleteAsync(styleUri, offset);
        Assert.Contains(completion.Items, item => item.Label == "CompletionResources.Color");
        Assert.Contains(completion.Items, item => item.Label == "CompletionResources.Nested.Color");
        Assert.DoesNotContain(completion.Items, item => item.Label is "CompletionResources.Padding" or "CompletionResources.Raw" or "CompletionResources.Hidden" or "CompletionResources.Property");
        var definition = Assert.IsType<UiDefinition>(service.Definition(styleUri, offset));
        Assert.Equal(resourcePath, definition.Uri);
        Assert.Equal("Color", definition.Text!.Substring(definition.Span.Start, definition.Span.Length));
        Assert.Contains("ThemeResource", service.Hover(styleUri, offset)!.Markdown);
        Assert.Empty(service.GetDiagnostics(styleUri));

        style = imports + "Button { HorizontalPadding: resource(CompletionResources.Padding); }";
        service.Documents.Change(styleUri, style, 2); service.ApplyBuffer(styleUri, style);
        completion = await service.CompleteAsync(styleUri, style.IndexOf("CompletionResources", StringComparison.Ordinal) + 3);
        Assert.Contains(completion.Items, item => item.Label == "CompletionResources.Padding");
        Assert.DoesNotContain(completion.Items, item => item.Label == "CompletionResources.Color");
        Assert.Empty(service.GetDiagnostics(styleUri));

        style = imports + "Button { Background: resource(CompletionResources.Padding); }";
        service.Documents.Change(styleUri, style, 3); service.ApplyBuffer(styleUri, style);
        Assert.Contains(service.GetDiagnostics(styleUri), diagnostic => diagnostic.Code == "EUI050" && diagnostic.Message.Contains("requires"));

        style = "@using BubuUI.Elements\nButton { Background: resource(BubuUI.Themes.Fluent.CompletionResources.Color); }";
        service.Documents.Change(styleUri, style, 4); service.ApplyBuffer(styleUri, style);
        completion = await service.CompleteAsync(styleUri, style.IndexOf(".Color", StringComparison.Ordinal) + 3);
        Assert.Contains(completion.Items, item => item.Label == "BubuUI.Themes.Fluent.CompletionResources.Color");
        Assert.Empty(service.GetDiagnostics(styleUri));

        style = imports + ".primary:hover { Background: resource(CompletionResources.Color); }";
        service.Documents.Change(styleUri, style, 5); service.ApplyBuffer(styleUri, style);
        offset = style.IndexOf("CompletionResources", StringComparison.Ordinal) + 3;
        completion = await service.CompleteAsync(styleUri, offset);
        Assert.Contains(completion.Items, item => item.Label == "CompletionResources.Color");
        Assert.Equal(resourcePath, service.Definition(styleUri, offset)!.Uri);
        Assert.Empty(service.GetDiagnostics(styleUri));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BubuUI.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Cannot locate the repository root.");
    }
}
