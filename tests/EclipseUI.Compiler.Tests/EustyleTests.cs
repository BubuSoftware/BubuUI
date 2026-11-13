using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EclipseUI.Compiler;
using EclipseUI.Compiler.Generator;
using EclipseUI.TestKit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace EclipseUI.Compiler.Tests;

/// <summary>
/// E1（design/17 §4/§5）：Eustyle 样式表解析器、C# 生成器与 .eustyle/@style 管线�?
/// 诊断码：EUI050（语�?不支持形态）、EUI051（未知属性）、EUI052（值类型不符）�?
/// </summary>
public class EustyleTests
{
    // ---------- 解析�?----------

    /// <summary>真实符号派生的属性目录（扫描 Runtime + Rendering，绝不手写固定表）�?/summary>
    private static StylePropertyCatalog Catalog => UiTestCatalogs.StyleProperties();

    [Theory]
    [InlineData("IsFocused")]
    [InlineData("IsFocusWithin")]
    [InlineData("IsFocusVisible")]
    public void FocusStatesCannotBeAssignedByTemplates(string state)
    {
        var result = Run(new TestAdditionalText("Probe.ui", "@using EclipseUI.Elements\n<Button " + state + "=\"true\" />"));
        Assert.NotEmpty(result.Diagnostics);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.GetMessage().Contains(state));
    }

    [Fact]
    public void FocusVariantsAndVisualPropertiesCompileThroughRealCatalog()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel><Button Class="action" Style="FocusVisualMode: Outline">Go</Button></StackPanel>
            @style {
                .action {
                    FocusVisualMode: Outline;
                    FocusRingWidth: 3;
                    FocusRingOffset: 4;
                    FocusRingColor: theme(#7ebaff, #3476ff);
                    &:focus { Background: #123456; }
                    &:focus-visible { CornerRadius: 8; }
                    &:focus-within { Padding: 3; }
                }
            }
            """;
        var result = Run(new TestAdditionalText("Probe.ui", ui));
        Assert.Empty(result.Diagnostics);
        var code = SourceOf(result, hint => hint.Contains("Probe."));
        AssertGeneratedCodeParses(code);
        Assert.Contains(".Focus(s =>", code); Assert.Contains(".FocusVisible(s =>", code); Assert.Contains(".FocusWithin(s =>", code);
        Assert.Contains("s.FocusRingOffset(4)", code); Assert.Contains("FocusVisualMode.Outline", code);
    }

    private static (EustyleStylesheet Sheet, List<UiDiagnostic> Diags) Parse(string source, int offset = 0)
    {
        var diags = new List<UiDiagnostic>();
        var sheet = Eustyle.Parse(source, "test.eustyle", diags, Catalog, offset);
        return (sheet, diags);
    }

    [Fact]
    public void ClassRule_ParsesThemeAndNumberSetters()
    {
        var (sheet, diags) = Parse(".card {\n    Background: theme(#22a064, #1c8c58);\n    CornerRadius: 12;\n}");

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
        var (sheet, diags) = Parse(".a { Background: #abc; }\n.b { Background: #aabbcc; }\n.c { Background: #aabbcc80; }");

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
                Background: #22a064;
                &:hover { Background: #30ba78; }
                &:pressed { Background: #187e4e; }
            }
            .secondary:hover { Background: #30ba78; }
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
        var (sheet, diags) = Parse(".btn:active { Background: #fff; }");

        Assert.Empty(diags);
        Assert.Equal(EustyleVariant.Pressed, sheet.Rules[0].Setters.Single().Variant);
    }

    [Fact]
    public void Variables_SubstituteAtParseTime()
    {
        const string source = """
            $primary: theme(#22a064, #1c8c58);
            $alias: $primary;
            .x { Background: $primary; Foreground: $alias; }
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
        var (sheet, diags) = Parse("// header note\n.card { /* inline */ Background: #fff; } // trailing");

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
        Assert.Equal(UiDiagnostics.StyleValueType, CodeOf(".x { Background: 12; }"));
    }

    [Fact]
    public void ColorForNumberProperty_ReportsEUI052()
    {
        Assert.Equal(UiDiagnostics.StyleValueType, CodeOf(".x { Padding: #fff; }"));
    }

    [Fact]
    public void UnitSuffix_ReportsEUI052_WithLogicalUnitHint()
    {
        var (_, diags) = Parse(".x { Padding: 16px; }");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.StyleValueType, diag.Code);
        Assert.Contains("not '16px'", diag.Message);
    }

    [Fact]
    public void UndefinedVariable_ReportsEUI052()
    {
        Assert.Equal(UiDiagnostics.StyleValueType, CodeOf(".x { Background: $nope; }"));
    }

    [Fact]
    public void InvalidHexColor_ReportsEUI052()
    {
        Assert.Equal(UiDiagnostics.StyleValueType, CodeOf(".x { Background: #12g; }"));
    }

    [Fact]
    public void UnknownPseudoClass_ReportsEUI050()
    {
        Assert.Equal(UiDiagnostics.StyleSyntax, CodeOf(".x:spinning { Background: #fff; }"));
    }

    [Fact]
    public void UnterminatedRule_ReportsEUI050()
    {
        Assert.Equal(UiDiagnostics.StyleSyntax, CodeOf(".x { Background: #fff;"));
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
        // 'colour' 在宿主文件中的绝对位置：100 + 14（第二行�?5 个字符起�?
        Assert.True(diag.Span.Start >= 100);
        Assert.Equal("colour".Length, diag.Span.Length);
    }

    // ---------- C# 生成�?----------

    [Fact]
    public void Generator_EmitsDefineChainWithFullyQualifiedTypes()
    {
        var (sheet, diags) = Parse(".primary { Background: theme(#22a064, #1c8c58); }\n.flat { Background: #aaa; CornerRadius: 1.5; }");
        Assert.Empty(diags);

        var code = EustyleGenerator.EmitDefines(sheet, "registry", Catalog);

        Assert.Contains("registry.Define(\"primary\", s =>", code);
        Assert.Contains("new global::EclipseUI.Rendering.ThemeColor(new global::EclipseUI.Rendering.Color(34, 160, 100), new global::EclipseUI.Rendering.Color(28, 140, 88))", code);
        Assert.Contains("registry.Define(\"flat\", s =>", code);
        Assert.Contains("new global::EclipseUI.Rendering.Color(170, 170, 170)", code);
        Assert.Contains("s.CornerRadius(1.5);", code);
        // 值顺序与声明序一致（theme �?dark �?light 之前�?
        Assert.True(code.IndexOf("34, 160, 100", StringComparison.Ordinal) <
                    code.IndexOf("28, 140, 88", StringComparison.Ordinal));
    }

    // ---------- 生成器契约（Roslyn 驱动�?----------

    private sealed class TestAdditionalText : AdditionalText
    {
        private readonly string _content;

        public TestAdditionalText(string path, string content)
        {
            Path = path;
            _content = content;
        }

        public override string Path { get; }

        /// <summary>
        /// �?<see cref="RunWith"/> 在拿�?code-behind 后填入：<c>@namespace</c> 是必填契�?
        /// （spec/02 §2.2），而夹具在测试体里构造时还看不到 code-behind�?
        /// </summary>
        public string DeclaredNamespace { get; set; } = NamespaceDeclarationFixture.SyntheticNamespace;

        public override SourceText? GetText(CancellationToken cancellationToken = default)
            => SourceText.From(NamespaceDeclarationFixture.Declare(Path, _content, DeclaredNamespace));
    }

    private static GeneratorRunResult Run(params AdditionalText[] texts)
        => RunWith("public partial class Probe { }", texts);

    private static GeneratorRunResult RunWith(string codeBehind, params AdditionalText[] texts)
    {
        // .ui 的声明与 code-behind 的命名空间必须成对规整，否则 partial 合并不上�?
        // {expr} 看不�?code-behind 成员（spec/02 §2.2）�?
        var fixture = NamespaceDeclarationFixture.Prepare("Probe.ui", codeBehind);
        foreach (var text in texts.OfType<TestAdditionalText>())
            text.DeclaredNamespace = NamespaceDeclarationFixture.Prepare(text.Path, codeBehind).NamespaceName;

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(EclipseUI.UiObject).Assembly.Location))
            // design/17 §4.3：样式属性目录由符号扫描得出，探针编译必须引用渲染程序集
            .Append(MetadataReference.CreateFromFile(Path.Combine(
                Path.GetDirectoryName(typeof(EclipseUI.UiObject).Assembly.Location)!,
                "EclipseUI.Rendering.dll")))
            .ToList();
        var compilation = CSharpCompilation.Create(
            "StyleProbe",
            new[] { CSharpSyntaxTree.ParseText(fixture.CodeBehind) },
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

    /// <summary>生成产物必须�?C# 语法解析（E1 首版曾发射残�?lambda，Contains 断言没拦住、画廊真实构建才暴露）�?/summary>
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
                .demo { CornerRadius: 12; }
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
        // 错误短路：组件代码不再产�?
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void StyleBlock_NonTopLevel_ReportsEUI100()
    {
        const string ui = """
            <StackPanel>
                @style { .demo { Background: #fff; } }
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
                Background: $primary;
                &:hover { Background: #30ba78; }
                &:pressed { Background: #187e4e; }
                &:disabled { Background: #2e3e3a; }
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

    // ---------- E2：元素级 Style= 声明序列（design/17 §4.8②） ----------

    private static (EustyleDeclarations Decl, List<UiDiagnostic> Diags) ParseDeclarations(string source, int offset = 0)
    {
        var diags = new List<UiDiagnostic>();
        var decl = Eustyle.ParseDeclarations(source, "test.ui", diags, Catalog, offset);
        return (decl, diags);
    }

    [Fact]
    public void StyleDeclarations_ParseColorsAndNumbers()
    {
        var (decl, diags) = ParseDeclarations("Background: #c45499; CornerRadius: 18");

        Assert.Empty(diags);
        Assert.Equal(2, decl.Setters.Count);
        Assert.Equal("Background", decl.Setters[0].Property);
        Assert.Equal((byte)196, Assert.IsType<EustyleColor>(decl.Setters[0].Value).R);
        Assert.Equal("CornerRadius", decl.Setters[1].Property);
        Assert.Equal(18, Assert.IsType<EustyleNumber>(decl.Setters[1].Value).Value);
        Assert.All(decl.Setters, s => Assert.Equal(EustyleVariant.None, s.Variant));
    }

    [Fact]
    public void StyleDeclarations_ThemeValue_ParsesPair()
    {
        var (decl, diags) = ParseDeclarations("Foreground: theme(#2a3040, #ffffff);");

        Assert.Empty(diags);
        var theme = Assert.IsType<EustyleTheme>(Assert.Single(decl.Setters).Value);
        Assert.Equal((byte)255, theme.Light.R);
    }

    [Fact]
    public void StyleDeclarations_ClassRule_ReportsEUI050()
    {
        var (_, diags) = ParseDeclarations(".card { Background: #fff; }");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.StyleSyntax, diag.Code);
        Assert.Contains("class rules are not allowed", diag.Message);
    }

    [Fact]
    public void StyleDeclarations_Variable_ReportsEUI050()
    {
        var (_, diags) = ParseDeclarations("$pad: 8; Padding: $pad;");
        Assert.Contains(diags, d => d.Code == UiDiagnostics.StyleSyntax && d.Message.Contains("'$variable'"));
    }

    [Fact]
    public void StyleDeclarations_Empty_IsLegal()
    {
        var (decl, diags) = ParseDeclarations("");
        Assert.Empty(diags);
        Assert.Empty(decl.Setters);
    }

    [Fact]
    public void StyleDeclarations_Spans_OffsetToHostFile()
    {
        // attr.ValueSpan.Start 传入 offset——诊断与 setter �?span 都是宿主 .ui 绝对位置
        var (decl, diags) = ParseDeclarations("Padding: 4px;", offset: 37);
        Assert.Equal(UiDiagnostics.StyleValueType, Assert.Single(diags).Code);

        var (ok, _) = ParseDeclarations("Padding: 4;", offset: 37);
        Assert.Equal(37 + "Padding: ".Length, Assert.Single(ok.Setters).ValueSpan.Start);
    }

    [Fact]
    public void LocalSetters_EmitVisualCallsAndPropertyFields()
    {
        var (decl, diags) = ParseDeclarations(
            "Background: #c45499; Foreground: theme(#10131a, #f5f7fb); CornerRadius: 18; FocusRingWidth: 2; Padding: 16; FontSize: 20");
        Assert.Empty(diags);

        var code = EustyleGenerator.EmitLocalSetters(decl.Setters, "__el3", Catalog);

        Assert.Contains("__el3.SetValue(global::EclipseUI.Rendering.Visual.BackgroundProperty, new global::EclipseUI.Rendering.Color(196, 84, 153));", code.Select(c => c.Statement));
        Assert.Contains("__el3.SetValue(global::EclipseUI.Rendering.Visual.ForegroundResourceProperty, new global::EclipseUI.Rendering.ThemeColor(new global::EclipseUI.Rendering.Color(16, 19, 26), new global::EclipseUI.Rendering.Color(245, 247, 251)));", code.Select(c => c.Statement));
        Assert.Contains("__el3.SetValue(global::EclipseUI.Rendering.Visual.CornerRadiusProperty, 18);", code.Select(c => c.Statement));
        Assert.Contains("__el3.SetValue(global::EclipseUI.Rendering.Visual.FocusRingWidthProperty, 2);", code.Select(c => c.Statement));
        Assert.Contains("__el3.SetValue(global::EclipseUI.Elements.Panel.PaddingProperty, 16);", code.Select(c => c.Statement));
        Assert.Contains("__el3.SetValue(global::EclipseUI.Elements.TextBlock.FontSizeProperty, 20);", code.Select(c => c.Statement));

        // 语句必须语法成立（E1 教训：Contains 拦不住缺括号，画廊真实构建才暴露�?
        AssertGeneratedCodeParses(
            "class __EuiProbe { void M(global::EclipseUI.Element __el3) {\n" +
            string.Join("\n", code.Select(c => c.Statement)) + "\n} }");
    }

    // ---------- E2：模�?Class=/Style= 管线（EUI031 转正�?----------

    private const string SheetWithCard = """
        .card {
            Background: theme(#2a3040, #ffffff);
            CornerRadius: 12;
        }
        .primary { Foreground: #e8ecf4; }
        """;

    private const string TemplateWithCard = """
        @using EclipseUI.Elements
        <StackPanel>
            <Button Class="card primary" Style="CornerRadius: 18; Padding: 8">go</Button>
        </StackPanel>
        """;

    [Fact]
    public void ClassLiteral_KnownClasses_GenerateClassesArray_AndStyleGeneratesLocalSetters()
    {
        var result = Run(new TestAdditionalText("app.eustyle", SheetWithCard),
                         new TestAdditionalText("Probe.ui", TemplateWithCard));
        Assert.Null(result.Exception);
        Assert.Empty(result.Diagnostics);

        var code = SourceOf(result, hint => hint.Contains("Probe."));
        AssertGeneratedCodeParses(code);

        Assert.Contains("el1.ApplyClasses(new global::System.String[] { \"card\", \"primary\" });", code);
        Assert.Contains("el1.SetValue(global::EclipseUI.Rendering.Visual.CornerRadiusProperty, 18);", code);
        Assert.Contains("el1.SetValue(global::EclipseUI.Elements.Panel.PaddingProperty, 8);", code);
    }

    [Fact]
    public void ClassLiteral_UnknownClass_ReportsEUI031()
    {
        var result = Run(new TestAdditionalText("app.eustyle", SheetWithCard),
                         new TestAdditionalText("Probe.ui", """
            @using EclipseUI.Elements
            <StackPanel>
                <Button Class="card" />
                <Button Class="carrd" />
            </StackPanel>
            """));

        Assert.Null(result.Exception);
        var diag = Assert.Single(result.Diagnostics);
        Assert.Equal(UiDiagnostics.StyleAttributeInvalid, diag.Id);
        Assert.Contains("'carrd'", diag.GetMessage());
        // Error 短路的是�?.ui 的组件产物；StyleBootstrap�?eustyle 管道）不受影�?
        Assert.DoesNotContain(result.GeneratedSources, s => s.HintName.Contains("Probe."));
    }

    [Fact]
    public void ClassLiteral_WithoutAnyStylesheet_IsUnknown()
    {
        // 空目�?= 项目无任何样式类：写�?Class= 就报——编译期闭环的意义所在�?
        var result = Run(new TestAdditionalText("Probe.ui", """
            @using EclipseUI.Elements
            <StackPanel>
                <Button Class="card" />
            </StackPanel>
            """));

        Assert.Null(result.Exception);
        Assert.Contains(result.Diagnostics, d =>
            d.Id == UiDiagnostics.StyleAttributeInvalid && d.GetMessage().Contains("'card'"));
    }

    [Fact]
    public void ClassLiteral_AtStyleBlockClass_ResolvesLocally()
    {
        // §4.8�?闭环：本文件 @style 块的类名（带组件前缀）对本文�?Class= 可见�?
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel>
                <Button Class="demo" />
            </StackPanel>
            @style {
                .demo { CornerRadius: 12; }
            }
            """;

        var result = Run(new TestAdditionalText("Probe.ui", ui));
        Assert.Null(result.Exception);
        Assert.Empty(result.Diagnostics);

        var code = SourceOf(result, hint => hint.Contains("Probe."));
        // §4.8�?解析序：本文�?@style 类按注册名（组件前缀）发�?
        Assert.Contains("el1.ApplyClasses(new global::System.String[] { \"Probe__demo\" });", code);
        Assert.Contains("Define(\"Probe__demo\"", code); // 注册侧同�?
    }

    [Fact]
    public void ClassExpression_String_GeneratesSplitUpdate()
    {
        var result = RunWith("public partial class Probe { public string kind = \"card\"; }",
            new TestAdditionalText("app.eustyle", SheetWithCard),
            new TestAdditionalText("Probe.ui", """
                @using EclipseUI.Elements
                <StackPanel>
                    <Button Class={kind} />
                </StackPanel>
                """));

        Assert.Null(result.Exception);
        Assert.Empty(result.Diagnostics);
        var code = SourceOf(result, hint => hint.Contains("Probe."));
        AssertGeneratedCodeParses(code);
        Assert.Contains(".ApplyClasses(kind.Split(new[] { ' ', '\\t', '\\r', '\\n' }, global::System.StringSplitOptions.RemoveEmptyEntries));", code);
        Assert.Contains("private void U0()", code); // 根级动�?�?U 方法 + Render 登记
        Assert.Contains("protected override void Render()", code);
    }

    [Fact]
    public void ClassExpression_Collection_GeneratesToArray()
    {
        var result = RunWith("""
            using System.Collections.Generic;
            public partial class Probe { public List<string> kinds = new(); }
            """,
            new TestAdditionalText("app.eustyle", SheetWithCard),
            new TestAdditionalText("Probe.ui", """
                @using EclipseUI.Elements
                <StackPanel>
                    <Button Class={kinds} />
                </StackPanel>
                """));

        Assert.Null(result.Exception);
        Assert.Empty(result.Diagnostics);
        var code = SourceOf(result, hint => hint.Contains("Probe."));
        Assert.Contains("global::System.Linq.Enumerable.ToArray(kinds)", code);
    }

    [Fact]
    public void ClassExpression_InvalidType_ReportsEUI031()
    {
        var result = RunWith("public partial class Probe { public int kind = 4; }",
            new TestAdditionalText("app.eustyle", SheetWithCard),
            new TestAdditionalText("Probe.ui", """
                @using EclipseUI.Elements
                <StackPanel>
                    <Button Class={kind} />
                </StackPanel>
                """));

        Assert.Null(result.Exception);
        var diag = Assert.Single(result.Diagnostics);
        Assert.Equal(UiDiagnostics.StyleAttributeInvalid, diag.Id);
        Assert.Contains("string collection", diag.GetMessage());
    }

    [Fact]
    public void BindClass_ReportsEUI040()
    {
        var result = Run(new TestAdditionalText("Probe.ui", """
            @using EclipseUI.Elements
            <StackPanel>
                <Button @bind-Class={x} />
            </StackPanel>
            """));

        Assert.Null(result.Exception);
        Assert.Contains(result.Diagnostics, d => d.Id == UiDiagnostics.BindTargetInvalid);
    }

    [Fact]
    public void StyleExpression_ReportsEUI031()
    {
        var result = Run(new TestAdditionalText("Probe.ui", """
            @using EclipseUI.Elements
            <StackPanel>
                <Button Style={expr} />
            </StackPanel>
            """));

        Assert.Null(result.Exception);
        var diag = Assert.Single(result.Diagnostics);
        Assert.Equal(UiDiagnostics.StyleAttributeInvalid, diag.Id);
        Assert.Contains("static declaration list", diag.GetMessage());
    }

    [Fact]
    public void StyleLiteral_UnknownProperty_ReportsEUI051_AtValueSpan()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel>
                <Button Style="colour: #fff" />
            </StackPanel>
            """;

        var result = Run(new TestAdditionalText("Probe.ui", ui));
        Assert.Null(result.Exception);
        var diag = Assert.Single(result.Diagnostics);
        Assert.Equal(UiDiagnostics.StyleUnknownProperty, diag.Id);
        Assert.Contains("colour", diag.GetMessage());
        Assert.Contains("Probe.ui", diag.Location.GetLineSpan().Path);
    }

    // ---------- E4：嵌套塌缩（design/17 §4.5，EUI053�?----------

    private static List<UiDiagnostic> Collapse(EustyleStylesheet sheet, out EustyleCollapse.Collapsed collapsed)
    {
        var diags = new List<UiDiagnostic>();
        collapsed = EustyleCollapse.Flatten(sheet, diags);
        return diags;
    }

    [Fact]
    public void NestedRules_ParseDirectChildDescendantAndConstraint()
    {
        const string source = """
            .panel {
                Background: #222222;
                > Button { Padding: 8; }
                TextBlock.title { FontSize: 20; }
                .title { CornerRadius: 4; }
            }
            """;

        var (sheet, diags) = Parse(source);
        Assert.Empty(diags);

        var panel = Assert.Single(sheet.Rules);
        Assert.Single(panel.Setters);
        Assert.Equal(3, panel.Nested.Count);

        var direct = panel.Nested[0];
        Assert.True(direct.DirectChild);
        Assert.Equal("Button", direct.TypeName);
        Assert.Null(direct.ClassConstraint);
        Assert.Equal("Padding", Assert.Single(direct.Setters).Property);

        var descendant = panel.Nested[1];
        Assert.False(descendant.DirectChild);
        Assert.Equal("TextBlock", descendant.TypeName);
        Assert.Equal("title", descendant.ClassConstraint);

        var constraintOnly = panel.Nested[2];
        Assert.Null(constraintOnly.TypeName);
        Assert.Equal("title", constraintOnly.ClassConstraint);
    }

    [Fact]
    public void NestedCollapse_NamesDerivedClasses_KebabAndChains()
    {
        const string source = """
            .panel {
                > Button { Padding: 8; }
                TextBlock.title { FontSize: 20; }
                .title { CornerRadius: 4; }
                > Button { > Text { CornerRadius: 2; } }
            }
            """;

        var (sheet, _) = Parse(source);
        var diags = Collapse(sheet, out var collapsed);

        Assert.Empty(diags);
        Assert.Equal(
            new[] { "panel", "panel__button", "panel__text-block-title", "panel__title", "panel__button__text" },
            collapsed.Rules.Select(static r => r.ClassName).ToArray());

        var button = collapsed.Injects.Single(static i => i.DerivedName == "panel__button");
        Assert.Equal("panel", button.RootClass);
        var seg = Assert.Single(button.Chain);
        Assert.True(seg.DirectChild);
        Assert.Equal("button", seg.TypeName); // ChainSeg 类型名已 kebab 化（与匹配侧同一规则�?
        Assert.Null(seg.ClassConstraint);

        var text = collapsed.Injects.Single(static i => i.DerivedName == "panel__button__text");
        Assert.Equal(2, text.Chain.Count);
        Assert.True(text.Chain[1].DirectChild);
        Assert.Equal("text", text.Chain[1].TypeName);
    }

    [Fact]
    public void NestedCollapse_SameDerivedName_MergesSettersAndSubtree()
    {
        // CSS 同选择器多�?= 声明合并（声明序后者胜）；同名子嵌套同理递归聚合
        const string source = """
            .panel {
                > Button { Padding: 8; }
                > Button { CornerRadius: 2; > Text { FontSize: 12; } }
                > Button:hover { Padding: 4; }
            }
            """;

        var (sheet, _) = Parse(source);
        var diags = Collapse(sheet, out var collapsed);

        Assert.Empty(diags);
        Assert.Equal(new[] { "panel", "panel__button", "panel__button__text" },
            collapsed.Rules.Select(static r => r.ClassName).ToArray());

        var button = Assert.Single(collapsed.Rules, static r => r.ClassName == "panel__button");
        Assert.Equal(3, button.Setters.Count); // 两个块的声明合并 + :hover 变体
        Assert.Single(collapsed.Injects, static i => i.DerivedName == "panel__button");

        var text = Assert.Single(collapsed.Rules, static r => r.ClassName == "panel__button__text");
        Assert.Single(text.Setters);
    }

    [Fact]
    public void NestedCollapse_CollidesWithExplicitClass_ReportsEUI053()
    {
        const string source = """
            .panel__button { Padding: 99; }
            .panel { > Button { Padding: 8; } }
            """;

        var (sheet, _) = Parse(source);
        var diags = Collapse(sheet, out _);

        Assert.Equal(UiDiagnostics.StyleDerivedNameConflict, Assert.Single(diags).Code);
    }

    [Fact]
    public void NestedState_SelectorAndVariantBlock_BothLandOnDerivedClass()
    {
        // '> Button:hover {}' �?'> Button { &:hover {} }' 两种写法等价——都归派生类�?Hover 变体
        const string source = """
            .panel {
                > Button:hover { Padding: 4; }
                > Label { &:focus { FontSize: 15; } }
            }
            """;

        var (sheet, diags) = Parse(source);
        Assert.Empty(diags);

        var directButton = sheet.Rules[0].Nested[0];
        Assert.Equal(EustyleVariant.Hover, directButton.Setters.Single().Variant);
        var label = sheet.Rules[0].Nested[1];
        Assert.Equal(EustyleVariant.Focus, label.Setters.Single().Variant);
    }

    [Fact]
    public void NestedSelectorInsideVariantBlock_ReportsEUI050()
    {
        var (_, diags) = Parse(".a { &:hover { > Button { Padding: 8; } } }");

        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.StyleSyntax, diag.Code);
        Assert.Contains("nested selectors are not allowed", diag.Message);
    }

    [Fact]
    public void NestedCollapse_GeneratorEmitsDerivedDefines()
    {
        const string source = """
            .panel {
                Background: #222222;
                > Button { Padding: 8; }
            }
            """;

        var (sheet, _) = Parse(source);
        Collapse(sheet, out var collapsed);
        var code = EustyleGenerator.EmitDefines(collapsed.Rules, "registry", Catalog);

        Assert.Contains("registry.Define(\"panel\", s =>", code);
        Assert.Contains("registry.Define(\"panel__button\", s =>", code);
        AssertGeneratedCodeParses("class __P { void M(global::EclipseUI.Rendering.StyleRegistry registry) {\n" + code + "\n} }");
    }

    // ---------- E4：模板注入管�?----------

    private const string NestedSheet = """
        .panel {
            Background: theme(#2a3040, #ffffff);
            > Button { Padding: 8; }
        }
        .primary { Foreground: #e8ecf4; }
        """;

    [Fact]
    public void Inject_DirectChildButton_GeneratesInjectedClasses()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel Class="panel">
                <Button>injected</Button>
                <Button Class="primary">both</Button>
            </StackPanel>
            """;

        var result = Run(new TestAdditionalText("app.eustyle", NestedSheet),
                         new TestAdditionalText("Probe.ui", ui));
        Assert.Null(result.Exception);
        Assert.Empty(result.Diagnostics);

        var code = SourceOf(result, hint => hint.Contains("Probe."));
        AssertGeneratedCodeParses(code);

        // �?Class 属性的按钮：只发注入类列表
        Assert.Contains("this.ApplyTemplateClasses(new global::System.String[] { \"panel\" });", code);
        Assert.Contains("el1.ApplyClasses(new global::System.String[] { \"panel__button\" });", code);
        // 已有类名的按钮：注入排在解析类名之后（design/17 §2.2 派生类胜�?
        Assert.Contains("el2.ApplyClasses(new global::System.String[] { \"primary\", \"panel__button\" });", code);
    }

    [Fact]
    public void Inject_InsideForeach_MatchesStaticTemplate()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel Class="panel">
                @foreach (var item in items)
                {
                    <Button>row</Button>
                }
            </StackPanel>
            """;

        var result = RunWith("public partial class Probe { public global::System.Collections.Generic.List<string> items = new(); }",
            new TestAdditionalText("app.eustyle", NestedSheet),
            new TestAdditionalText("Probe.ui", ui));

        Assert.Null(result.Exception);
        Assert.Empty(result.Diagnostics);
        var code = SourceOf(result, hint => hint.Contains("Probe."));
        Assert.Contains(".ApplyClasses(new global::System.String[] { \"panel__button\" });", code);
    }

    [Fact]
    public void Inject_ExpressionClass_SkipsInjection()
    {
        // §4.5-3 明示边界：表达式 Class 的运行时类列表编译期未知，塌缩注入跳�?
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel Class="panel">
                <Button Class={kind}>dynamic</Button>
            </StackPanel>
            """;

        var result = RunWith("public partial class Probe { public string kind = \"primary\"; }",
            new TestAdditionalText("app.eustyle", NestedSheet),
            new TestAdditionalText("Probe.ui", ui));

        Assert.Null(result.Exception);
        Assert.Empty(result.Diagnostics);
        var code = SourceOf(result, hint => hint.Contains("Probe."));
        Assert.DoesNotContain("\"panel__button\"", code);
        Assert.Contains(".ApplyClasses(kind.Split(", code);
    }

    [Fact]
    public void Inject_DerivedClassInCatalog_ClassReferenceValidates()
    {
        // 塌缩派生类同样进样式目录：模板直接写 Class="panel__button" 编译期校验通过
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel Class="panel">
                <Button Class="panel__button">manual</Button>
            </StackPanel>
            """;

        var result = Run(new TestAdditionalText("app.eustyle", NestedSheet),
                         new TestAdditionalText("Probe.ui", ui));
        Assert.Null(result.Exception);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void StyleBlock_NestedRules_InjectWithLocalPrefix()
    {
        // @style 块嵌套：派生类带组件前缀注册，注入按注册名闭�?
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel Class="panel">
                <Button>go</Button>
            </StackPanel>
            @style {
                .panel {
                    Background: #222222;
                    > Button { CornerRadius: 6; }
                }
            }
            """;

        var result = Run(new TestAdditionalText("Probe.ui", ui));
        Assert.Null(result.Exception);
        Assert.Empty(result.Diagnostics);

        var code = SourceOf(result, hint => hint.Contains("Probe."));
        AssertGeneratedCodeParses(code);
        Assert.Contains("el1.ApplyClasses(new global::System.String[] { \"Probe__panel__button\" });", code);
        Assert.Contains("Define(\"Probe__panel__button\"", code);
    }

    [Fact]
    public void Stylesheet_NestedRules_EmitDerivedDefinesInBootstrap()
    {
        var result = Run(new TestAdditionalText("app.eustyle", NestedSheet));
        var code = SourceOf(result, hint => hint.Contains("StyleBootstrap.g.cs"));
        AssertGeneratedCodeParses(code);

        Assert.Contains("registry.Define(\"panel\", s =>", code);
        Assert.Contains("registry.Define(\"panel__button\", s =>", code);
        Assert.Contains("s.Padding(8);", code);
        Assert.Empty(result.Diagnostics);
    }

    // ---------- E5：注册制伪类（design/17 §3.2/§5/§7�?---------

    private static (EustyleStylesheet Sheet, List<UiDiagnostic> Diags) ParseWithStates(
        string source, IReadOnlyCollection<string>? externalStates = null, int offset = 0)
    {
        var diags = new List<UiDiagnostic>();
        var sheet = Eustyle.Parse(source, "test.eustyle", diags, Catalog, offset, externalStates);
        return (sheet, diags);
    }

    [Fact]
    public void StatesDeclaration_SelectorForm_ParsesRegisteredStateAndEmitsRegisterChain()
    {
        const string source = """
            @states { spinning; }
            .spinner {
                Background: theme(#2a3040, #ffffff);
                &:spinning { Foreground: #22c55e; }
            }
            .busy:spinning { CornerRadius: 4; }
            """;

        var (sheet, diags) = ParseWithStates(source);
        Assert.Empty(diags);
        var state = Assert.Single(sheet.States);
        Assert.Equal("spinning", state.Name);

        var spinner = sheet.Rules[0];
        // 基础 + 变体块归属注册名（Variant 保持 None，RegisteredState 承载归属�?
        Assert.Equal(EustyleVariant.None, spinner.Setters[0].Variant);
        Assert.Null(spinner.Setters[0].RegisteredState);
        var variant = spinner.Setters[1];
        Assert.Equal(EustyleVariant.None, variant.Variant);
        Assert.Equal("spinning", variant.RegisteredState);

        var selectorForm = sheet.Rules[1].Setters.Single();
        Assert.Equal(EustyleVariant.None, selectorForm.Variant);
        Assert.Equal("spinning", selectorForm.RegisteredState);

        // 生成代码：Apply 顶部注册�?+ 变体�?.State(Register("spinning"), �?
        var result = Run(new TestAdditionalText("app.eustyle", source));
        var code = SourceOf(result, hint => hint.Contains("StyleBootstrap.g.cs"));
        AssertGeneratedCodeParses(code);
        Assert.Contains("global::EclipseUI.PseudoStatesRegistry.Register(\"spinning\");", code);
        Assert.Contains(".State(global::EclipseUI.PseudoStatesRegistry.Register(\"spinning\"), s =>", code);
        Assert.Contains("s.Foreground(", code);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void StatesDeclaration_DeclarationOrderIndependent_AndDeduplicated()
    {
        // 声明写在规则之后同样可用（预扫两遍解析）；重复声明去重�?
        const string source = """
            .spinner:loading { Background: #22c55e; }
            @states {
                loading;
                loading, busy;
            }
            .busy:busy { Foreground: #fff; }
            """;

        var (sheet, diags) = ParseWithStates(source);
        Assert.Empty(diags);
        Assert.Equal(["loading", "busy"], sheet.States.Select(s => s.Name).ToArray());
        Assert.Equal("loading", sheet.Rules[0].Setters.Single().RegisteredState);
        Assert.Equal("busy", sheet.Rules[1].Setters.Single().RegisteredState);
    }

    [Fact]
    public void StatesDeclaration_NestedSelector_RegisteredState()
    {
        const string source = """
            @states { spinning; }
            .card {
                Background: #222222;
                > Button:spinning { Foreground: #22c55e; }
            }
            """;

        var (sheet, diags) = ParseWithStates(source);
        Assert.Empty(diags);
        var nested = Assert.Single(sheet.Rules[0].Nested);
        var setter = Assert.Single(nested.Setters);
        Assert.Equal("spinning", setter.RegisteredState);
    }

    [Fact]
    public void StatesDeclaration_UndeclaredName_ReportsEUI050()
    {
        var (_, diags) = ParseWithStates(".spinner:loading { Background: #fff; }");

        // EUI050 �?Error �?产物整体短路（无效状态归属不进产物，断言只看诊断）�?
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.StyleSyntax, diag.Code);
        Assert.Contains("unknown pseudo-class ':loading'", diag.Message);
        Assert.Contains("@states", diag.Message);
    }

    [Fact]
    public void StatesDeclaration_PlatformNameConflict_ReportsEUI050()
    {
        var (_, diags) = ParseWithStates("@states { hover; loading; }");

        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.StyleSyntax, diag.Code);
        Assert.Contains("platform pseudo-class", diag.Message);
    }

    [Fact]
    public void StatesDeclaration_CrossFileReference_ResolvesViaExternalCatalog()
    {
        // 本文件未声明、外部目录（其他样式来源�?@states 并集）已声明 �?引用通过�?
        // 外部目录也没�?�?报错�?
        const string source = ".spinner:loading { Background: #fff; }";

        var (sheet, okDiags) = ParseWithStates(source, ["loading"]);
        Assert.Empty(okDiags);
        Assert.Equal("loading", sheet.Rules[0].Setters.Single().RegisteredState);

        var (_, missingDiags) = ParseWithStates(source, ["other"]);
        Assert.Contains(missingDiags, d => d.Code == UiDiagnostics.StyleSyntax);
    }

    [Fact]
    public void StyleBlock_AtStates_EmitsRegisterSequenceInComponentStyles()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel />
            @style {
                @states { spinning; }
                .demo:spinning { CornerRadius: 18; }
            }
            """;

        var result = Run(new TestAdditionalText("Probe.ui", ui));
        var code = SourceOf(result, hint => hint.Contains("Probe."));
        AssertGeneratedCodeParses(code);
        // 注册名是全局掩码命名空间——不做组件前缀（与类名前缀机制解耦）
        Assert.Contains("global::EclipseUI.PseudoStatesRegistry.Register(\"spinning\");", code);
        Assert.Contains(".State(global::EclipseUI.PseudoStatesRegistry.Register(\"spinning\"), s =>", code);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void StyleAttribute_AtDirective_ReportsEUI050()
    {
        // 元素属性没有「状态」语境（design/17 §4.8②）——@states 与一�?@ 指令不适用�?
        // 直测 ParseDeclarations�?ui 属性值里�?'@' �?Lexer 指令通道处理，是既有限制）�?
        var (_, diags) = ParseDeclarations("Background: #fff; @states { loading; }");

        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.StyleSyntax, diag.Code);
        Assert.Contains("not allowed in Style=", diag.Message);
    }

    [Fact]
    public void DualChannel_HandwrittenDefine_SameName_ReportsEUI054Warning()
    {
        // 宿主 code-behind 手写 Define("card", �? × 样式�?.card �?EUI054 Warning（不短路生成）�?
        const string sheet = """
            .card { Background: theme(#2a3040, #ffffff); }
            .unique { Padding: 8; }
            """;
        const string codeBehind = """
            using EclipseUI.Rendering;
            public partial class Probe
            {
                static Probe()
                {
                    StyleRegistry.Instance.Define("card", s => s.Padding(4));
                }
            }
            """;

        var result = RunWith(codeBehind, new TestAdditionalText("app.eustyle", sheet));
        Assert.Null(result.Exception);
        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal(UiDiagnostics.StyleDualChannelConflict, warning.Id);
        // Warning 不短路：产物照常生成
        var code = SourceOf(result, hint => hint.Contains("StyleBootstrap.g.cs"));
        Assert.Contains("registry.Define(\"card\", s =>", code);
    }

    [Fact]
    public void DualChannel_NoOverlap_NoDiagnostics()
    {
        const string sheet = ".unique { Padding: 8; }";
        const string codeBehind = """
            using EclipseUI.Rendering;
            public partial class Probe
            {
                static Probe()
                {
                    StyleRegistry.Instance.Define("onlyHandwritten", s => s.Padding(4));
                }
            }
            """;

        var result = RunWith(codeBehind, new TestAdditionalText("app.eustyle", sheet));
        Assert.Empty(result.Diagnostics);
    }

    // ---------- design/17 §4.3：符号派生目�?+ CLR 原名规范�?----------

    [Fact]
    public void CanonicalClrNames_ParseToCatalogSetters()
    {
        var (sheet, diags) = Parse(".a { CornerRadius: 8; FontSize: 20; FocusRingWidth: 2; }");

        Assert.Empty(diags);
        Assert.Equal(new[] { "CornerRadius", "FontSize", "FocusRingWidth" },
            sheet.Rules.Single().Setters.Select(s => s.Property));
    }

    [Theory]
    [InlineData("corner-radius")]
    [InlineData("cornerradius")]
    [InlineData("Corner_Radius")]
    [InlineData("CORNERRADIUS")]
    [InlineData("background")]  // 大小写敏感：Background 存在，全小写不存�?
    public void LegacyOrCaseVariantNames_AreRejectedWithEUI051(string legacy)
    {
        // 只认控件原始 CLR 名，�?kebab 别名、无大小�?分隔符归一化�?
        var (_, diags) = Parse($".a {{ {legacy}: 8; }}");

        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.StyleUnknownProperty, diag.Code);
        Assert.Contains(legacy, diag.Message);
        Assert.Contains("case-sensitive CLR name", diag.Message);
    }

    [Fact]
    public void StyleDeclarations_LegacySpelling_ReportsEUI051()
    {
        var (_, diags) = ParseDeclarations("corner-radius: 18");
        Assert.Equal(UiDiagnostics.StyleUnknownProperty, Assert.Single(diags).Code);
    }

    [Fact]
    public void PascalCaseProperty_IsNotParsedAsNestedTypeSelector()
    {
        var (sheet, diags) = Parse("""
            .card {
                CornerRadius: 8;
                > Button { CornerRadius: 4; }
                TextBlock.hint { FontSize: 12; }
            }
            """);

        Assert.Empty(diags);
        var rule = Assert.Single(sheet.Rules);
        Assert.Equal("CornerRadius", Assert.Single(rule.Setters).Property);
        Assert.Equal(2, rule.Nested.Count);
        Assert.All(rule.Nested, n => Assert.Single(n.Setters));

        var collapsed = EustyleCollapse.Flatten(sheet, new List<UiDiagnostic>());
        Assert.Equal(3, collapsed.Rules.Count); // 根类 + 两个派生�?
    }

    [Fact]
    public void UnknownPascalCaseProperty_ReportsEUI051WithDerivedNameList()
    {
        var (_, diags) = Parse(".a { Bogus: 1; }");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.StyleUnknownProperty, diag.Code);
        Assert.Contains("Bogus", diag.Message);
        Assert.Contains("CornerRadius", diag.Message); // 支持列表来自目录，不再是手写数组
    }

    [Fact]
    public void ThemeValue_RejectedWhenCatalogHasNoResourceField()
    {
        var synthetic = new StylePropertyCatalog(
            new[] { new StylePropertyDef("Accent", StyleValueKind.Color,
                "global::EclipseUI.Rendering.Visual.BackgroundProperty", null,
                "global::EclipseUI.Rendering.Visual") },
            Array.Empty<UiMetadataProblem>());
        var diags = new List<UiDiagnostic>();
        Eustyle.Parse(".a { Accent: theme(#000000, #ffffff); }", "synthetic.eustyle", diags, synthetic);

        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.StyleValueType, diag.Code);
        Assert.Contains("theme(dark, light)", diag.Message);
    }

    [Fact]
    public void Generator_CanonicalStyleAttribute_EmitsCatalogField()
    {
        var result = Run(new TestAdditionalText("Probe.ui", """
            @using EclipseUI.Elements
            <Button Style="CornerRadius: 18; Padding: 8">go</Button>
            """));

        Assert.Null(result.Exception);
        Assert.Empty(result.Diagnostics);
        var code = SourceOf(result, hint => hint.Contains("Probe."));
        Assert.Contains("SetValue(global::EclipseUI.Rendering.Visual.CornerRadiusProperty, 18)", code);
        Assert.Contains("SetValue(global::EclipseUI.Elements.Panel.PaddingProperty, 8)", code);
        AssertGeneratedCodeParses(code);
    }

    // ---------- design/19：自定义样式属性（[UiStyleProperty] + 值语法扩展） ----------

    /// <summary>标注通道的目录：probe 源里声明 [UiStyleProperty] 字段，与内建并集（真符号派生）。</summary>
    private static StylePropertyCatalog CustomCatalog(string cs) => UiTestCatalogs.Scan(cs).StyleProperties;

    /// <summary>design/19 §4.1 的示例声明（Glow 数字 / Tone 枚举 / Label 字符串）。</summary>
    private const string BadgeSource = """
        public enum Tone { Neutral, Success, Danger }
        public static class BadgeStyles
        {
            [UiStyleProperty]
            public static readonly StyledProperty<double> GlowProperty =
                StyledProperty<double>.Register<Probe>("Glow", 0d);
            [UiStyleProperty(Name = "Tone")]
            public static readonly StyledProperty<Tone> ToneProperty =
                StyledProperty<Tone>.Register<Probe>("Tone", Tone.Neutral);
            [UiStyleProperty]
            public static readonly StyledProperty<string> LabelProperty =
                StyledProperty<string>.Register<Probe>("Label", "");
        }
        """;

    private static (EustyleStylesheet Sheet, List<UiDiagnostic> Diags) ParseWith(
        StylePropertyCatalog catalog, string source)
    {
        var diags = new List<UiDiagnostic>();
        var sheet = Eustyle.Parse(source, "custom.eustyle", diags, catalog);
        return (sheet, diags);
    }

    private static (EustyleDeclarations Decl, List<UiDiagnostic> Diags) ParseDeclarationsWith(
        StylePropertyCatalog catalog, string source)
    {
        var diags = new List<UiDiagnostic>();
        var decl = Eustyle.ParseDeclarations(source, "ProbeDoc.ui", diags, catalog);
        return (decl, diags);
    }

    [Fact]
    public void AnnotatedFields_EnterCatalogWithDerivedKinds()
    {
        var catalog = CustomCatalog(BadgeSource);
        Assert.Empty(catalog.Problems);

        var glow = catalog.Find("Glow");
        Assert.NotNull(glow);
        Assert.False(glow!.IsBuiltIn);
        Assert.Equal(StyleValueKind.Number, glow.Kind);
        Assert.StartsWith("global::", glow.PropertyField);
        Assert.EndsWith("BadgeStyles.GlowProperty", glow.PropertyField);

        // 显式 Name 生效；枚举属性带成员表与全限定枚举类型名（升序）。
        var tone = catalog.Find("Tone");
        Assert.NotNull(tone);
        Assert.Equal(StyleValueKind.Enum, tone!.Kind);
        Assert.Equal("global::BadgeStyles.ToneProperty", tone.PropertyField);
        Assert.Equal("global::Tone", tone.EnumTypeName);
        Assert.Equal(new[] { "Danger", "Neutral", "Success" }, tone.Candidates);

        var label = catalog.Find("Label");
        Assert.NotNull(label);
        Assert.Equal(StyleValueKind.Text, label!.Kind);

        // 内建 6 属性零回归：并集目录里全部保留。
        Assert.NotNull(catalog.Find("CornerRadius"));
        Assert.NotNull(catalog.Find("Background"));
    }

    [Fact]
    public void CustomEnumProperty_KeywordValue_ParsesAndEmitsEnumMember()
    {
        var catalog = CustomCatalog(BadgeSource);
        var (sheet, diags) = ParseWith(catalog, ".badge { Tone: Success; Glow: 6; Label: \"hi\"; }");
        Assert.Empty(diags);

        var setters = Assert.Single(sheet.Rules).Setters;
        Assert.Equal("Tone", setters[0].Property);
        Assert.Equal("Success", Assert.IsType<EustyleKeyword>(setters[0].Value).Name);
        Assert.Equal("Glow", setters[1].Property);
        Assert.Equal("Label", setters[2].Property);
        Assert.Equal("hi", Assert.IsType<EustyleString>(setters[2].Value).Value);

        var code = EustyleGenerator.EmitDefines(sheet, "registry", catalog);
        // 自定义属性走泛型逃生口 s.Set(field, value)；枚举发射 T.Member，字符串走 C# 转义。
        Assert.Contains("s.Set(global::BadgeStyles.ToneProperty, global::Tone.Success);", code);
        Assert.Contains("s.Set(global::BadgeStyles.GlowProperty, 6);", code);
        Assert.Contains("s.Set(global::BadgeStyles.LabelProperty, \"hi\");", code);
        // 内建属性仍走命名方法。
        Assert.Contains("s.CornerRadius", EustyleGenerator.EmitDefines(
            Eustyle.Parse(".a { CornerRadius: 4; }", "b.eustyle", new List<UiDiagnostic>(), catalog)
                , "registry", catalog));
    }

    [Fact]
    public void CustomEnumProperty_UnknownMember_ReportsEUI052WithMembers()
    {
        var catalog = CustomCatalog(BadgeSource);
        var (sheet, diags) = ParseWith(catalog, ".badge { Tone: Dangerously; }");

        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.StyleValueType, diag.Code);
        Assert.Contains("'Dangerously' is not a member of global::Tone", diag.Message);
        Assert.Contains("Danger, Neutral, Success", diag.Message);
        Assert.Empty(sheet.Rules.Single().Setters);
    }

    [Fact]
    public void CustomProperties_ValueKindMismatches_ReportsEUI052()
    {
        var catalog = CustomCatalog(BadgeSource);
        Assert.Equal(UiDiagnostics.StyleValueType,
            ParseWith(catalog, ".a { Glow: Center; }").Diags.Single().Code);        // keyword → number 属性
        Assert.Equal(UiDiagnostics.StyleValueType,
            ParseWith(catalog, ".a { Glow: \"6\"; }").Diags.Single().Code);          // string → number 属性
        Assert.Equal(UiDiagnostics.StyleValueType,
            ParseWith(catalog, ".a { Label: 6; }").Diags.Single().Code);             // number → string 属性
        Assert.Equal(UiDiagnostics.StyleValueType,
            ParseWith(catalog, ".a { Tone: \"Success\"; }").Diags.Single().Code);    // string → enum 属性
        Assert.Equal(UiDiagnostics.StyleValueType,
            ParseWith(catalog, ".a { Label: #fff; }").Diags.Single().Code);          // color → string 属性
    }

    [Fact]
    public void StringProperty_AcceptsKeywordDirectPass()
    {
        var catalog = CustomCatalog(BadgeSource);
        var (sheet, diags) = ParseWith(catalog, ".a { Label: hello; }");
        Assert.Empty(diags);
        var setter = Assert.Single(sheet.Rules.Single().Setters);
        Assert.Equal("hello", Assert.IsType<EustyleKeyword>(setter.Value).Name);
    }

    [Fact]
    public void CustomProperty_RejectsTheme_NoResourceField()
    {
        var catalog = CustomCatalog(BadgeSource);
        var (_, diags) = ParseWith(catalog, ".a { Glow: theme(#000000, #ffffff); }");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.StyleValueType, diag.Code);
        Assert.Contains("theme(dark, light)", diag.Message);
    }

    [Fact]
    public void Variables_KeepNumberAndColorOnly()
    {
        var catalog = CustomCatalog(BadgeSource);
        var (_, diags) = ParseWith(catalog, "$t: Success;\n.a { Glow: 4; }");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.StyleSyntax, diag.Code);
        Assert.Contains("limited to numbers and colors", diag.Message);
    }

    [Fact]
    public void InlineStyle_EmitsSetValueForCustomProperties()
    {
        var catalog = CustomCatalog(BadgeSource);
        var (decl, diags) = ParseDeclarationsWith(catalog, "Glow: 6; Tone: Success; Label: inline");
        Assert.Empty(diags);

        var statements = EustyleGenerator.EmitLocalSetters(decl.Setters, "el", catalog);
        Assert.Contains("el.SetValue(global::BadgeStyles.GlowProperty, 6);",
            statements.Select(s => s.Statement));
        Assert.Contains("el.SetValue(global::BadgeStyles.ToneProperty, global::Tone.Success);",
            statements.Select(s => s.Statement));
        Assert.Contains("el.SetValue(global::BadgeStyles.LabelProperty, \"inline\");",
            statements.Select(s => s.Statement));
    }

    [Fact]
    public void Pipeline_ClassChannel_AndInlineStyle_ReachCustomProperties()
    {
        var result = RunWith("""
            using EclipseUI;
            public enum Tone { Neutral, Success, Danger }
            public static class BadgeStyles
            {
                [UiStyleProperty]
                public static readonly StyledProperty<double> GlowProperty =
                    StyledProperty<double>.Register<Probe>("Glow", 0d);
                [UiStyleProperty(Name = "Tone")]
                public static readonly StyledProperty<Tone> ToneProperty =
                    StyledProperty<Tone>.Register<Probe>("Tone", Tone.Neutral);
            }
            public partial class Probe { }
            """,
            new TestAdditionalText("Probe.ui", """
                @using EclipseUI.Elements
                <StackPanel Class="badge">
                    <Button Style="Glow: 6; Tone: Success">go</Button>
                </StackPanel>
                """),
            new TestAdditionalText("app.eustyle", """
                .badge {
                    Glow: 4;
                    Tone: Danger;
                }
                """));

        Assert.Null(result.Exception);
        Assert.Empty(result.Diagnostics);

        var bootstrap = SourceOf(result, hint => hint.Contains("StyleBootstrap"));
        Assert.Contains("registry.Define(\"badge\", s =>", bootstrap);
        Assert.Contains("s.Set(global::TestFixtures.BadgeStyles.GlowProperty, 4);", bootstrap);
        Assert.Contains("s.Set(global::TestFixtures.BadgeStyles.ToneProperty, global::TestFixtures.Tone.Danger);", bootstrap);
        AssertGeneratedCodeParses(bootstrap);

        var component = SourceOf(result, hint => hint.Contains("Probe."));
        Assert.Contains("SetValue(global::TestFixtures.BadgeStyles.GlowProperty, 6)", component);
        Assert.Contains("SetValue(global::TestFixtures.BadgeStyles.ToneProperty, global::TestFixtures.Tone.Success)", component);
        AssertGeneratedCodeParses(component);
    }

    [Fact]
    public void Pipeline_UnknownCustomProperty_InUnionCatalog_StillEUI051()
    {
        var result = Run(new TestAdditionalText("Probe.ui", """
            @using EclipseUI.Elements
            <StackPanel />
            @style {
                .demo { Glow: 6; }
            }
            """));

        // 目录里没有 Glow（codeBehind 没声明标注字段）→ 照常 EUI051。
        var diag = Assert.Single(result.Diagnostics);
        Assert.Equal(UiDiagnostics.StyleUnknownProperty, diag.Id);
        Assert.Contains("Glow", diag.GetMessage());
    }
}
