using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EclipseUI.Compiler;
using EclipseUI.Compiler.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace EclipseUI.Compiler.Tests;

/// <summary>
/// E1（design/17 §4/§5）：Eustyle 样式表解析器、C# 生成器与 .eustyle/@style 管线。
/// 诊断码：EUI050（语法/不支持形态）、EUI051（未知属性）、EUI052（值类型不符）。
/// </summary>
public class EustyleTests
{
    // ---------- 解析器 ----------

    private static (EustyleStylesheet Sheet, List<UiDiagnostic> Diags) Parse(string source, int offset = 0)
    {
        var diags = new List<UiDiagnostic>();
        var sheet = Eustyle.Parse(source, "test.eustyle", diags, offset);
        return (sheet, diags);
    }

    [Fact]
    public void ClassRule_ParsesThemeAndNumberSetters()
    {
        var (sheet, diags) = Parse(".card {\n    background: theme(#22a064, #1c8c58);\n    corner-radius: 12;\n}");

        Assert.Empty(diags);
        var rule = Assert.Single(sheet.Rules);
        Assert.Equal("card", rule.ClassName);
        Assert.Equal(2, rule.Setters.Count);

        var background = rule.Setters[0];
        Assert.Equal(EustyleVariant.None, background.Variant);
        Assert.Equal("Background", background.Property);
        var theme = Assert.IsType<EustyleTheme>(background.Value);
        Assert.Equal((byte)34, theme.Dark.R);
        Assert.Equal((byte)160, theme.Dark.G);
        Assert.Equal((byte)100, theme.Dark.B);
        Assert.Equal((byte)28, theme.Light.R);
        Assert.Equal((byte)140, theme.Light.G);
        Assert.Equal((byte)88, theme.Light.B);

        var radius = rule.Setters[1];
        Assert.Equal("CornerRadius", radius.Property);
        Assert.Equal(12, Assert.IsType<EustyleNumber>(radius.Value).Value);
    }

    [Fact]
    public void HexForms_ExpandShortAndParseAlpha()
    {
        var (sheet, diags) = Parse(".a { background: #abc; }\n.b { background: #aabbcc; }\n.c { background: #aabbcc80; }");

        Assert.Empty(diags);
        var short_ = Assert.IsType<EustyleColor>(sheet.Rules[0].Setters.Single().Value);
        Assert.Equal((byte)170, short_.R);
        Assert.Equal((byte)187, short_.G);
        Assert.Equal((byte)204, short_.B);
        Assert.Equal((byte)255, short_.A);

        var full = Assert.IsType<EustyleColor>(sheet.Rules[1].Setters.Single().Value);
        Assert.Equal((byte)170, full.R);
        Assert.Equal((byte)187, full.G);
        Assert.Equal((byte)204, full.B);

        var alpha = Assert.IsType<EustyleColor>(sheet.Rules[2].Setters.Single().Value);
        Assert.Equal((byte)0x80, alpha.A);
    }

    [Fact]
    public void Variants_NestedBlockAndSelectorForm_BothSupported()
    {
        const string source = """
            .primary {
                background: #22a064;
                &:hover { background: #30ba78; }
                &:pressed { background: #187e4e; }
            }
            .secondary:hover { background: #30ba78; }
            """;

        var (sheet, diags) = Parse(source);
        Assert.Empty(diags);

        var primary = sheet.Rules[0];
        Assert.Equal(3, primary.Setters.Count);
        Assert.Equal(EustyleVariant.None, primary.Setters[0].Variant);
        Assert.Equal(EustyleVariant.Hover, primary.Setters[1].Variant);
        Assert.Equal(EustyleVariant.Pressed, primary.Setters[2].Variant);

        var secondary = sheet.Rules[1];
        var setter = Assert.Single(secondary.Setters);
        Assert.Equal(EustyleVariant.Hover, setter.Variant);
    }

    [Fact]
    public void ActiveAlias_MapsToPressedVariant()
    {
        var (sheet, diags) = Parse(".btn:active { background: #fff; }");

        Assert.Empty(diags);
        Assert.Equal(EustyleVariant.Pressed, sheet.Rules[0].Setters.Single().Variant);
    }

    [Fact]
    public void Variables_SubstituteAtParseTime()
    {
        const string source = """
            $primary: theme(#22a064, #1c8c58);
            $alias: $primary;
            .x { background: $primary; foreground: $alias; }
            """;

        var (sheet, diags) = Parse(source);
        Assert.Empty(diags);

        var setters = sheet.Rules[0].Setters;
        var background = Assert.IsType<EustyleTheme>(setters[0].Value);
        var foreground = Assert.IsType<EustyleTheme>(setters[1].Value);
        Assert.Equal(background.Dark, foreground.Dark);
        Assert.Equal(background.Light, foreground.Light);
    }

    [Fact]
    public void Comments_AreIgnored()
    {
        var (sheet, diags) = Parse("// header note\n.card { /* inline */ background: #fff; } // trailing");

        Assert.Empty(diags);
        var rule = Assert.Single(sheet.Rules);
        Assert.Equal("Background", rule.Setters.Single().Property);
    }

    // ---------- 诊断 ----------

    private static string CodeOf(string source, int offset = 0)
    {
        var (_, diags) = Parse(source, offset);
        return Assert.Single(diags).Code;
    }

    [Fact]
    public void UnknownProperty_ReportsEUI051()
    {
        Assert.Equal(UiDiagnostics.StyleUnknownProperty, CodeOf(".x { colour: #fff; }"));
    }

    [Fact]
    public void NumberForColorProperty_ReportsEUI052()
    {
        Assert.Equal(UiDiagnostics.StyleValueType, CodeOf(".x { background: 12; }"));
    }

    [Fact]
    public void ColorForNumberProperty_ReportsEUI052()
    {
        Assert.Equal(UiDiagnostics.StyleValueType, CodeOf(".x { padding: #fff; }"));
    }

    [Fact]
    public void UnitSuffix_ReportsEUI052_WithLogicalUnitHint()
    {
        var (_, diags) = Parse(".x { padding: 16px; }");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.StyleValueType, diag.Code);
        Assert.Contains("not '16px'", diag.Message);
    }

    [Fact]
    public void UndefinedVariable_ReportsEUI052()
    {
        Assert.Equal(UiDiagnostics.StyleValueType, CodeOf(".x { background: $nope; }"));
    }

    [Fact]
    public void InvalidHexColor_ReportsEUI052()
    {
        Assert.Equal(UiDiagnostics.StyleValueType, CodeOf(".x { background: #12g; }"));
    }

    [Fact]
    public void UnknownPseudoClass_ReportsEUI050()
    {
        Assert.Equal(UiDiagnostics.StyleSyntax, CodeOf(".x:spinning { background: #fff; }"));
    }

    [Fact]
    public void UnterminatedRule_ReportsEUI050()
    {
        Assert.Equal(UiDiagnostics.StyleSyntax, CodeOf(".x { background: #fff;"));
    }

    [Fact]
    public void UnexpectedTopLevelCharacter_ReportsEUI050()
    {
        Assert.Equal(UiDiagnostics.StyleSyntax, CodeOf("garbage"));
    }

    [Fact]
    public void MissingSemicolon_ReportsEUI050()
    {
        Assert.Equal(UiDiagnostics.StyleSyntax, CodeOf(".x { background #fff; }"));
    }

    [Fact]
    public void Diagnostics_MapThroughSpanOffset_ToHostFilePositions()
    {
        var (_, diags) = Parse("$pad: 0;\n.x { colour: #fff; }", offset: 100);

        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.StyleUnknownProperty, diag.Code);
        // 'colour' 在宿主文件中的绝对位置：100 + 14（第二行第 5 个字符起）
        Assert.True(diag.Span.Start >= 100);
        Assert.Equal("colour".Length, diag.Span.Length);
    }

    // ---------- C# 生成器 ----------

    [Fact]
    public void Generator_EmitsDefineChainWithFullyQualifiedTypes()
    {
        var (sheet, diags) = Parse(".primary { background: theme(#22a064, #1c8c58); }\n.flat { background: #aaa; corner-radius: 1.5; }");
        Assert.Empty(diags);

        var code = EustyleGenerator.EmitDefines(sheet, "registry");

        Assert.Contains("registry.Define(\"primary\", s =>", code);
        Assert.Contains("new global::EclipseUI.Rendering.ThemeColor(new global::EclipseUI.Rendering.Color(34, 160, 100), new global::EclipseUI.Rendering.Color(28, 140, 88))", code);
        Assert.Contains("registry.Define(\"flat\", s =>", code);
        Assert.Contains("new global::EclipseUI.Rendering.Color(170, 170, 170)", code);
        Assert.Contains("s.CornerRadius(1.5);", code);
        // 值顺序与声明序一致（theme 的 dark 在 light 之前）
        Assert.True(code.IndexOf("34, 160, 100", StringComparison.Ordinal) <
                    code.IndexOf("28, 140, 88", StringComparison.Ordinal));
    }

    // ---------- 生成器契约（Roslyn 驱动） ----------

    private sealed class TestAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public TestAdditionalText(string path, string content)
        {
            Path = path;
            _text = SourceText.From(content);
        }

        public override string Path { get; }

        public override SourceText? GetText(CancellationToken cancellationToken = default) => _text;
    }

    private static GeneratorRunResult Run(params AdditionalText[] texts)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(EclipseUI.Component).Assembly.Location))
            .ToList();
        var compilation = CSharpCompilation.Create(
            "StyleProbe",
            new[] { CSharpSyntaxTree.ParseText("public partial class Probe { }") },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() }, texts);
        return driver.RunGenerators(compilation).GetRunResult().Results.Single();
    }

    private static string SourceOf(GeneratorRunResult result, Func<string, bool> hintPredicate)
    {
        Assert.Null(result.Exception);
        Assert.True(result.GeneratedSources.Any(s => hintPredicate(s.HintName)),
            "no generated source matched; diagnostics: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Id + ": " + d.GetMessage())) +
            " | hints: " + string.Join(", ", result.GeneratedSources.Select(s => s.HintName)));
        return result.GeneratedSources.Single(s => hintPredicate(s.HintName)).SourceText.ToString();
    }

    /// <summary>生成产物必须过 C# 语法解析（E1 首版曾发射残缺 lambda，Contains 断言没拦住、画廊真实构建才暴露）。</summary>
    private static void AssertGeneratedCodeParses(string code)
    {
        var errors = CSharpSyntaxTree.ParseText(code)
            .GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        Assert.True(errors.Count == 0,
            "generated code has syntax errors: " + string.Join("; ", errors.Take(4)) + "\n---- generated ----\n" + code);
    }

    [Fact]
    public void StyleBlock_GeneratesPrefixedRegistration_InvokedFromBuild()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel />
            @style {
                .demo { corner-radius: 12; }
            }
            """;

        var result = Run(new TestAdditionalText("Probe.ui", ui));
        var code = SourceOf(result, hint => hint.Contains("Probe."));
        AssertGeneratedCodeParses(code);

        Assert.Contains("__RegisterStyles", code);
        Assert.Contains("__RegisterStyles();", code); // Build 首行调用
        Assert.Contains("global::EclipseUI.Rendering.StyleRegistry.Instance", code);
        Assert.Contains("global::EclipseUI.Rendering.StyleRegistry.Instance.Define(\"Probe__demo\", s =>", code);
        Assert.Contains("s.CornerRadius(12);", code);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void StyleBlock_UnknownProperty_ReportsEUI051()
    {
        const string ui = """
            <StackPanel />
            @style {
                .demo { colour: #fff; }
            }
            """;

        var result = Run(new TestAdditionalText("Probe.ui", ui));
        Assert.Null(result.Exception);
        Assert.Contains(result.Diagnostics, d => d.Id == UiDiagnostics.StyleUnknownProperty);
        // 错误短路：组件代码不再产出
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void StyleBlock_NonTopLevel_ReportsEUI100()
    {
        const string ui = """
            <StackPanel>
                @style { .demo { background: #fff; } }
            </StackPanel>
            """;

        var result = Run(new TestAdditionalText("Probe.ui", ui));
        Assert.Null(result.Exception);
        Assert.Contains(result.Diagnostics, d =>
            d.Id == UiDiagnostics.Syntax && d.GetMessage().Contains("'@style' is only allowed at the top level"));
    }

    [Fact]
    public void Stylesheet_EmitsStyleBootstrapApply()
    {
        const string sheet = """
            $primary: theme(#22a064, #1c8c58);
            .primary {
                background: $primary;
                &:hover { background: #30ba78; }
                &:pressed { background: #187e4e; }
                &:disabled { background: #2e3e3a; }
            }
            """;

        var result = Run(new TestAdditionalText("app.eustyle", sheet));
        var code = SourceOf(result, hint => hint.Contains("StyleBootstrap.g.cs"));
        AssertGeneratedCodeParses(code);

        Assert.Contains("public static class StyleBootstrap", code);
        Assert.Contains("public static void Apply(global::EclipseUI.Rendering.StyleRegistry registry)", code);
        Assert.Contains("registry.Define(\"primary\", s =>", code);
        Assert.Contains(".Hover(s =>", code);
        Assert.Contains(".Pressed(s =>", code);
        Assert.Contains(".Disabled(s =>", code);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Stylesheet_Error_ShortCircuitsGeneration()
    {
        var result = Run(new TestAdditionalText("app.eustyle", ".x { colour: #fff; }"));

        Assert.Null(result.Exception);
        Assert.Contains(result.Diagnostics, d => d.Id == UiDiagnostics.StyleUnknownProperty);
        Assert.Empty(result.GeneratedSources);
    }
}
