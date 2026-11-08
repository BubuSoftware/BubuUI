using System;
using System.Collections.Generic;
using System.Linq;
using EclipseUI.Compiler;
using EclipseUI.TestKit;
using Xunit;

namespace EclipseUI.Headless.Tests;

/// <summary>
/// C6 切片：具名插槽——消费侧 <c>&lt;Slot Name="..">&lt;/Slot></c>、宿主侧 <c>&lt;ContentSlot Name=".."/></c>。
/// 错误统一走 EUI022 家族（spec/01 §6.2 / §4.15；spec/02 §4.12.5；design/14 §14.6.3）。
/// 保留标签 <c>&lt;Slot&gt;</c>/<c>&lt;ContentSlot&gt;</c> 由解析器特判成槽节点（不再依赖
/// 「目录无同名控件」的 C3 口径），所以本文件不覆盖与真实控件的共存——那是声明期保留冲突。
/// </summary>
public class C6SlotTests
{
    private static (UiDocument Doc, List<UiDiagnostic> Diags, UiCatalog Catalog) ParseAnalyze(UiCatalog catalog, string text)
    {
        var diags = new List<UiDiagnostic>();
        var doc = FrontEnd.Parse(text, "Test.ui", diags);
        FrontEnd.Analyze(doc, catalog, diags);
        return (doc, diags, catalog);
    }

    private static UiParameterDef Param(string name, string type, bool required = false) => new(
        name, type, new UiTypeShape(UiTypeShapeKind.Other, type, []),
        CanWrite: true, Required: required, IsDelegate: false, DelegateArity: 0,
        IsContent: false, ContentIsMultiple: false, ChangedCallbackName: null);

    private static UiParameterDef Content(string name, string type, bool multiple) => new(
        name, type, new UiTypeShape(UiTypeShapeKind.Other, type, []),
        CanWrite: true, Required: false, IsDelegate: false, DelegateArity: 0,
        IsContent: true, ContentIsMultiple: multiple, ChangedCallbackName: null);

    private static UiSlotDef Slot(string name, string prop, string type, UiSlotCardinality cardinality) => new(
        name, prop, type, cardinality);

    private const string ElementOptT = "global::EclipseUI.Element?";
    private const string ElementListT = "global::System.Collections.Generic.IReadOnlyList<global::EclipseUI.Element>";

    /// <summary>
    /// 合成表面：ProbeCard（Title 参数 + Header 单根槽 + Items 多根槽 + Body 内容参数）、
    /// ProbeList（多根内容参数）、SelfHost —— <c>global::Probe.Test</c>（doc.ClassName = "Test"，
    /// ContentSlot 的自身 def 匹配目标）。
    /// </summary>
    private static UiCatalog CatalogWithComponent()
    {
        var built = UiTestCatalogs.BuiltIn();
        var card = new UiElementDef(
            "ProbeCard", "global::Probe.Card", UiElementOrigin.Component,
            UiContentModel.None, null,
            new Dictionary<string, UiPropDef>(StringComparer.Ordinal),
            new Dictionary<string, UiEventDef>(StringComparer.Ordinal),
            new Dictionary<string, UiParameterDef>(StringComparer.Ordinal)
            {
                ["Title"] = new UiParameterDef("Title", "global::System.String?",
                    new UiTypeShape(UiTypeShapeKind.Primitive, "string", []),
                    CanWrite: true, Required: false, IsDelegate: false, DelegateArity: 0,
                    IsContent: false, ContentIsMultiple: false, ChangedCallbackName: null),
                ["Body"] = Content("Body", ElementOptT, multiple: false),
            },
            new Dictionary<string, UiSlotDef>(StringComparer.Ordinal)
            {
                ["Header"] = Slot("Header", "Header", ElementOptT, UiSlotCardinality.Single),
                ["Rows"] = Slot("Rows", "Rows", ElementListT, UiSlotCardinality.Multiple),
            }, []);
        var self = new UiElementDef(
            "ProbeSelfHost", "global::Probe.Test", UiElementOrigin.Component,
            UiContentModel.None, null,
            new Dictionary<string, UiPropDef>(StringComparer.Ordinal),
            new Dictionary<string, UiEventDef>(StringComparer.Ordinal),
            new Dictionary<string, UiParameterDef>(StringComparer.Ordinal),
            new Dictionary<string, UiSlotDef>(StringComparer.Ordinal)
            {
                ["Header"] = Slot("Header", "Header", ElementOptT, UiSlotCardinality.Single),
                ["Rows"] = Slot("Rows", "Rows", ElementListT, UiSlotCardinality.Multiple),
            }, []);
        return new UiCatalog(
            built.Elements.Append(card).Append(self).ToArray(), built.HiddenTags, built.Problems);
    }

    private static UiCatalog CatalogWithoutSelf()
    {
        // catalogWithoutSelf：移除与 doc.ClassName 短名匹配的 def——ContentSlot 无法解析自身插槽。
        var built = UiTestCatalogs.BuiltIn();
        var orphan = new UiElementDef(
            "ProbeCard", "global::Probe.Card", UiElementOrigin.Component,
            UiContentModel.None, null,
            new Dictionary<string, UiPropDef>(StringComparer.Ordinal),
            new Dictionary<string, UiEventDef>(StringComparer.Ordinal),
            new Dictionary<string, UiParameterDef>(StringComparer.Ordinal)
            {
                ["Title"] = Param("Title", ElementOptT),
            },
            new Dictionary<string, UiSlotDef>(StringComparer.Ordinal)
            {
                ["Header"] = Slot("Header", "Header", ElementOptT, UiSlotCardinality.Single),
            }, []);
        return new UiCatalog(built.Elements.Append(orphan).ToArray(), built.HiddenTags, built.Problems);
    }

    // ---- 消费侧：解析与发射解析 ----

    [Fact]
    public void SlotFill_SingleRoot_SucceedsAndResolvesDef()
    {
        var (doc, diags, _) = ParseAnalyze(CatalogWithComponent(), """
            @using Probe
            @using EclipseUI.Elements
            <ProbeCard Title="Hi">
              <Slot Name="Header"><TextBlock FontSize="12">a</TextBlock></Slot>
            </ProbeCard>
            """);
        Assert.Empty(diags);
        var card = doc.Nodes.OfType<UiElement>().Single();
        var fill = card.Children.OfType<UiSlotNode>().Single();
        Assert.NotNull(fill.ResolvedSlot);
        Assert.Equal("Header", fill.ResolvedSlot!.Name);
        Assert.Equal("Header", fill.ResolvedSlot.PropertyName);
        Assert.Equal(UiSlotCardinality.Single, fill.ResolvedSlot.Cardinality);
    }

    [Fact]
    public void SlotFill_WithDefaultContent_BothResolve()
    {
        // 未包 <Slot> 的子内容进默认内容（spec/01 §4.14 语义不变）；槽填充独立处理。
        var (doc, diags, _) = ParseAnalyze(CatalogWithComponent(), """
            @using Probe
            @using EclipseUI.Elements
            <ProbeCard Title="Hi">
              <Button />
              <Slot Name="Rows"><TextBlock FontSize="12">r1</TextBlock></Slot>
            </ProbeCard>
            """);
        Assert.Empty(diags);
        var card = doc.Nodes.OfType<UiElement>().Single();
        Assert.Single(card.Children.OfType<UiSlotNode>());
        var defaultChild = card.Children.Single(c => c is UiElement);
        Assert.IsType<UiElement>(defaultChild);
    }

    [Fact]
    public void SlotFill_MissingName_IsEUI022()
    {
        var (_, diags, _) = ParseAnalyze(CatalogWithComponent(), """
            @using Probe
            @using EclipseUI.Elements
            <ProbeCard Title="Hi"><Slot><TextBlock /></Slot></ProbeCard>
            """);
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.SlotInvalid, diag.Code);
        Assert.Contains("Name", diag.Message);
    }

    [Fact]
    public void SlotFill_ExpressionName_IsEUI022()
    {
        var (_, diags, _) = ParseAnalyze(CatalogWithComponent(), """
            @using Probe
            @using EclipseUI.Elements
            <ProbeCard Title="Hi"><Slot Name={slotName}><TextBlock /></Slot></ProbeCard>
            """);
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.SlotInvalid, diag.Code);
        Assert.Contains("literal", diag.Message);
    }

    [Fact]
    public void SlotFill_UnexpectedAttribute_IsEUI022()
    {
        var (_, diags, _) = ParseAnalyze(CatalogWithComponent(), """
            @using Probe
            @using EclipseUI.Elements
            <ProbeCard Title="Hi"><Slot Name="Header" Gap="4"><TextBlock /></Slot></ProbeCard>
            """);
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.SlotInvalid, diag.Code);
        Assert.Contains("Gap", diag.Message);
    }

    [Fact]
    public void SlotFill_UnknownName_GetsSuggestion()
    {
        var (_, diags, _) = ParseAnalyze(CatalogWithComponent(), """
            @using Probe
            @using EclipseUI.Elements
            <ProbeCard Title="Hi"><Slot Name="Heade"><TextBlock /></Slot></ProbeCard>
            """);
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.SlotInvalid, diag.Code);
        Assert.Contains("unknown slot 'Heade'", diag.Message);
        Assert.Contains("Header", diag.Message);
    }

    [Fact]
    public void SlotFill_SingleSlotMultipleRoots_IsEUI022_ButStillResolvesFirstRoot()
    {
        var (doc, diags, _) = ParseAnalyze(CatalogWithComponent(), """
            @using Probe
            @using EclipseUI.Elements
            <ProbeCard Title="Hi">
              <Slot Name="Header"><TextBlock /><Button /></Slot>
            </ProbeCard>
            """);
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.SlotInvalid, diag.Code);
        Assert.Contains("single root", diag.Message);
        // 生成口径与 §4.12.3 单根内容一致：错误已报，解析保留首个根
        var fill = doc.Nodes.OfType<UiElement>().Single().Children.OfType<UiSlotNode>().Single();
        Assert.NotNull(fill.ResolvedSlot);
    }

    [Fact]
    public void SlotFill_DuplicateSingleFill_IsEUI022()
    {
        var (_, diags, _) = ParseAnalyze(CatalogWithComponent(), """
            @using Probe
            @using EclipseUI.Elements
            <ProbeCard Title="Hi">
              <Slot Name="Header"><TextBlock /></Slot>
              <Slot Name="Header"><Button /></Slot>
            </ProbeCard>
            """);
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.SlotInvalid, diag.Code);
        Assert.Contains("only once", diag.Message);
    }

    [Fact]
    public void SlotFill_MultipleSlot_AcceptsSeveralFills_AsConcatenation()
    {
        var (doc, diags, _) = ParseAnalyze(CatalogWithComponent(), """
            @using Probe
            @using EclipseUI.Elements
            <ProbeCard Title="Hi">
              <Slot Name="Rows"><TextBlock /></Slot>
              <Slot Name="Rows"><Button /></Slot>
            </ProbeCard>
            """);
        Assert.Empty(diags);
        var fills = doc.Nodes.OfType<UiElement>().Single().Children.OfType<UiSlotNode>().ToList();
        Assert.All(fills, f => Assert.NotNull(f.ResolvedSlot));
    }

    // ---- 消费侧：位置 ----

    [Fact]
    public void SlotFill_InsideControl_IsEUI022()
    {
        var (_, diags, _) = ParseAnalyze(CatalogWithComponent(), """
            @using EclipseUI.Elements
            <StackPanel><Slot Name="Header" /></StackPanel>
            """);
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.SlotInvalid, diag.Code);
        Assert.Contains("direct children of a component tag", diag.Message);
    }

    [Fact]
    public void SlotFill_InsideBlock_IsEUI022()
    {
        var (_, diags, _) = ParseAnalyze(CatalogWithComponent(), """
            @using Probe
            @using EclipseUI.Elements
            <ProbeCard Title="Hi">@if (true) { <Slot Name="Header" /> }</ProbeCard>
            """);
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.SlotInvalid, diag.Code);
    }

    [Fact]
    public void SlotFill_AtDocumentRoot_IsEUI022()
    {
        var (_, diags, _) = ParseAnalyze(CatalogWithComponent(), "@using EclipseUI.Elements\n" + """<Slot Name="Header" />""");
        Assert.Contains(diags, d => d.Code == UiDiagnostics.SlotInvalid &&
            d.Message.Contains("direct children of a component tag"));
    }

    // ---- 消费侧：内容上下文继承（EUI040/@bind、EUI010/Key） ----

    [Fact]
    public void SlotFill_BindInsideFill_IsEUI040()
    {
        var (_, diags, _) = ParseAnalyze(CatalogWithComponent(), """
            @using Probe
            @using EclipseUI.Elements
            <ProbeCard Title="Hi"><Slot Name="Rows"><CheckBox @bind-Checked={flag} /></Slot></ProbeCard>
            """);
        Assert.Contains(diags, d => d.Code == UiDiagnostics.BindTargetInvalid);
    }

    [Fact]
    public void SlotFill_KeyInsideFill_IsEUI010()
    {
        var (_, diags, _) = ParseAnalyze(CatalogWithComponent(), """
            @using Probe
            @using EclipseUI.Elements
            <ProbeCard Title="Hi"><Slot Name="Rows"><TextBlock Key={k} /></Slot></ProbeCard>
            """);
        Assert.Contains(diags, d => d.Code == UiDiagnostics.KeyMisuse);
    }

    // ---- 宿主侧：自身模板中的 <ContentSlot> ----

    [Fact]
    public void ContentSlot_SelfTemplate_SingleSlot_Resolves()
    {
        var (doc, diags, _) = ParseAnalyze(CatalogWithComponent(), """
            @using EclipseUI.Elements
            <StackPanel><ContentSlot Name="Header" /></StackPanel>
            """);
        Assert.Empty(diags);
        var host = doc.Nodes.OfType<UiElement>().Single().Children.OfType<UiContentSlotNode>().Single();
        Assert.Equal("Header", host.ResolvedSlot!.Name);
        Assert.Equal(UiSlotCardinality.Single, host.ResolvedSlot.Cardinality);
    }

    [Fact]
    public void ContentSlot_SelfTemplate_MultipleSlot_Resolves()
    {
        var (doc, diags, _) = ParseAnalyze(CatalogWithComponent(), """
            @using EclipseUI.Elements
            <StackPanel><ContentSlot Name="Rows" /></StackPanel>
            """);
        Assert.Empty(diags);
        var host = doc.Nodes.OfType<UiElement>().Single().Children.OfType<UiContentSlotNode>().Single();
        Assert.Equal(UiSlotCardinality.Multiple, host.ResolvedSlot!.Cardinality);
    }

    [Fact]
    public void ContentSlot_UnknownName_IsEUI022()
    {
        var (_, diags, _) = ParseAnalyze(CatalogWithComponent(), "@using EclipseUI.Elements\n" + """<StackPanel><ContentSlot Name="Foo" /></StackPanel>""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.SlotInvalid, diag.Code);
        Assert.Contains("unknown slot 'Foo'", diag.Message);
        Assert.Contains("global::Probe.Test", diag.Message);
    }

    [Fact]
    public void ContentSlot_MissingName_IsEUI022()
    {
        var (_, diags, _) = ParseAnalyze(CatalogWithComponent(), "@using EclipseUI.Elements\n" + """<StackPanel><ContentSlot /></StackPanel>""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.SlotInvalid, diag.Code);
        Assert.Contains("Name", diag.Message);
    }

    [Fact]
    public void ContentSlot_UnexpectedAttribute_IsEUI022()
    {
        var (_, diags, _) = ParseAnalyze(CatalogWithComponent(), "@using EclipseUI.Elements\n" + """<StackPanel><ContentSlot Name="Header" Gap="4" /></StackPanel>""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.SlotInvalid, diag.Code);
        Assert.Contains("Gap", diag.Message);
    }

    [Fact]
    public void ContentSlot_WithChildren_IsEUI022()
    {
        var (_, diags, _) = ParseAnalyze(CatalogWithComponent(),
            "@using EclipseUI.Elements\n" + """<StackPanel><ContentSlot Name="Header"><TextBlock /></ContentSlot></StackPanel>""");
        Assert.Contains(diags, d => d.Code == UiDiagnostics.SlotInvalid &&
            d.Message.Contains("placement marker"));
    }

    [Fact]
    public void ContentSlot_InsideBlock_IsEUI022()
    {
        var (_, diags, _) = ParseAnalyze(CatalogWithComponent(),
            "@using EclipseUI.Elements\n" + """<StackPanel>@if (true) { <ContentSlot Name="Header" /> }</StackPanel>""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.SlotInvalid, diag.Code);
        Assert.Contains("@if/@foreach blocks", diag.Message);
    }

    [Fact]
    public void ContentSlot_AtDocumentRoot_IsEUI022()
    {
        var (_, diags, _) = ParseAnalyze(CatalogWithComponent(), "@using EclipseUI.Elements\n" + """<ContentSlot Name="Header" />""");
        Assert.Contains(diags, d => d.Code == UiDiagnostics.SlotInvalid &&
            d.Message.Contains("template root"));
    }

    [Fact]
    public void ContentSlot_DuplicatePlacement_IsEUI022()
    {
        var (_, diags, _) = ParseAnalyze(CatalogWithComponent(), """
            @using EclipseUI.Elements
            <StackPanel>
              <ContentSlot Name="Header" />
              <ContentSlot Name="Header" />
            </StackPanel>
            """);
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.SlotInvalid, diag.Code);
        Assert.Contains("placed more than once", diag.Message);
    }

    [Fact]
    public void ContentSlot_InsideComponentUse_IsEUI022()
    {
        // <ContentSlot> 属于组件自身模板，不能出现在使用其他组件的填充里
        var (_, diags, _) = ParseAnalyze(CatalogWithComponent(),
            "@using Probe\n" + """<ProbeCard Title="Hi"><ContentSlot Name="Header" /></ProbeCard>""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.SlotInvalid, diag.Code);
        Assert.Contains("own template", diag.Message);
    }

    [Fact]
    public void ContentSlot_OwnDefMissing_IsEUI022()
    {
        var (_, diags, _) = ParseAnalyze(CatalogWithoutSelf(), "@using EclipseUI.Elements\n" + """<StackPanel><ContentSlot Name="Header" /></StackPanel>""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.SlotInvalid, diag.Code);
        Assert.Contains("cannot resolve the slots", diag.Message);
    }

    [Fact]
    public void ContentSlot_InsideTextModel_IsEUI022()
    {
        var (_, diags, _) = ParseAnalyze(CatalogWithComponent(), "@using EclipseUI.Elements\n" + """<TextBlock><ContentSlot Name="Header" /></TextBlock>""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.SlotInvalid, diag.Code);
        Assert.Contains("element container", diag.Message);
    }
}
