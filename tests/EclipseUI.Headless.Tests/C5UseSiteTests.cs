using System;
using System.Collections.Generic;
using System.Linq;
using EclipseUI.Compiler;
using EclipseUI.TestKit;
using Xunit;

namespace EclipseUI.Headless.Tests;

/// <summary>
/// C5 切片：Ref（EUI029 语法级）与 EUI019/EUI020/EUI021 使用点诊断。
/// 规范条文：spec/01 §4.3/§4.15/§6.2；design/14 §14.5.4/§14.9.1——引用程序集控件按使用点
/// 暴露元数据问题（消息带完整类型名）；源项目控件用点仍落 EUI002（声明处归 EUI026）。
/// 目录从 BuiltIn() 派生，追加合成的带问题 def（<c>IsFromReferencedAssembly</c> 模拟两种来源）。
/// 语义级 EUI029（字段缺失/类型不符）由生成器探针校验，属 Compiler.Tests 范围。
/// </summary>
public class C5UseSiteTests
{
    private static (UiDocument Doc, List<UiDiagnostic> Diags) ParseAnalyze(UiCatalog catalog, string text)
    {
        var diags = new List<UiDiagnostic>();
        var doc = FrontEnd.Parse(text, "Test.ui", diags);
        FrontEnd.Analyze(doc, catalog, diags);
        return (doc, diags);
    }

    /// <summary>合成 def：无可用成员，Problems 即全部表面（属性/事件/参数只经 Problems 触达）。</summary>
    private static UiElementDef Def(string tag, string clrName,
        bool referenced, params UiMetadataProblem[] problems) => new(
        tag, clrName, UiContentModel.None, null,
        new Dictionary<string, UiPropDef>(StringComparer.Ordinal),
        new Dictionary<string, UiEventDef>(StringComparer.Ordinal),
        new Dictionary<string, UiParameterDef>(StringComparer.Ordinal),
        new Dictionary<string, UiSlotDef>(StringComparer.Ordinal),
        problems, referenced);

    /// <summary>目录：BuiltIn() + 引用程序集来源的 Broken/FaultyCard/NoSourcePanel（模拟跨程序集）。</summary>
    private static UiCatalog CatalogWithReferencedDefs()
    {
        var built = UiTestCatalogs.BuiltIn();
        var broken = Def("Broken", "global::Probe.Broken", referenced: true,
            gapProblem, clickProblem);
        var faultyCard = Def("FaultyCard", "global::Probe.FaultyCard", referenced: true, contentProblem);
        var merged = built.Elements.Append(broken).Append(faultyCard);
        return new UiCatalog(merged, built.HiddenTags, built.Problems, built.AttachedProperties);
    }

    /// <summary>同样的问题清单，但 def 来自源项目（声明处归 EUI026，用点不得触发 EUI019/020/021）。</summary>
    private static UiCatalog CatalogWithSourceDefs()
    {
        var built = UiTestCatalogs.BuiltIn();
        var broken = Def("Broken", "global::Probe.Broken", referenced: false,
            gapProblem, clickProblem);
        var faultyCard = Def("FaultyCard", "global::Probe.FaultyCard", referenced: false, contentProblem);
        var merged = built.Elements.Append(broken).Append(faultyCard);
        return new UiCatalog(merged, built.HiddenTags, built.Problems, built.AttachedProperties);
    }

    private static readonly UiMetadataProblem gapProblem = new(
        UiDiagnostics.PropertyNotBindable,
        "'GapProperty' must be a public static readonly StyledProperty field",
        "global::Probe.Broken", "Gap");

    private static readonly UiMetadataProblem clickProblem = new(
        UiDiagnostics.EventFieldMissing,
        "'ClickEvent' ends with 'Event' but its type is EclipseUI.StyledProperty<EclipseUI.System.Int32>, not EclipseUI.RoutedEvent",
        "global::Probe.Broken", "ClickEvent");

    private static readonly UiMetadataProblem contentProblem = new(
        UiDiagnostics.ContentTargetInvalid,
        "content parameter 'Body' has type global::System.String; [UiContent] parameters must be " +
        "Element? (single root) or IReadOnlyList<Element> (multiple roots)",
        "global::Probe.FaultyCard", "Body");

    // ---- Ref：语法级（spec/01 §4.3 表："简单标识符、字段可写"；§4.15 保留名）----

    [Fact]
    public void Ref_SimpleIdentifier_IsAccepted()
    {
        var (_, diags) = ParseAnalyze(UiTestCatalogs.BuiltIn(), "@using EclipseUI.Elements\n" + """<StackPanel Ref={savePanel} />""");
        Assert.Empty(diags);
    }

    [Fact]
    public void Ref_SimpleIdentifier_SetsRefTargetOnAttribute()
    {
        var (doc, _) = ParseAnalyze(UiTestCatalogs.BuiltIn(), "@using EclipseUI.Elements\n" + """<StackPanel Ref={savePanel} />""");
        var attr = doc.Nodes.OfType<UiElement>().Single().Attributes.Single();
        Assert.Equal("savePanel", attr.RefTarget);
    }

    [Fact]
    public void Ref_QuotedLiteral_IsEUI029()
    {
        var (_, diags) = ParseAnalyze(UiTestCatalogs.BuiltIn(), "@using EclipseUI.Elements\n" + """<StackPanel Ref="savePanel" />""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.RefTargetInvalid, diag.Code);
        Assert.Contains("simple identifier", diag.Message);
        Assert.Equal(DiagnosticSeverity.Error, diag.Severity);
    }

    [Fact]
    public void Ref_MemberAccess_IsEUI029()
    {
        var (_, diags) = ParseAnalyze(UiTestCatalogs.BuiltIn(), "@using EclipseUI.Elements\n" + """<StackPanel Ref={user.Name} />""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.RefTargetInvalid, diag.Code);
        Assert.Contains("simple identifier", diag.Message);
    }

    [Fact]
    public void Ref_BareToken_IsEUI100PlusEUI029()
    {
        var (_, diags) = ParseAnalyze(UiTestCatalogs.BuiltIn(), "@using EclipseUI.Elements\n<StackPanel Ref=panel />");
        // 解析层把裸 token 报 EUI100（"expected attribute value" + "expected '='"），分析器补 EUI029。
        Assert.Equal(3, diags.Count);
        Assert.Equal(2, diags.Count(d => d.Code == UiDiagnostics.Syntax));
        Assert.Equal(1, diags.Count(d => d.Code == UiDiagnostics.RefTargetInvalid));
    }

    [Fact]
    public void Ref_DupAssignment_IsEUI025()
    {
        var (_, diags) = ParseAnalyze(UiTestCatalogs.BuiltIn(), "@using EclipseUI.Elements\n" + """<StackPanel Ref={a} Ref={b} />""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.DuplicateAttribute, diag.Code);
        Assert.Equal("Ref", diag.Message.Split('\'')[1]);
    }

    [Fact]
    public void Ref_WithBindForm_KeepsReservedBindDiagnostic()
    {
        var (_, diags) = ParseAnalyze(UiTestCatalogs.BuiltIn(), "@using EclipseUI.Elements\n" + """<StackPanel @bind-Ref={x} />""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.BindTargetInvalid, diag.Code);
        Assert.Contains("reserved", diag.Message);
    }

    // ---- EUI019/EUI020：引用程序集控件的使用点（design/14 §14.5.4）----

    [Fact]
    public void ReferencedControl_BrokenProperty_IsEUI019AtUseSite()
    {
        var (_, diags) = ParseAnalyze(CatalogWithReferencedDefs(), "@using Probe\n" + """<Broken Gap="8" />""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.PropertyNotBindable, diag.Code);
        Assert.Contains("property 'Gap' on 'Broken'", diag.Message);
        Assert.Contains("global::Probe.Broken", diag.Message);           // 消息带完整类型名
        Assert.Contains("referenced assembly", diag.Message);
        Assert.Contains("GapProperty", diag.Message);
    }

    [Fact]
    public void ReferencedControl_BrokenEvent_IsEUI020AtUseSite()
    {
        var (_, diags) = ParseAnalyze(CatalogWithReferencedDefs(), "@using Probe\n" + """<Broken OnClick={HandleClick} />""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.EventFieldMissing, diag.Code);
        Assert.Contains("event 'OnClick' on 'Broken'", diag.Message);
        Assert.Contains("global::Probe.Broken", diag.Message);
        Assert.Contains("ClickEvent", diag.Message);
    }

    [Fact]
    public void ReferencedControl_UnknownName_StillEUI002()
    {
        var (_, diags) = ParseAnalyze(CatalogWithReferencedDefs(), "@using Probe\n" + """<Broken Gappp="8" />""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.UnknownProperty, diag.Code);
    }

    [Fact]
    public void SourceControl_BrokenProperty_StaysEUI002()
    {
        var (_, diags) = ParseAnalyze(CatalogWithSourceDefs(), "@using Probe\n" + """<Broken Gap="8" />""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.UnknownProperty, diag.Code);
        Assert.Contains("unknown property 'Gap'", diag.Message);
    }

    [Fact]
    public void SourceControl_BrokenEvent_StaysEUI002()
    {
        var (_, diags) = ParseAnalyze(CatalogWithSourceDefs(), "@using Probe\n" + """<Broken OnClick={HandleClick} />""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.UnknownProperty, diag.Code);
        Assert.Contains("unknown property 'OnClick'", diag.Message);
    }

    // ---- EUI021：引用程序集控件的破损内容目标（内容被降级为 None）----

    [Fact]
    public void ReferencedComponent_BrokenContentTarget_IsEUI021AtUseSite()
    {
        var (_, diags) = ParseAnalyze(CatalogWithReferencedDefs(), "@using Probe\n<FaultyCard><StackPanel /></FaultyCard>");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.ContentTargetInvalid, diag.Code);
        Assert.Contains("'FaultyCard' does not accept children", diag.Message);
        Assert.Contains("global::Probe.FaultyCard", diag.Message);
        Assert.Contains("referenced assembly", diag.Message);
        Assert.Contains("content parameter 'Body'", diag.Message);
    }

    [Fact]
    public void SourceComponent_BrokenContentTarget_KeepsEUI006Guidance()
    {
        var (_, diags) = ParseAnalyze(CatalogWithSourceDefs(), "@using Probe\n<FaultyCard><StackPanel /></FaultyCard>");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.TextNotAllowed, diag.Code);
        Assert.Contains("declares no content model", diag.Message);
    }

    [Fact]
    public void ReferencedComponent_SelfClosingWithoutContent_IsClean()
    {
        var (_, diags) = ParseAnalyze(CatalogWithReferencedDefs(), "@using Probe\n" + """<FaultyCard />""");
        Assert.Empty(diags);                                             // 只有真正传内容才触发 EUI021
    }
}
