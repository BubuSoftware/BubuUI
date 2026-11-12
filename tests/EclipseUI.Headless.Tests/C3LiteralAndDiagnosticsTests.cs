using System;
using System.Collections.Generic;
using System.Linq;
using EclipseUI.Compiler;
using EclipseUI.TestKit;
using Xunit;

namespace EclipseUI.Headless.Tests;

/// <summary>
/// C3 切片：转换器扩展（float/decimal/long/short/byte/char/枚举/TimeSpan）、
/// EUI023（无转换器）、EUI025（完全重复属性）、EUI031（保留语法）。
/// 规范条文：spec/01 §3.1/§5.2/§6.2；design/14 §14.7.5/§14.12。
/// </summary>
public class C3LiteralAndDiagnosticsTests
{
    private static (UiDocument Doc, List<UiDiagnostic> Diags) ParseAnalyze(UiCatalog catalog, string text)
    {
        var diags = new List<UiDiagnostic>();
        var doc = FrontEnd.Parse(text, "Test.ui", diags);
        FrontEnd.Analyze(doc, catalog, diags);
        return (doc, diags);
    }

    /// <summary>
    /// 探针标签：目录从真实运行时扫描而来（§14.11.1），这里在其之上追加一个合成标签，
    /// 专门用来测 C3 转换器与 EUI023——不是第二份生产目录，只服务诊断测试。
    /// </summary>
    private static UiCatalog CatalogWithProbe(IReadOnlyList<UiLiteralParserDef>? literalParsers = null)
    {
        var built = UiTestCatalogs.BuiltIn();
        UiPropDef P(string name, UiTypeShape shape) => new(
            name, "global::Probe.Things", "global::Probe.Things", name + "Property", true, shape);
        var probe = new UiElementDef(
            "Probe", "global::Probe.Things", UiElementOrigin.Control,
            UiContentModel.None, null,
            new Dictionary<string, UiPropDef>(StringComparer.Ordinal)
            {
                ["Rate"] = P("Rate", new UiTypeShape(UiTypeShapeKind.Primitive, "float", [])),
                ["Blend"] = P("Blend", new UiTypeShape(UiTypeShapeKind.Primitive, "decimal", [])),
                ["Total"] = P("Total", new UiTypeShape(UiTypeShapeKind.Primitive, "long", [])),
                ["Age"] = P("Age", new UiTypeShape(UiTypeShapeKind.Primitive, "short", [])),
                ["Cell"] = P("Cell", new UiTypeShape(UiTypeShapeKind.Primitive, "byte", [])),
                ["Letter"] = P("Letter", new UiTypeShape(UiTypeShapeKind.Primitive, "char", [])),
                ["Delay"] = P("Delay", new UiTypeShape(UiTypeShapeKind.Other, "System.TimeSpan", [])),
                ["Mode"] = P("Mode", new UiTypeShape(UiTypeShapeKind.Enum, "Probe.Weather", ["Cloudy", "Sunny"])),
                ["Widget"] = P("Widget", new UiTypeShape(UiTypeShapeKind.Other, "Probe.Widget", [])),
                ["Badge"] = P("Badge", new UiTypeShape(UiTypeShapeKind.Other, "Probe.BadgeTone", [])),
            },
            new Dictionary<string, UiEventDef>(StringComparer.Ordinal),
            new Dictionary<string, UiParameterDef>(StringComparer.Ordinal),
            new Dictionary<string, UiSlotDef>(StringComparer.Ordinal), []);
        return new UiCatalog(built.Elements.Append(probe), built.HiddenTags, built.Problems,
            built.AttachedProperties, literalParsers: literalParsers);
    }

    [Fact]
    public void FloatLiteral_Converts()
    {
        var catalog = CatalogWithProbe();
        var (doc, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<Probe Rate="8.5" />""");
        Assert.Empty(diags);
        Assert.Equal("8.5f", doc.Nodes.OfType<UiElement>().Single().Attributes[0].ConvertedLiteral);
    }

    [Fact]
    public void DecimalLiteral_Converts()
    {
        var catalog = CatalogWithProbe();
        var (_, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<Probe Blend="8.5" />""");
        Assert.Empty(diags); // 精确串断言放在单元级（见 ConvertDecimalSuite）
    }

    [Fact]
    public void CharLiteral_Converts()
    {
        var catalog = CatalogWithProbe();
        var (doc, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<Probe Letter="A" />""");
        Assert.Empty(diags);
        Assert.Equal("'A'", doc.Nodes.OfType<UiElement>().Single().Attributes[0].ConvertedLiteral);
    }

    [Fact]
    public void EnumLiteral_ConvertsToQualifiedMember()
    {
        var catalog = CatalogWithProbe();
        var (doc, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<Probe Mode="Sunny" />""");
        Assert.Empty(diags);
        Assert.Equal("global::Probe.Weather.Sunny",
            doc.Nodes.OfType<UiElement>().Single().Attributes[0].ConvertedLiteral);
    }

    [Fact]
    public void EnumLiteral_UnknownMember_IsFormatErrorWithMembers()
    {
        var catalog = CatalogWithProbe();
        var (_, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<Probe Mode="sunny" />""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.LiteralFormat, diag.Code);
        Assert.Contains("Probe.Weather", diag.Message);
        Assert.Contains("Sunny", diag.Message);
    }

    [Fact]
    public void TimeSpanLiteral_ConvertsToConstructorCall()
    {
        var catalog = CatalogWithProbe();
        var (doc, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<Probe Delay="1h" />""");
        Assert.Empty(diags);
        Assert.Equal("new global::System.TimeSpan(36000000000L)",
            doc.Nodes.OfType<UiElement>().Single().Attributes[0].ConvertedLiteral);
    }

    [Fact]
    public void TimeSpanLiteral_ClockForm_ConvertsToConstructorCall()
    {
        var catalog = CatalogWithProbe();
        var (doc, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<Probe Delay="1.02:03:04.5" />""");
        Assert.Empty(diags);
        Assert.Equal("new global::System.TimeSpan(937845000000L)",
            doc.Nodes.OfType<UiElement>().Single().Attributes[0].ConvertedLiteral);
    }

    [Fact]
    public void TimeSpanLiteral_MultiUnit_IsFormatError()
    {
        var catalog = CatalogWithProbe();
        var (_, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<Probe Delay="1h30m" />""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.LiteralFormat, diag.Code);
        Assert.Contains("single", diag.Message.ToLowerInvariant())
            ; // 消息给出单单位格式与替代建议
    }

    [Fact]
    public void LiteralWithoutConverter_IsNoConverterDiagnostic()
    {
        var catalog = CatalogWithProbe();
        var (_, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<Probe Widget="1" />""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.NoLiteralConverter, diag.Code);
        Assert.Contains("Probe.Widget", diag.Message);
        Assert.Contains("{expression}", diag.Message);
    }

    /// <summary>EUI002 未知属性仍然先于一切（保留语法只在名字匹配时拦）。</summary>
    [Fact]
    public void DuplicatePlainAttribute_IsDuplicateAttribute()
    {
        var catalog = CatalogWithProbe();
        var (_, diags) = ParseAnalyze(UiTestCatalogs.BuiltIn(), "@using EclipseUI.Elements\n" + """<StackPanel Padding="1" Padding="2" />""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.DuplicateAttribute, diag.Code);
        Assert.Contains("Padding", diag.Message);
    }

    [Fact]
    public void DuplicateBindVsPlain_SameTarget_IsSameTargetDuplicate()
    {
        // S2 语义保持：名称相同但任一侧是绑定 → EUI033（目标级），不是 EUI025。
        var (_, diags) = ParseAnalyze(UiTestCatalogs.BuiltIn(), "@using EclipseUI.Elements\n" + """<CheckBox Checked={x} @bind-Checked={y}>c</CheckBox>""");
        Assert.Contains(diags, d => d.Code == UiDiagnostics.SameTargetDuplicate);
    }

    [Fact]
    public void ClassAttribute_ParsesClassNames_WithoutCatalogContext()
    {
        // E2 转正（design/17 §4.8）：Class= 是合法属性。无目录语境（本测试直调 Analyzer、
        // styleClasses=null）宽松不校验类名——生成器语境传真实目录，未知类名报 EUI031
        // （见 EustyleTests 的管线用例）。这里只验证解析与回写。
        var (doc, diags) = ParseAnalyze(UiTestCatalogs.BuiltIn(), "@using EclipseUI.Elements\n" + """<StackPanel Class="elevated flat-card" />""");
        Assert.Empty(diags);
        var attr = doc.Nodes.OfType<UiElement>().Single().Attributes.Single();
        Assert.NotNull(attr.ClassNames);
        Assert.Equal(new[] { "elevated", "flat-card" }, attr.ClassNames);
    }

    [Fact]
    public void StyleAttribute_MalformedDeclarations_ReportEUI050()
    {
        // E2 转正：Style= 是合法属性入口，但值必须是声明序列（prop: value; ...）——
        // 旧保留语义的裸词 "flat" 现在按样式语法解析报 EUI050。
        var (_, diags) = ParseAnalyze(UiTestCatalogs.BuiltIn(), "@using EclipseUI.Elements\n" + """<StackPanel Style="flat" />""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.StyleSyntax, diag.Code);
        Assert.Contains("flat", diag.Message);
    }

    [Fact]
    public void AttachedProperty_IsResolvedSinceG4()
    {
        // G4 前 Grid.Row 报 EUI031（保留语法）；附加属性落地后已注册的 Owner.Prop 走正常解析，
        // 未知点号名改报 EUI002（见 G4AttachedPropertyTests）。
        var (doc, diags) = ParseAnalyze(UiTestCatalogs.BuiltIn(), "@using EclipseUI.Elements\n" + """<StackPanel Grid.Row="1" />""");
        Assert.Empty(diags);
        var attr = doc.Nodes.OfType<UiElement>().Single().Attributes.Single();
        Assert.NotNull(attr.AttachedDef);
        Assert.Equal("1", attr.ConvertedLiteral);
    }

    // ---- 直接单元级转换（不经过解析器） ----
    // C6 前 `<Slot>`/`<ContentSlot>` 走保留语法（EUI031，见 git history 的
    // ReservedSlotTags_AreReservedSyntax / SlotTagWithRealControl_KeepsTagResolution），
    // 现由解析器特判成槽节点、按 EUI022 校验（见 C6SlotTests）。

    [Fact]
    public void ConvertDoubleSuite()
    {
        Assert.Equal("8d", ConvertOk(UiTypeShapeKind.Primitive, "double", "8"));
        Assert.Equal("0.001d", ConvertOk(UiTypeShapeKind.Primitive, "double", "1e-3"));
        Assert.False(ConvertShape(UiTypeShapeKind.Primitive, "double", "eight", out var err));
        Assert.Contains("number", err);
    }

    [Fact]
    public void ConvertFloatSuite()
    {
        Assert.Equal("8f", ConvertOk(UiTypeShapeKind.Primitive, "float", "8"));
        Assert.Equal("1000f", ConvertOk(UiTypeShapeKind.Primitive, "float", "1e3"));
        Assert.False(ConvertShape(UiTypeShapeKind.Primitive, "float", "1e400", out var err));
        Assert.Contains("float", err);
    }

    [Fact]
    public void ConvertDecimalSuite()
    {
        Assert.Equal("8.5m", ConvertOk(UiTypeShapeKind.Primitive, "decimal", "8.5"));
        Assert.Equal("1000m", ConvertOk(UiTypeShapeKind.Primitive, "decimal", "1e3"));
    }

    [Fact]
    public void ConvertIntegerSuites()
    {
        Assert.Equal("42L", ConvertOk(UiTypeShapeKind.Primitive, "long", "42"));
        Assert.Equal("-7L", ConvertOk(UiTypeShapeKind.Primitive, "long", "-7"));
        Assert.Equal("(short)32000", ConvertOk(UiTypeShapeKind.Primitive, "short", "32000"));
        Assert.False(ConvertShape(UiTypeShapeKind.Primitive, "short", "70000", out var shortError));
        Assert.Contains("[-32768 .. 32767]", shortError);
        Assert.Equal("(byte)255", ConvertOk(UiTypeShapeKind.Primitive, "byte", "255"));
        Assert.False(ConvertShape(UiTypeShapeKind.Primitive, "byte", "256", out _));
        Assert.Equal("42", ConvertOk(UiTypeShapeKind.Primitive, "int", "42"));
    }

    [Fact]
    public void ConvertCharSuite()
    {
        Assert.Equal("'A'", ConvertOk(UiTypeShapeKind.Primitive, "char", "A"));
        Assert.Equal("'\\n'", ConvertOk(UiTypeShapeKind.Primitive, "char", "\\n"));
        Assert.Equal("'\\t'", ConvertOk(UiTypeShapeKind.Primitive, "char", "\\t"));
        Assert.Equal("'\\0'", ConvertOk(UiTypeShapeKind.Primitive, "char", "\\0"));
        Assert.Equal("'\\''", ConvertOk(UiTypeShapeKind.Primitive, "char", "\\'"));
        // \uXXXX 转义解析成真实字符；生成侧可打印字符（含非 ASCII）原样写入，控制字符才用 \uXXXX
        Assert.Equal("'\u263A'", ConvertOk(UiTypeShapeKind.Primitive, "char", "\\u263A"));
        // 字面量不 Trim：空格是合法字符
        Assert.Equal("' '", ConvertOk(UiTypeShapeKind.Primitive, "char", " "));
        Assert.False(ConvertShape(UiTypeShapeKind.Primitive, "char", "", out var emptyError));
        Assert.Contains("single character", emptyError);
    }

    [Fact]
    public void ConvertTimeSpanSuite()
    {
        Assert.Equal("new global::System.TimeSpan(2500000L)",
            ConvertOk(UiTypeShapeKind.Other, "System.TimeSpan", "250ms"));
        Assert.Equal("new global::System.TimeSpan(15000000L)",
            ConvertOk(UiTypeShapeKind.Other, "System.TimeSpan", "1.5s"));
        Assert.Equal("new global::System.TimeSpan(1200000000L)",
            ConvertOk(UiTypeShapeKind.Other, "System.TimeSpan", "2m"));
        Assert.Equal("new global::System.TimeSpan(864000000000L)",
            ConvertOk(UiTypeShapeKind.Other, "System.TimeSpan", "1d"));
        Assert.Equal("new global::System.TimeSpan(54000000000L)",
            ConvertOk(UiTypeShapeKind.Other, "System.TimeSpan", "1:30"));
        Assert.Equal("new global::System.TimeSpan(-2500000L)",
            ConvertOk(UiTypeShapeKind.Other, "System.TimeSpan", "-250ms"));
        Assert.False(ConvertShape(UiTypeShapeKind.Other, "System.TimeSpan", "1h30m", out var multi));
        Assert.Contains("single unit", multi);
        Assert.False(ConvertShape(UiTypeShapeKind.Other, "System.TimeSpan", "abc", out var bad));
        Assert.NotNull(bad);
    }

    [Fact]
    public void HasConverter_Discriminates()
    {
        var shape = new UiTypeShape(UiTypeShapeKind.Other, "Probe.Widget", []);
        Assert.False(UiLiterals.HasConverter(shape));
        Assert.True(UiLiterals.HasConverter(
            new UiTypeShape(UiTypeShapeKind.Enum, "Probe.Weather", ["A"])));
        Assert.False(UiLiterals.HasConverter(
            new UiTypeShape(UiTypeShapeKind.Signal, "Probe.NotALiteral", [])));
    }

    private static string ConvertOk(UiTypeShapeKind kind, string key, string literal)
    {
        var shape = new UiTypeShape(kind, key, kind == UiTypeShapeKind.Enum ? ["Cloudy", "Sunny"] : []);
        Assert.True(UiLiterals.TryConvert(shape, literal, out var converted, out var error),
            $"{literal} should convert ({key}): {error}");
        return converted!;
    }

    private static bool ConvertShape(UiTypeShapeKind kind, string key, string literal, out string? error)
    {
        var shape = new UiTypeShape(kind, key, kind == UiTypeShapeKind.Enum ? ["Cloudy", "Sunny"] : []);
        var ok = UiLiterals.TryConvert(shape, literal, out _, out error);
        return ok;
    }

    // ---- 声明式字面量解析器（design/14 §14.7.3，2026-11-12）----

    private static readonly UiLiteralParserDef BadgeParser = new(
        "Probe.BadgeTone", "global::Probe.BadgeTone.Parse",
        @"^(Neutral|Success|Warning|Danger)$", "one of: Neutral, Success, Warning, Danger",
        ["Neutral", "Success", "Warning", "Danger"]);

    [Fact]
    public void ParserLiteral_ConvertsToParseCall()
    {
        var catalog = CatalogWithProbe([BadgeParser]);
        var (doc, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<Probe Badge="Warning" />""");
        Assert.Empty(diags);
        Assert.Equal("global::Probe.BadgeTone.Parse(\"Warning\")",
            doc.Nodes.OfType<UiElement>().Single().Attributes[0].ConvertedLiteral);
    }

    [Fact]
    public void ParserLiteral_PatternMismatch_IsFormatErrorWithHint()
    {
        var catalog = CatalogWithProbe([BadgeParser]);
        var (_, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<Probe Badge="warn" />""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.LiteralFormat, diag.Code);
        Assert.Contains("Neutral", diag.Message);          // Hint 进 EUI003
    }

    [Fact]
    public void ParserLiteral_WithoutRegisteredParser_IsNoLiteralConverter()
    {
        var catalog = CatalogWithProbe();                  // 不带解析器表
        var (_, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<Probe Badge="Warning" />""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.NoLiteralConverter, diag.Code);
        Assert.Contains("write an {expression} instead", diag.Message);
    }

    [Fact]
    public void ParserLiteral_WithoutPattern_AcceptsAnyLiteral()
    {
        var loose = new UiLiteralParserDef("Probe.BadgeTone", "global::Probe.BadgeTone.Parse",
            null, null, []);
        var catalog = CatalogWithProbe([loose]);
        var (doc, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<Probe Badge="anything goes" />""");
        Assert.Empty(diags);
        Assert.Equal("global::Probe.BadgeTone.Parse(\"anything goes\")",
            doc.Nodes.OfType<UiElement>().Single().Attributes[0].ConvertedLiteral);
    }

    [Fact]
    public void ParserLiteral_BuiltinConverterTakesPrecedence()
    {
        // 即使目录错误地给内建类型注册了解析器，内建 int 转换仍然优先（TryConvert 顺序保证）。
        var impostor = new UiLiteralParserDef("int", "global::Probe.Never", null, null, []);
        var catalog = CatalogWithProbe([impostor]);
        var (doc, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<Probe Age="42" />""");
        Assert.Empty(diags);
        Assert.Equal("(short)42", doc.Nodes.OfType<UiElement>().Single().Attributes[0].ConvertedLiteral);
    }

    [Fact]
    public void LiteralParserDef_HasValueEqualityForIncrementalCache()
    {
        // 数组按引用比较会让 §14.10.3 的增量缓存永远判定"已变化"，所以手写结构相等。
        var a = new UiCatalog(Array.Empty<UiElementDef>(), Array.Empty<UiHiddenTag>(),
            Array.Empty<UiMetadataProblem>(), literalParsers: [BadgeParser]);
        var b = new UiCatalog(Array.Empty<UiElementDef>(), Array.Empty<UiHiddenTag>(),
            Array.Empty<UiMetadataProblem>(), literalParsers:
            [
                new UiLiteralParserDef("Probe.BadgeTone", "global::Probe.BadgeTone.Parse",
                    @"^(Neutral|Success|Warning|Danger)$", "one of: Neutral, Success, Warning, Danger",
                    ["Neutral", "Success", "Warning", "Danger"]),
            ]);
        Assert.Equal(a, b);
        var different = new UiCatalog(Array.Empty<UiElementDef>(), Array.Empty<UiHiddenTag>(),
            Array.Empty<UiMetadataProblem>(), literalParsers: [BadgeParser with { Candidates = ["Neutral"] }]);
        Assert.NotEqual(a, different);
    }
}
