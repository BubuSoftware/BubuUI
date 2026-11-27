using System.Collections.Generic;
using System.Linq;
using BubuUI.Compiler;
using Xunit;

namespace BubuUI.Headless.Tests;

/// <summary>
/// Tokenizer 切片：`@foreach` 头从正则换成深度/字符串感知扫描。
/// 规范条文：spec/01 §4.11 —— 形式冻结为 `var item in collection`，
/// 不支持解构/类型化条目（EUI100）。旧正则的已知弱点：集合里带引号的 " in "、
/// 括号内的 `in`（方法实参修饰符）会撕裂集合表达式。
/// </summary>
public class TokenizerTests
{
    private static (UiDocument Doc, List<UiDiagnostic> Diags) Parse(string text)
    {
        var diags = new List<UiDiagnostic>();
        var doc = FrontEnd.Parse(text, "Test.bui", diags);
        return (doc, diags);
    }

    private static UiForeach SingleForeach(UiDocument doc)
    {
        var root = Assert.IsType<UiElement>(doc.Nodes.Single(n => n is UiElement));
        return Assert.IsType<UiForeach>(root.Children.Single(n => n is UiForeach));
    }

    [Fact]
    public void Header_StandardForm_ParsesNameAndCollection()
    {
        var (doc, diags) = Parse("@using BubuUI.Elements\n" + """<StackPanel>@foreach (var item in items) { <TextBlock /> }</StackPanel>""");
        Assert.Empty(diags);
        var node = SingleForeach(doc);
        Assert.Equal("item", node.VarName);
        Assert.Equal("items", node.Collection);
    }

    [Fact]
    public void Header_QuotedIn_KeepsCollectionIntact()
    {
        // 旧正则把带引号的 " in " 当分隔符，集合被切成 `" in "))` 后必然报 EUI007。
        var (doc, diags) = Parse(
            "@using BubuUI.Elements\n" + """<StackPanel>@foreach (var t in names.Where(n => n.Contains(" in "))) { <TextBlock /> }</StackPanel>""");
        Assert.Empty(diags);
        var node = SingleForeach(doc);
        Assert.Equal("t", node.VarName);
        Assert.Equal("""names.Where(n => n.Contains(" in "))""", node.Collection);
    }

    [Fact]
    public void Header_InInsideParens_KeepsCollectionIntact()
    {
        // `M(in x)` 的实参修饰符在深度内，不得当分隔符（词边界+深度判定）。
        var (doc, diags) = Parse("@using BubuUI.Elements\n" + """<StackPanel>@foreach (var t in Store.Get(in key)) { <TextBlock /> }</StackPanel>""");
        Assert.Empty(diags);
        var node = SingleForeach(doc);
        Assert.Equal("Store.Get(in key)", node.Collection);
    }

    [Fact]
    public void Header_TypedItem_IsSyntaxWithTypedMessage()
    {
        var (_, diags) = Parse("@using BubuUI.Elements\n" + """<StackPanel>@foreach (Name t in ts) { <TextBlock /> }</StackPanel>""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.Syntax, diag.Code);
        Assert.Contains("typed item", diag.Message);
        Assert.Contains("(var item in collection)", diag.Message);
    }

    [Fact]
    public void Header_Deconstruction_IsSyntaxWithDeconstructionMessage()
    {
        var (_, diags) = Parse("@using BubuUI.Elements\n" + """<StackPanel>@foreach ((a, b) in pairs) { <TextBlock /> }</StackPanel>""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.Syntax, diag.Code);
        Assert.Contains("deconstruction", diag.Message);

        var (_, diags2) = Parse("@using BubuUI.Elements\n" + """<StackPanel>@foreach (var (a, b) in kvs) { <TextBlock /> }</StackPanel>""");
        var diag2 = Assert.Single(diags2);
        Assert.Equal(UiDiagnostics.Syntax, diag2.Code);
        Assert.Contains("deconstruction", diag2.Message);
    }

    [Fact]
    public void Header_MissingIn_IsGenericSyntax()
    {
        var (_, diags) = Parse("@using BubuUI.Elements\n" + """<StackPanel>@foreach (var t ts) { <TextBlock /> }</StackPanel>""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.Syntax, diag.Code);
        Assert.Contains("@foreach requires", diag.Message);
    }

    [Fact]
    public void Header_MissingCollection_IsGenericSyntax()
    {
        var (_, diags) = Parse("@using BubuUI.Elements\n" + """<StackPanel>@foreach (var t in) { <TextBlock /> }</StackPanel>""");
        var diag = Assert.Single(diags);
        Assert.Equal(UiDiagnostics.Syntax, diag.Code);
        Assert.Contains("@foreach requires", diag.Message);
    }
}
