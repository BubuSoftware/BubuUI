using EclipseUI.Compiler;
using EclipseUI.LanguageServer.Analysis;
using Xunit;

namespace EclipseUI.Ide.Tests;

/// <summary>大纲/面包屑：结构来自 AST，不是正则扫出来的。</summary>
public class SymbolTests
{
    private const string Template = """
        @using EclipseUI.Elements

        <StackPanel Gap="8">
            <TextBlock>You pressed {count} times</TextBlock>
            @if (count > 0) {
                <Button>+1</Button>
            }
            @foreach (var row in rows) {
                <TextBlock>{row.Name}</TextBlock>
            }
        </StackPanel>

        @code {
            public int count;
        }
        """;

    [Fact]
    public void RootElementBecomesTheFirstSymbol()
    {
        var (service, uri, _) = TestHost.Open(Template);
        var symbols = service.GetSymbols(uri);

        var root = Assert.Single(symbols, s => s.Name == "StackPanel");
        Assert.Equal("StackPanel", root.Detail);
        Assert.Equal(UiSymbolKind.Control, root.Kind);
    }

    [Fact]
    public void BlocksNestUnderTheirParent()
    {
        var (service, uri, _) = TestHost.Open(Template);
        var root = service.GetSymbols(uri).Single(s => s.Name == "StackPanel");

        var condition = root.Children.Single(c => c.Name.StartsWith("@if", StringComparison.Ordinal));
        Assert.Equal(UiSymbolKind.Block, condition.Kind);
        Assert.Contains(condition.Children, c => c.Name == "Button");

        var loop = root.Children.Single(c => c.Name.StartsWith("@foreach", StringComparison.Ordinal));
        Assert.Equal("repeated", loop.Detail);
    }

    [Fact]
    public void CodeBlockAppearsInTheOutline()
    {
        var (service, uri, _) = TestHost.Open(Template);
        Assert.Contains(service.GetSymbols(uri), s => s.Name == "@code" && s.Kind == UiSymbolKind.CodeBlock);
    }

    [Fact]
    public void ContainerRangeCoversTheClosingTag()
    {
        var (service, uri, _) = TestHost.Open(Template);
        var root = service.GetSymbols(uri).Single(s => s.Name == "StackPanel");

        Assert.True(root.Span.Length > root.SelectionSpan.Length,
            "container range must extend past the tag name to cover its content");
    }

    [Fact]
    public void UnknownTagIsStillListed()
    {
        var (service, uri, _) = TestHost.Open("@using EclipseUI.Elements\n<Nope>\n</Nope>");
        var root = Assert.Single(service.GetSymbols(uri));
        Assert.Equal("Nope", root.Name);
        Assert.Equal("unknown tag", root.Detail);
    }
}

/// <summary>诊断走的是编译器前端，断言码而不是文案（文案会随消息打磨变化）。</summary>
public class DiagnosticTests
{
    private const string Using = "@using EclipseUI.Elements\n\n";

    private static string[] Codes(LanguageService service, string uri) =>
        service.GetDiagnostics(uri).Select(d => d.Code).ToArray();

    [Fact]
    public void ValidTemplate_HasNoDiagnostics()
    {
        var text = Using + """
            <StackPanel Gap="8">
                <TextBlock>You pressed {count} times</TextBlock>
                <CheckBox @bind-Checked={showButton} />
                <Button OnClick={() => count++}>+1</Button>
            </StackPanel>

            @code {
                public int count;
                public bool showButton;
            }
            """;

        var (service, uri, _) = TestHost.Open(text);
        Assert.Empty(service.GetDiagnostics(uri));
    }

    [Fact]
    public void UnknownTag_ReportsEui001()
    {
        var (service, uri, _) = TestHost.Open(Using + "<Buton />");
        Assert.Contains(UiDiagnostics.UnknownTag, Codes(service, uri));
    }

    [Fact]
    public void UnknownProperty_ReportsEui002()
    {
        var (service, uri, _) = TestHost.Open(Using + "<Button Nope=\"1\" />");
        Assert.Contains(UiDiagnostics.UnknownProperty, Codes(service, uri));
    }

    [Fact]
    public void MissingUsing_ReportsEui001()
    {
        var (service, uri, _) = TestHost.Open("<Button />");
        Assert.Contains(UiDiagnostics.UnknownTag, Codes(service, uri));
    }

    [Fact]
    public void BadLiteral_ReportsEui003()
    {
        var (service, uri, _) = TestHost.Open(Using + "<StackPanel Gap=\"wide\" />");
        Assert.Contains(UiDiagnostics.LiteralFormat, Codes(service, uri));
    }

    [Fact]
    public void DiagnosticsCarrySpanInsideTheDocument()
    {
        var text = Using + "<Buton />";
        var (service, uri, _) = TestHost.Open(text);
        var diagnostic = Assert.Single(service.GetDiagnostics(uri),
            d => d.Code == UiDiagnostics.UnknownTag);

        Assert.Equal(text.IndexOf("Buton", StringComparison.Ordinal), diagnostic.Span.Start);
        Assert.Equal("Buton".Length, diagnostic.Span.Length);
    }

    [Fact]
    public void EmptyCatalog_ProducesNoTagDiagnostics()
    {
        // 没找到 Runtime.dll 时不能把"每个标签都未知"报出来——那是配置问题，不是代码问题。
        var analysis = DocumentAnalysis.Create(Using + "<Buton />", "Probe.ui", EmptyCatalog());
        Assert.DoesNotContain(analysis.Diagnostics, d => d.Code == UiDiagnostics.UnknownTag);
    }

    private static UiCatalog EmptyCatalog() =>
        new(Array.Empty<UiElementDef>(), Array.Empty<UiHiddenTag>(), Array.Empty<UiMetadataProblem>());
}
