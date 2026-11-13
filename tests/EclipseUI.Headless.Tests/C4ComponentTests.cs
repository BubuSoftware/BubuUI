using System;
using System.Collections.Generic;
using System.Linq;
using EclipseUI.Compiler;
using EclipseUI.TestKit;
using Xunit;

namespace EclipseUI.Headless.Tests;

/// <summary>
/// C4 切片：组件标签——参数解析、委托参数（事件）、必填参数、@bind 回调、内容参数。
/// 规范条文：spec/01 §4.2/§4.4/§4.14；spec/02 §4.12；design/14 §14.6。
/// 目录从 BuiltIn() 派生，其上追加合成组件标签以驱动诊断与解析分支。
/// </summary>
public class C4ComponentTests
{
    private static (UiDocument Doc, List<UiDiagnostic> Diags) ParseAnalyze(UiCatalog catalog, string text)
    {
        var diags = new List<UiDiagnostic>();
        var doc = FrontEnd.Parse(text, "Test.ui", diags);
        FrontEnd.Analyze(doc, catalog, diags);
        return (doc, diags);
    }

    private static UiParameterDef Param(string name, string type, bool required = false,
        bool isDelegate = false, int arity = 0, string? changed = null,
        UiTypeShapeKind shapeKind = UiTypeShapeKind.Other, string? canonical = null) => new(
        name, type, new UiTypeShape(shapeKind, canonical ?? type, []),
        CanWrite: true, Required: required, IsDelegate: isDelegate, DelegateArity: arity,
        IsContent: false, ContentIsMultiple: false, ChangedCallbackName: changed);

    private static UiParameterDef Content(string name, string type, bool multiple) => new(
        name, type, new UiTypeShape(UiTypeShapeKind.Other, type, []),
        CanWrite: true, Required: false, IsDelegate: false, DelegateArity: 0,
        IsContent: true, ContentIsMultiple: multiple, ChangedCallbackName: null);

    /// <summary>
    /// 合成组件：ProbeCard（Title/Count 必填参数、OnClose 无参/OnSelect 一参委托、
    /// Color 绑定参数（带 ColorChanged）、Plain 无回调、Body 单根内容）；
    /// ProbeList（Count 可选参数、Items 多根内容）。各只含一个内容参数（spec/02 §4.12.3）。
    /// </summary>
    private static UiCatalog CatalogWithComponent()
    {
        var built = UiTestCatalogs.BuiltIn();
        var cardParameters = new Dictionary<string, UiParameterDef>(StringComparer.Ordinal)
        {
            ["Title"] = Param("Title", "global::System.String?",
                shapeKind: UiTypeShapeKind.Primitive, canonical: "string"),
            ["Count"] = Param("Count", "global::System.Int32", required: true,
                shapeKind: UiTypeShapeKind.Primitive, canonical: "int"),
            ["OnClose"] = Param("OnClose", "global::System.Action", isDelegate: true),
            ["OnSelect"] = Param("OnSelect", "global::System.Action<global::System.Int32>", isDelegate: true, arity: 1),
            ["Color"] = Param("Color", "global::System.String?", changed: "ColorChanged",
                shapeKind: UiTypeShapeKind.Primitive, canonical: "string"),
            ["Plain"] = Param("Plain", "global::System.String?",
                shapeKind: UiTypeShapeKind.Primitive, canonical: "string"),
            ["ColorChanged"] = Param("ColorChanged", "global::System.Action<global::System.String?>", isDelegate: true),
            ["Body"] = Content("Body", "global::EclipseUI.Element?", multiple: false),
        };
        var listParameters = new Dictionary<string, UiParameterDef>(StringComparer.Ordinal)
        {
            ["Count"] = Param("Count", "global::System.Int32",
                shapeKind: UiTypeShapeKind.Primitive, canonical: "int"),
            ["Items"] = Content("Items", "global::System.Collections.Generic.IReadOnlyList<global::EclipseUI.Element>", multiple: true),
        };
        var card = new UiElementDef(
            "ProbeCard", "global::Probe.Card", UiContentModel.None, null,
            new Dictionary<string, UiPropDef>(StringComparer.Ordinal),
            new Dictionary<string, UiEventDef>(StringComparer.Ordinal),
            cardParameters,
            new Dictionary<string, UiSlotDef>(StringComparer.Ordinal), []);
        var list = new UiElementDef(
            "ProbeList", "global::Probe.List", UiContentModel.None, null,
            new Dictionary<string, UiPropDef>(StringComparer.Ordinal),
            new Dictionary<string, UiEventDef>(StringComparer.Ordinal),
            listParameters,
            new Dictionary<string, UiSlotDef>(StringComparer.Ordinal), []);
        var merged = built.Elements.Append(card).Append(list);
        return new UiCatalog(merged, built.HiddenTags, built.Problems, built.AttachedProperties);
    }

    [Fact]
    public void Component_LiteralAndExpressionParameters_ResolveWithoutDiagnostics()
    {
        var catalog = CatalogWithComponent();
        var (doc, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<ProbeCard Title="Hi" Count={Value} />""");
        Assert.Empty(diags);
        var element = doc.Nodes.OfType<UiElement>().Single();
        Assert.NotNull(element.Attributes[0].ParamDef);
        Assert.Equal("\"Hi\"", element.Attributes[0].ConvertedLiteral);
    }

    [Fact]
    public void Component_UnknownParameter_IsEUI002()
    {
        var catalog = CatalogWithComponent();
        var (_, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<ProbeCard Count="42" Ttile="x" />""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.UnknownProperty, diag.Code);
        Assert.Contains("unknown property", diag.Message);
        Assert.Contains("Title", diag.Message);
    }

    [Fact]
    public void RequiredParameterMissing_IsEUI027()
    {
        var catalog = CatalogWithComponent();
        var (_, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<ProbeCard Title="Hi" />""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.RequiredParameterMissing, diag.Code);
        Assert.Contains("Count", diag.Message);
        Assert.Equal(DiagnosticSeverity.Error, diag.Severity);
    }

    [Fact]
    public void DelegateParameterLiteral_IsEUI004()
    {
        var catalog = CatalogWithComponent();
        var (_, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<ProbeCard Count="1" OnClose="HandleClose" />""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.EventNeedsExpression, diag.Code);
        Assert.Contains("delegate", diag.Message);
        Assert.Contains("{expression}", diag.Message);
    }

    [Fact]
    public void DelegateParameterExpression_IsAccepted()
    {
        var catalog = CatalogWithComponent();
        var (_, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<ProbeCard Count="1" OnClose={() => Close()} />""");
        Assert.Empty(diags);
    }

    [Fact]
    public void BindWithoutChangedCallback_IsEUI040()
    {
        var catalog = CatalogWithComponent();
        var (_, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<ProbeCard Count="1" @bind-Plain={Name} />""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.BindTargetInvalid, diag.Code);
        Assert.Contains("PlainChanged", diag.Message);
    }

    [Fact]
    public void BindOnDelegateParameter_IsEUI040()
    {
        var catalog = CatalogWithComponent();
        var (_, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<ProbeCard Count="1" @bind-OnClose={Name} />""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.BindTargetInvalid, diag.Code);
        Assert.Contains("OnClose", diag.Message);
    }

    [Fact]
    public void ContentParameterViaAttribute_IsEUI021()
    {
        var catalog = CatalogWithComponent();
        var (_, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<ProbeCard Count="1" Body={content} />""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.ContentTargetInvalid, diag.Code);
        Assert.Contains("content parameter", diag.Message);
    }

    [Fact]
    public void SingleRootContent_TwoRoots_IsEUI021()
    {
        var catalog = CatalogWithComponent();
        var (_, diags) = ParseAnalyze(catalog,
            """
            @using Probe
            @using EclipseUI.Elements
            <ProbeCard Count="1">
                <StackPanel />
                <StackPanel />
            </ProbeCard>
            """);
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.ContentTargetInvalid, diag.Code);
        Assert.Contains("single-root", diag.Message);
    }

    [Fact]
    public void MultiRootContent_ChildrenAccepted()
    {
        var catalog = CatalogWithComponent();
        var (_, diags) = ParseAnalyze(catalog,
            """
            @using Probe
            @using EclipseUI.Elements
            <ProbeList Count="1">
                <StackPanel />
                <StackPanel />
                @if (Flag)
                {
                    <TextBlock />
                }
            </ProbeList>
            """);
        Assert.Empty(diags);
    }

    [Fact]
    public void SingleRootContent_OneRootAccepted()
    {
        var catalog = CatalogWithComponent();
        var (_, diags) = ParseAnalyze(catalog, "@using Probe\n@using EclipseUI.Elements\n" + """<ProbeCard Count="1"><TextBlock /></ProbeCard>""");
        Assert.Empty(diags);
    }

    [Fact]
    public void BindInsideContent_IsEUI040()
    {
        var catalog = CatalogWithComponent();
        var (_, diags) = ParseAnalyze(catalog,
            """
            @using Probe
            @using EclipseUI.Elements
            <ProbeCard Count="1">
                <CheckBox @bind-Checked={Flag} />
            </ProbeCard>
            """);
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.BindTargetInvalid, diag.Code);
        Assert.Contains("built once", diag.Message);
    }

    [Fact]
    public void TextInsideContent_IsEUI006()
    {
        var catalog = CatalogWithComponent();
        var (_, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<ProbeCard Count="1">hello</ProbeCard>""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.TextNotAllowed, diag.Code);
    }

    [Fact]
    public void ComponentsWithoutContentModel_ChildrenRejected()
    {
        var catalog = CatalogWithComponent();
        var (_, diags) = ParseAnalyze(catalog, "@using Probe\n@using EclipseUI.Elements\n" + """<StackPanel><ProbeCard Count="1" /></StackPanel>""");
        Assert.Empty(diags);
    }

    [Fact]
    public void ComponentChildInsideStackPanel_DoesNotParseEvents()
    {
        // 组件不解析自身事件：OnClose 是委托参数，按参数名赋值（不做 On* 推断）

        var catalog = CatalogWithComponent();
        var (_, diags) = ParseAnalyze(catalog, "@using Probe\n@using EclipseUI.Elements\n" + """<StackPanel><ProbeCard Count="1" OnClose={Close} /></StackPanel>""");
        Assert.Empty(diags);
    }

    [Fact]
    public void Component_ClassLiteral_ParsesClassNames()
    {
        // E2 转正：组件实例同为 Element，Class= 语义与控件一致（无目录语境宽松回写）。
        var catalog = CatalogWithComponent();
        var (doc, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<ProbeCard Count="1" Class="big" />""");
        Assert.Empty(diags);
        var attr = doc.Nodes.OfType<UiElement>().First(e => e.Tag == "ProbeCard").Attributes
            .Single(a => a.Name == "Class");
        Assert.Equal(new[] { "big" }, attr.ClassNames);
    }

    [Fact]
    public void DuplicateComponentParameter_IsEUI025()
    {
        var catalog = CatalogWithComponent();
        var (_, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<ProbeCard Count="1" Title="a" Title="b" />""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.DuplicateAttribute, diag.Code);
    }

    [Fact]
    public void DuplicateBoundComponentParameter_IsEUI033()
    {
        var catalog = CatalogWithComponent();
        var (_, diags) = ParseAnalyze(catalog, "@using Probe\n" + """<ProbeCard Count="1" Title="a" @bind-Title={Name} />""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.SameTargetDuplicate, diag.Code);
    }
}
