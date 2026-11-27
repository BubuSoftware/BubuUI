using BubuUI.Compiler;
using BubuUI.LanguageServer.Analysis;
using Xunit;

namespace BubuUI.Ide.Tests;

/// <summary>大纲/面包屑：结构来自 AST，不是正则扫出来的。</summary>
public class SymbolTests
{
    private const string Template = """
        @using BubuUI.Elements

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
        var (service, uri, _) = TestHost.Open("@using BubuUI.Elements\n<Nope>\n</Nope>");
        var root = Assert.Single(service.GetSymbols(uri));
        Assert.Equal("Nope", root.Name);
        Assert.Equal("unknown tag", root.Detail);
    }
}

/// <summary>诊断走的是编译器前端，断言码而不是文案（文案会随消息打磨变化）。</summary>
public class DiagnosticTests
{
    private const string Using = "@using BubuUI.Elements\n\n";

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

    /// <summary>
    /// spec/01 §4.5：<c>@bind-P</c> 的 P 存在但不可绑时，IDE 必须报 EUI040（并说清为什么），
    /// 而不是「未知属性」EUI002——后者会把人带去改拼写。与生成器共用同一份目录判断，
    /// 所以 Tier 1 与编译期的结论天然一致。
    /// </summary>
    [Fact]
    public void BindOnExistingButUnbindableProperty_ReportsEui040()
    {
        var (service, uri, _) = TestHost.Open(Using + "<Button @bind-IsEnabled={count} />");
        // 只看 Tier 1：Tier 2 跑真生成器时同一处会再报一次（span 相同），这里要的是「IDE 立刻就能说清」。
        var diagnostics = service.GetDiagnostics(uri, includeTier2: false);

        Assert.DoesNotContain(diagnostics, d => d.Code == UiDiagnostics.UnknownProperty);
        var diagnostic = Assert.Single(diagnostics, d => d.Code == UiDiagnostics.BindTargetInvalid);
        Assert.Contains("IsEnabledChanged", diagnostic.Message);
        // 报在属性名上，且覆盖整个 `@bind-IsEnabled`（属性名前缀也是这条诊断的一部分）
        Assert.Equal(Using.Length + "<Button ".Length, diagnostic.Span.Start);
        Assert.Equal("@bind-IsEnabled".Length, diagnostic.Span.Length);
    }

    [Fact]
    public void BindOnMissingProperty_StillReportsEui002()
    {
        var (service, uri, _) = TestHost.Open(Using + "<Button @bind-Lable={count} />");
        var diagnostics = service.GetDiagnostics(uri, includeTier2: false);

        Assert.DoesNotContain(diagnostics, d => d.Code == UiDiagnostics.BindTargetInvalid);
        Assert.Contains(diagnostics, d => d.Code == UiDiagnostics.UnknownProperty);
    }

    [Fact]
    public void EmptyCatalog_ProducesNoTagDiagnostics()
    {
        // 没找到 Runtime.dll 时不能把"每个标签都未知"报出来——那是配置问题，不是代码问题。
        var analysis = DocumentAnalysis.Create(Using + "<Buton />", "Probe.bui", EmptyCatalog());
        Assert.DoesNotContain(analysis.Diagnostics, d => d.Code == UiDiagnostics.UnknownTag);
    }

    private static UiCatalog EmptyCatalog() =>
        new(Array.Empty<UiElementDef>(), Array.Empty<UiHiddenTag>(), Array.Empty<UiMetadataProblem>());
}
