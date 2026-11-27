using System;
using System.Collections.Generic;
using System.Linq;
using BubuUI;
using BubuUI.Compiler;
using BubuUI.Elements;
using BubuUI.TestKit;
using Xunit;

namespace BubuUI.Headless.Tests;

/// <summary>
/// G4 附加属性切片（design/14 §14.5.5）：目录表面（Grid.Row/Grid.Column）、使用点解析、
/// 字面量转换与未知诊断（EUI002 建议口径），以及模板 → 运行时弱表值的端到端。
/// 保留语法 EUI031 已对附加属性解禁——已注册的走正常解析，未注册的点号名按未知处理。
/// </summary>
public class G4AttachedPropertyTests
{
    private static (UiDocument Doc, List<UiDiagnostic> Diags) ParseAnalyze(UiCatalog catalog, string text)
    {
        var diags = new List<UiDiagnostic>();
        var doc = FrontEnd.Parse(text, "Test.bui", diags);
        FrontEnd.Analyze(doc, catalog, diags);
        return (doc, diags);
    }

    private static UiElement SingleElement(UiDocument doc) => doc.Nodes.OfType<UiElement>().Single();

    // ---- 使用点解析 ----

    [Fact]
    public void AttachedLiteral_IsResolvedAndConverted()
    {
        var (doc, diags) = ParseAnalyze(UiTestCatalogs.BuiltIn(),
            "@using BubuUI.Elements\n<Button Grid.Row=\"3\" />");
        Assert.Empty(diags);
        var attr = SingleElement(doc).Attributes.Single();
        var attached = attr.AttachedDef;
        Assert.NotNull(attached);
        Assert.Equal("Grid", attached!.OwnerTag);
        Assert.Equal("Row", attached.Name);
        Assert.Equal("SetRow", attached.SetterMethod);
        Assert.Equal("3", attr.ConvertedLiteral);
    }

    [Fact]
    public void AttachedExpression_IsResolvedWithoutConversion()
    {
        var (doc, diags) = ParseAnalyze(UiTestCatalogs.BuiltIn(),
            "@using BubuUI.Elements\n<Button Grid.Row={row} />");
        Assert.Empty(diags);
        var attr = SingleElement(doc).Attributes.Single();
        Assert.NotNull(attr.AttachedDef);
        Assert.Null(attr.ConvertedLiteral);
    }

    [Fact]
    public void AttachedNegativeLiteral_Converts()
    {
        var (doc, diags) = ParseAnalyze(UiTestCatalogs.BuiltIn(),
            "@using BubuUI.Elements\n<Button Grid.Row=\"-2\" />");
        Assert.Empty(diags);
        Assert.Equal("-2", SingleElement(doc).Attributes.Single().ConvertedLiteral);
    }

    [Fact]
    public void AttachedLiteral_FormatError_IsEUI003()
    {
        var (_, diags) = ParseAnalyze(UiTestCatalogs.BuiltIn(),
            "@using BubuUI.Elements\n<Button Grid.Row=\"x\" />");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.LiteralFormat, diag.Code);
        Assert.Contains("attached property 'Grid.Row'", diag.Message);
    }

    [Fact]
    public void UnknownAttachedProperty_IsEUI002_WithNearestHint()
    {
        var (_, diags) = ParseAnalyze(UiTestCatalogs.BuiltIn(),
            "@using BubuUI.Elements\n<Button Grid.Ro=\"1\" />");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.UnknownProperty, diag.Code);
        Assert.Contains("unknown attached property 'Grid.Ro'", diag.Message);
        Assert.Contains("did you mean 'Grid.Row'?", diag.Message);
    }

    [Fact]
    public void UnknownOwner_IsEUI002_WithoutHint()
    {
        var (_, diags) = ParseAnalyze(UiTestCatalogs.BuiltIn(),
            "@using BubuUI.Elements\n<Button MissingHost.Row=\"1\" />");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.UnknownProperty, diag.Code);
        Assert.Contains("unknown attached property 'MissingHost.Row'", diag.Message);
        Assert.DoesNotContain("did you mean", diag.Message);
    }

    [Fact]
    public void ExtraDot_IsUnknownAttachedProperty()
    {
        var (_, diags) = ParseAnalyze(UiTestCatalogs.BuiltIn(),
            "@using BubuUI.Elements\n<Button Grid.Row.Extra=\"1\" />");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.UnknownProperty, diag.Code);
        Assert.Contains("unknown attached property 'Grid.Row.Extra'", diag.Message);
    }

    [Fact]
    public void ComponentTag_CarriesAttachedProperty()
    {
        // 组件标签也是 Element：附加属性同样解析（design/14 §14.5.3 组件分支）。
        var built = UiTestCatalogs.BuiltIn();
        var card = new UiElementDef("Card", "global::Probe.Card", UiContentModel.None, null,
            new Dictionary<string, UiPropDef>(StringComparer.Ordinal),
            new Dictionary<string, UiEventDef>(StringComparer.Ordinal),
            new Dictionary<string, UiParameterDef>(StringComparer.Ordinal),
            new Dictionary<string, UiSlotDef>(StringComparer.Ordinal),
            Array.Empty<UiMetadataProblem>());
        var catalog = new UiCatalog(built.Elements.Append(card), built.HiddenTags, built.Problems,
            built.AttachedProperties);
        var (doc, diags) = ParseAnalyze(catalog, "@using BubuUI.Elements\n@using Probe\n<Card Grid.Row=\"1\" />");
        Assert.Empty(diags);
        Assert.NotNull(SingleElement(doc).Attributes.Single().AttachedDef);
    }

    // ---- 运行时存储（弱表 + 默认值回落）----

    [Fact]
    public void AttachedStore_FallsBackToDefault_AfterClear()
    {
        var el = new Button();
        Assert.Equal(0, Grid.GetRow(el));
        Grid.SetRow(el, 5);
        Assert.Equal(5, Grid.GetRow(el));
        Grid.SetRow(el, 7);                    // 复用值盒，不重新分配
        Assert.Equal(7, Grid.GetRow(el));
        Grid.RowProperty.ClearValue(el);
        Assert.Equal(0, Grid.GetRow(el));
        Assert.Equal(0, Grid.ColumnProperty.GetValue(el));
    }

    // ---- 端到端：模板 → 生成代码 → 运行时弱表 ----

    [Fact]
    public void TemplateWritesAttachedValues_RuntimeReadsThemBack()
    {
        var probe = new G4AttachedProbe();
        var root = new HeadlessRoot(probe);
        root.UpdateLayout();

        var button = probe.Descendants<Button>().Single();
        var checkbox = probe.Descendants<CheckBox>().Single();

        Assert.Equal(1, Grid.GetRow(button));      // 字面量
        Assert.Equal(2, Grid.GetColumn(button));   // 表达式 {extraCol}
        Assert.Equal(2, Grid.GetRow(checkbox));    // 另一元素独立取值
        Assert.Equal(0, Grid.GetColumn(checkbox)); // 未写 → 默认值
    }

    [Fact]
    public void TemplateGridLayout_FlowsThroughFullPipeline()
    {
        // G4 布局切片：RowDefinitions 字符串属性 + Grid.Row/Column 附加属性
        // 经完整管线（模板 → 生成代码 → 运行时布局）落到最终矩形。
        var probe = new G4AttachedProbe();
        var root = new HeadlessRoot(probe) { AvailableSize = new Size(1280, 800) };
        root.UpdateLayout();

        var cell = probe.Descendants<TextBlock>().Single();
        // 列 "*,2*"：内宽 1280 按 1:2 → 1280/3 与 1280×2/3
        Assert.Equal(1280d / 3, cell.FinalRect.X, 3);
        Assert.Equal(1280d * 2 / 3, cell.FinalRect.Width, 3);
        Assert.Equal(60, cell.FinalRect.Y, 5);                           // 固定首行 60
        Assert.Equal(cell.DesiredSize.Height, cell.FinalRect.Height, 5); // Auto 行 = 文本期望高
    }
}
