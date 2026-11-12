using EclipseUI.Compiler;
using EclipseUI.LanguageServer.Analysis;
using Xunit;

namespace EclipseUI.Ide.Tests;

/// <summary>
/// 补全：候选来自目录（控件/事件/参数/附加属性），位置来自光标扫描器。
/// 断言用"包含某个标签 + 类型 + 详情"的形态，不锁死列表顺序——顺序是客户端的排序策略。
/// </summary>
public class CompletionTests
{
    private const string Using = "@using EclipseUI.Elements\n\n";

    private static UiCompletionItem Item(UiCompletionList list, string label)
    {
        var item = list.Items.FirstOrDefault(i => i.Label == label);
        Assert.NotNull(item);
        return item!;
    }

    [Fact]
    public void Tags_ComeFromTheCatalog()
    {
        var (service, uri, offset) = TestHost.Open("<|");
        var list = service.Complete(uri, offset);

        Assert.Equal(UiCompletionKind.Tag, Item(list, "Button").Kind);
        Assert.Contains(list.Items, i => i.Label == "StackPanel");
        Assert.Contains(list.Items, i => i.Label == "ScrollViewer");
    }

    [Fact]
    public void Tags_HintAtTheMissingUsing()
    {
        var (service, uri, offset) = TestHost.Open("<|");
        var detail = Item(service.Complete(uri, offset), "Button").Detail;
        Assert.Contains("needs @using EclipseUI.Elements", detail);
    }

    [Fact]
    public void Tags_DoNotHintWhenTheNamespaceIsImported()
    {
        var (service, uri, offset) = TestHost.Open(Using + "<|");
        var detail = Item(service.Complete(uri, offset), "Button").Detail;
        Assert.DoesNotContain("needs @using", detail);
    }

    [Fact]
    public void Tags_DoNotHintForTheTemplatesOwnNamespace()
    {
        // K33（D111）：`@namespace App` 里的 Card 不需要 @using，补全不能反向引导。
        // 直接喂一个只含 App.Card 的目录——内置目录里没有这个 tag。
        var catalog = new UiCatalog(new[] { CardIn("App") }, Array.Empty<UiHiddenTag>(),
            Array.Empty<UiMetadataProblem>());
        var (analysis, offset) = TestHost.Analyze("@namespace App\n<|", catalog: catalog);

        var item = Item(CompletionProvider.Complete(analysis, catalog, offset), "Card");
        Assert.DoesNotContain("needs @using", item.Detail);
    }

    [Fact]
    public void Tags_HintWhenOnlyAnOuterOrSiblingNamespaceHasIt()
    {
        // 链外的命名空间仍要 @using：App.Gallery 看不到 Other.Card
        var catalog = new UiCatalog(new[] { CardIn("Other") }, Array.Empty<UiHiddenTag>(),
            Array.Empty<UiMetadataProblem>());
        var (analysis, offset) = TestHost.Analyze("@namespace App.Gallery\n<|", catalog: catalog);

        var item = Item(CompletionProvider.Complete(analysis, catalog, offset), "Card");
        Assert.Contains("needs @using Other", item.Detail);
    }

    private static UiElementDef CardIn(string ns) => new(
        "Card",
        "global::" + ns + ".Card",
        UiElementOrigin.Control,
        UiContentModel.Children,
        null,
        new Dictionary<string, UiPropDef>(StringComparer.Ordinal),
        new Dictionary<string, UiEventDef>(StringComparer.Ordinal),
        new Dictionary<string, UiParameterDef>(StringComparer.Ordinal),
        new Dictionary<string, UiSlotDef>(StringComparer.Ordinal),
        Array.Empty<UiMetadataProblem>());

    [Fact]
    public void TagCompletion_ReplacesTheTypedPrefix()
    {
        var (service, uri, offset) = TestHost.Open(Using + "<But|");
        var list = service.Complete(uri, offset);

        Assert.Equal(Using.Length + 1, list.Replacement.Start);
        Assert.Equal("But".Length, list.Replacement.Length);
        Assert.Equal("Button", Item(list, "Button").Text);
    }

    [Fact]
    public void Attributes_ListPropertiesEventsAndReservedNames()
    {
        var (service, uri, offset) = TestHost.Open(Using + "<Button |");
        var list = service.Complete(uri, offset);

        Assert.Equal(UiCompletionKind.Property, Item(list, "Label").Kind);
        Assert.Equal("string", Item(list, "Label").Detail);

        var click = Item(list, "OnClick");
        Assert.Equal(UiCompletionKind.Event, click.Kind);
        Assert.Contains("RoutedEventArgs", click.Detail);

        Assert.Equal(UiCompletionKind.Keyword, Item(list, "Class").Kind);
        Assert.Equal(UiCompletionKind.Keyword, Item(list, "Key").Kind);
    }

    [Fact]
    public void Attributes_IncludeAttachedPropertiesFromTheCatalog()
    {
        var (service, uri, offset) = TestHost.Open(Using + "<Button Grid.|");
        var list = service.Complete(uri, offset);
        Assert.Equal(UiCompletionKind.AttachedProperty, Item(list, "Grid.Row").Kind);
    }

    [Fact]
    public void Bindings_OnlyOfferWritableTargets()
    {
        var (service, uri, offset) = TestHost.Open(Using + "<CheckBox @bind-|");
        var list = service.Complete(uri, offset);

        Assert.Equal(UiCompletionKind.Property, Item(list, "@bind-Checked").Kind);
        Assert.DoesNotContain(list.Items, i => i.Label.StartsWith("@bind-On", StringComparison.Ordinal));
        Assert.DoesNotContain(list.Items, i => i.Label is "@bind-Class" or "@bind-Key");
    }

    /// <summary>
    /// spec/01 §4.5：候选 = 目录里的可绑属性，一个不多一个不少。
    /// 「不多个」靠目录的 <c>NonBindableProperties</c> 逐项对齐——只读、没有 <c>{P}Property</c>
    /// 的公开属性在目录里记了原因，它们不该出现在候选里（诊断负责解释为什么绑不了）。
    /// </summary>
    [Fact]
    public void Bindings_ListExactlyTheBindableProperties()
    {
        var (service, uri, offset) = TestHost.Open(Using + "<Button @bind-|");
        var labels = service.Complete(uri, offset).Items.Select(i => i.Label).ToList();

        var button = TestHost.Catalog.Elements.Single(e => e.Tag == "Button");
        foreach (var property in button.Properties.Keys)
            Assert.Contains("@bind-" + property, labels);

        foreach (var name in new[] { "IsEnabled", "IsHovered", "Parent", "Classes" })
            Assert.DoesNotContain("@bind-" + name, labels);
        Assert.All(button.NonBindableProperties.Keys,
            name => Assert.DoesNotContain("@bind-" + name, labels));
    }

    [Fact]
    public void Values_OfferBooleanLiterals()
    {
        var (service, uri, offset) = TestHost.Open(Using + "<CheckBox Checked=\"|\" />");
        var labels = service.Complete(uri, offset).Items.Select(i => i.Label).ToArray();
        Assert.Contains("true", labels);
        Assert.Contains("false", labels);
    }

    [Fact]
    public void Values_OfferStylePropertiesForTheInlineStyleAttribute()
    {
        var (service, uri, offset) = TestHost.Open(Using + "<Button Style=\"|\" />");
        var list = service.Complete(uri, offset);

        Assert.NotEmpty(TestHost.Catalog.StyleProperties.Properties);
        Assert.Equal(UiCompletionKind.StyleProperty, Item(list, "CornerRadius").Kind);
        // 插入形态是 `Name: `，接着就能写值。
        Assert.Equal("CornerRadius: ", Item(list, "CornerRadius").Text);
    }

    /// <summary>
    /// design/19 §9 值候选：光标在 <c>Prop: |</c> 之后时按目录形态给值——
    /// enum 属性列成员表（合成目录注入自定义标注属性，内置属性没有 enum 形态）。
    /// </summary>
    [Fact]
    public void Values_OfferEnumMembersForStylePropertyAtValuePosition()
    {
        var tone = new StylePropertyDef("Tone", StyleValueKind.Enum,
            "global::Probe.BadgeStyles.ToneProperty", ResourceField: null,
            "global::Probe.BadgeStyles", IsBuiltIn: false,
            "global::Probe.Tone", new[] { "Danger", "Neutral", "Success" });
        var catalog = new UiCatalog(TestHost.Catalog.Elements, Array.Empty<UiHiddenTag>(),
            Array.Empty<UiMetadataProblem>(),
            styleProperties: new StylePropertyCatalog(new[] { tone }, Array.Empty<UiMetadataProblem>()));
        var (analysis, offset) = TestHost.Analyze(Using + "<Button Style=\"Tone: |\" />", catalog: catalog);

        var list = CompletionProvider.Complete(analysis, catalog, offset);

        Assert.Equal(new[] { "Danger", "Neutral", "Success" },
            list.Items.Select(i => i.Label).ToArray());
        Assert.All(list.Items, i => Assert.Equal(UiCompletionKind.EnumMember, i.Kind));

        // 属性名位置（没跟冒号）不受值分支影响，仍列属性名。
        var (nameAnalysis, nameOffset) = TestHost.Analyze(Using + "<Button Style=\"|\" />", catalog: catalog);
        var nameList = CompletionProvider.Complete(nameAnalysis, catalog, nameOffset);
        Assert.Equal(UiCompletionKind.StyleProperty, Item(nameList, "Tone").Kind);
    }

    [Fact]
    public void Values_OfferEnumMembers()
    {
        var catalog = TestHost.EnumCatalog(out _);
        var text = "<Box Align=\"|\" />";
        var offset = text.IndexOf('|');
        var analysis = DocumentAnalysis.Create(text.Remove(offset, 1), "Probe.ui", catalog);

        var list = CompletionProvider.Complete(analysis, catalog, offset);
        var labels = list.Items.Select(i => i.Label).ToArray();
        Assert.Equal(new[] { "Center", "Left", "Right" }, labels);
        Assert.All(list.Items, i => Assert.Equal(UiCompletionKind.EnumMember, i.Kind));
    }

    [Fact]
    public void Directives_CoverTheWholeLanguageSurface()
    {
        var (service, uri, offset) = TestHost.Open("@|");
        var labels = service.Complete(uri, offset).Items.Select(i => i.Label).ToArray();
        foreach (var expected in new[] { "@if", "@foreach", "@switch", "@using", "@code", "@style" })
            Assert.Contains(expected, labels);
    }

    [Fact]
    public void UsingDirective_OffersNamespaces()
    {
        var (service, uri, offset) = TestHost.Open("@using |");
        var list = service.Complete(uri, offset);
        Assert.Contains(list.Items, i => i.Label == "EclipseUI.Elements" && i.Kind == UiCompletionKind.Namespace);
    }

    [Fact]
    public void ClosingTag_OffersTheOpenElements()
    {
        var (service, uri, offset) = TestHost.Open(Using + "<StackPanel>\n    <Button>\n</|");
        var labels = service.Complete(uri, offset).Items.Select(i => i.Label).ToArray();
        Assert.Contains("StackPanel", labels);
        Assert.Contains("Button", labels);
        // 内层优先
        Assert.Equal("Button", labels[0]);
    }
}
