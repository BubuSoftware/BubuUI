using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BubuUI;
using BubuUI.Compiler;
using BubuUI.Compiler.Metadata;
using BubuUI.TestKit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Xunit.Abstractions;

namespace BubuUI.Compiler.Tests;

/// <summary>
/// 派生目录测试（design/14 §14.11.1）：扫描真实 BubuUI.Runtime.dll，
/// 断言发现规则、注册表交叉校验、失效级别守卫与仓库内表面快照。
/// 表面直接来自符号扫描，不另写第二份手写目录（T1 禁止第二事实源）。
/// </summary>
public class CatalogScannerTests
{
    private readonly ITestOutputHelper _output;

    public CatalogScannerTests(ITestOutputHelper output) => _output = output;

    private static readonly Assembly RuntimeAssembly = UiTestCatalogs.RuntimeAssembly;

    private static CSharpCompilation Compile(string source = "", string assemblyName = "CatalogProbe")
        => UiTestCatalogs.ProbeCompilation(source, assemblyName);

    private static UiCatalog BuiltIn() => UiTestCatalogs.BuiltIn();

    private static UiCatalog Scan(string source) => UiTestCatalogs.Scan(source);

    private static Type? ClrType(string declaringTypeName)
        => RuntimeAssembly.GetType(declaringTypeName.Replace("global::", string.Empty), throwOnError: false);

    private static StyledProperty RegisteredProperty(UiElementDef element, string propertyName)
    {
        var def = element.Properties[propertyName];
        var owner = ClrType(def.DeclaringTypeName);
        Assert.NotNull(owner);
        var field = owner!.GetField(def.PropertyFieldName,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        Assert.NotNull(field);
        return (StyledProperty)field!.GetValue(null)!;
    }

    // ---- 发现规则 ----

    [Fact]
    public void BuiltInCatalog_ContainsTheFiveHandTags()
    {
        var tags = BuiltIn().Elements.Select(e => e.Tag).ToList();
        foreach (var expected in new[] { "TextBlock", "StackPanel", "Button", "CheckBox", "ListBoxItem" })
            Assert.Contains(expected, tags);
    }

    // ---- 声明式字面量解析器（design/14 §14.7.3，2026-11-12）----

    [Fact]
    public void LiteralParser_ValidDeclaration_EntersCatalog()
    {
        var catalog = Scan("""
            using BubuUI;
            namespace Probe;

            [UiLiteralParser(Pattern = "^(A|B)$", Hint = "one of: A, B",
                Candidates = new[] { "A", "B" })]
            public readonly struct Tone
            {
                public static Tone Parse(string s) => default;
            }
            """);
        var parser = Assert.Single(catalog.LiteralParsers, p => p.CanonicalKey == "Probe.Tone");
        Assert.Equal("Probe.Tone", parser.CanonicalKey);
        Assert.Equal("global::Probe.Tone.Parse", parser.ParseCall);
        Assert.Equal("^(A|B)$", parser.Pattern);
        Assert.Equal("one of: A, B", parser.Hint);
        Assert.Equal(new[] { "A", "B" }, parser.Candidates);
        Assert.DoesNotContain(catalog.Problems, p => p.DeclaringTypeName.Contains("Probe.Tone"));
    }

    [Fact]
    public void LiteralParser_BadSignature_IsMetadataProblem()
    {
        var catalog = Scan("""
            using BubuUI;
            namespace Probe;

            [UiLiteralParser]
            public readonly struct Tone
            {
                public static string Parse(string s) => s;   // 返回类型不是自身 → EUI026
            }
            """);
        Assert.DoesNotContain(catalog.LiteralParsers, p => p.CanonicalKey == "Probe.Tone");
        var problem = Assert.Single(catalog.Problems,
            p => p.DeclaringTypeName.Contains("Probe.Tone"));
        Assert.Equal(UiDiagnostics.MetadataDeclarationInvalid, problem.Code);
        Assert.Contains("public static Tone Parse(string)", problem.Message);
    }

    [Fact]
    public void LiteralParser_CustomMethodName_IsHonored()
    {
        var catalog = Scan("""
            using BubuUI;
            namespace Probe;

            [UiLiteralParser(ParseMethod = "FromName", Pattern = "^(A|B)$")]
            public readonly struct Tone
            {
                public static Tone FromName(string s) => default;
            }
            """);
        var parser = Assert.Single(catalog.LiteralParsers, p => p.CanonicalKey == "Probe.Tone");
        Assert.Equal("global::Probe.Tone.FromName", parser.ParseCall);
    }

    [Fact]
    public void BuiltInCatalog_ExcludesInfrastructureAbstractAndBaseTypes()
    {
        var catalog = BuiltIn();
        foreach (var hidden in catalog.HiddenTags)
            _output.WriteLine($"hidden {hidden.Tag} ({hidden.ClrTypeName}) [{hidden.ReasonCode}] {hidden.Reason}");
        Assert.DoesNotContain(catalog.Elements, e => e.Tag == "TextRun");
        Assert.DoesNotContain(catalog.Elements, e => e.Tag == "ConditionalBlock");
        Assert.DoesNotContain(catalog.Elements, e => e.Tag == "Panel");
        Assert.DoesNotContain(catalog.Elements, e => e.Tag == "Element");
    }

    [Fact]
    public void StaticEventHost_IsReportedAsMetadataProblemNotATag()
    {
        // C2：框架的 7 个输入/焦点事件已迁入 Element（design/14 §14.4.3），内置目录不再有静态宿主问题，
        // 且继承事件出现在每个标签上（OnPointerPressed 不在 Button 声明，只从基类继承）。
        var builtIn = BuiltIn();
        Assert.DoesNotContain(builtIn.Problems, p => p.Code == UiDiagnostics.MetadataDeclarationInvalid);
        Assert.Contains(builtIn.Elements,
            e => e.Tag == "Button" && e.Events.ContainsKey("OnPointerPressed"));

        var catalog = Scan("""
            using BubuUI;
            namespace Probe;
            public static class ProbeEventHost
            {
                public static readonly RoutedEvent FooEvent = new RoutedEvent("Foo", RoutingStrategy.Bubble);
            }
            """);

        // 静态宿主按类型报一条（不是每个字段一条），并且该类型不进目录
        var problems = catalog.Problems.Where(p => p.Code == UiDiagnostics.MetadataDeclarationInvalid).ToList();
        foreach (var problem in problems)
        {
            _output.WriteLine($"[{problem.Code}] {problem.DeclaringTypeName}: {problem.Message}");
            Assert.Contains("ProbeEventHost", problem.DeclaringTypeName, StringComparison.Ordinal);
        }

        Assert.Single(problems);
        Assert.DoesNotContain(catalog.Elements, e => e.Events.Values.Any(v => v.FieldName.Contains("Foo", StringComparison.Ordinal)));
    }

    [Fact]
    public void UiTagAttribute_IsRetired_TagIsAlwaysTheTypeName()
    {
        // `[UiTag]` 已退役（design/14 §14.4.1 / K32）：即使作者自己定义同名特性，目录也只按类型短名登记。
        var catalog = Scan("""
            using System;
            using BubuUI;
            namespace Probe;
            public sealed class UiTagAttribute : Attribute { public UiTagAttribute(string name) { } }
            [UiTag("RenamedBox")]
            public sealed class ProbePanel : StackPanel { }
            """);
        Assert.Contains(catalog.Elements, e => e.Tag == "ProbePanel");
        Assert.DoesNotContain(catalog.Elements, e => e.Tag == "RenamedBox");
    }

    [Fact]
    public void UiIgnoreOnType_RemovesTagEntirely()
    {
        var catalog = Scan("""
            using BubuUI;
            namespace Probe;
            [UiIgnore]
            public sealed class SecretBox : StackPanel { }
            """);
        Assert.DoesNotContain(catalog.Elements, e => e.Tag == "SecretBox");
        Assert.DoesNotContain(catalog.HiddenTags, h => h.Tag == "SecretBox");
    }

    [Theory]
    [InlineData("internal sealed class ProbeBox : StackPanel { }", false)]
    [InlineData("public abstract class ProbeBox : StackPanel { }", false)]
    [InlineData("public class ProbeBox : StackPanel { private ProbeBox() { } }", false)]
    [InlineData("public class ProbeBox : StackPanel { public ProbeBox(int x) { } }", false)]
    [InlineData("public class ProbeBox { }", false)]
    [InlineData("public class ProbeBox : StackPanel { }", true)]
    public void DiscoveryRules_MatchExposureContract(string declaration, bool expectExposed)
    {
        var catalog = Scan("using BubuUI;\nnamespace Probe;\n" + declaration);
        Assert.Equal(expectExposed, catalog.Elements.Any(e => e.Tag == "ProbeBox"));
    }

    [Fact]
    public void NestedType_IsHiddenNotExposed()
    {
        var catalog = Scan("""
            using BubuUI;
            namespace Probe;
            public class Outer : StackPanel
            {
                public class Inner : StackPanel { }
            }
            """);
        Assert.DoesNotContain(catalog.Elements, e => e.Tag == "Inner");
        Assert.Contains(catalog.HiddenTags, h => h.Tag == "Inner" && h.ReasonCode == UiDiagnostics.TagNotExposed);
    }

    // ---- 属性暴露 ----

    [Fact]
    public void InheritedStyledProperty_IsVisibleOnLeaf()
    {
        var catalog = BuiltIn();
        var button = catalog.Elements.Single(e => e.Tag == "Button");
        Assert.True(button.Properties.ContainsKey("IsHitTestVisible"));
        var stack = catalog.Elements.Single(e => e.Tag == "StackPanel");
        Assert.True(stack.Properties.ContainsKey("Padding"));
        Assert.True(stack.Properties.ContainsKey("ClipToBounds"));
        Assert.True(stack.Properties.ContainsKey("Gap"));
    }

    [Fact]
    public void PlainClrProperty_IsNotExposed()
    {
        var catalog = Scan("""
            using BubuUI;
            namespace Probe;
            public class PlainBox : StackPanel
            {
                public int Counter { get; set; }
            }
            """);
        var box = catalog.Elements.Single(e => e.Tag == "PlainBox");
        Assert.False(box.Properties.ContainsKey("Counter"));
        Assert.Empty(box.Problems);
    }

    [Fact]
    public void ReadOnlyProperty_IsNotExposed()
    {
        var catalog = Scan("""
            using BubuUI;
            namespace Probe;
            public class RoBox : StackPanel
            {
                public static readonly StyledProperty<int> CountProperty = StyledProperty<int>.Register<RoBox>("Count", 0);
                public int Count => GetValue(CountProperty);
            }
            """);
        var box = catalog.Elements.Single(e => e.Tag == "RoBox");
        Assert.False(box.Properties.ContainsKey("Count"));
    }

    [Fact]
    public void MismatchedPropertyField_ReportsEui019()
    {
        var catalog = Scan("""
            using BubuUI;
            namespace Probe;
            public class BadBox : StackPanel
            {
                public static readonly StyledProperty<int> SizeProperty = StyledProperty<int>.Register<BadBox>("Size", 0);
                public double Size { get => 0; set { } }
            }
            """);
        var box = catalog.Elements.Single(e => e.Tag == "BadBox");
        Assert.False(box.Properties.ContainsKey("Size"));
        Assert.Contains(box.Problems, p => p.Code == UiDiagnostics.PropertyNotBindable && p.MemberName == "Size");
    }

    [Fact]
    public void ReservedPropertyName_ReportsEui026()
    {
        var catalog = Scan("""
            using BubuUI;
            namespace Probe;
            public class KeyBox : StackPanel
            {
                public static readonly StyledProperty<string> KeyProperty = StyledProperty<string>.Register<KeyBox>("Key", "");
                public string Key { get => ""; set { } }
            }
            """);
        var box = catalog.Elements.Single(e => e.Tag == "KeyBox");
        Assert.Contains(box.Problems, p => p.Code == UiDiagnostics.MetadataDeclarationInvalid && p.MemberName == "Key");
    }

    // ---- 内容模型 ----

    [Theory]
    [InlineData("StackPanel", UiContentModel.Children)]
    [InlineData("ListBoxItem", UiContentModel.Children)]
    [InlineData("Button", UiContentModel.Text)]
    [InlineData("CheckBox", UiContentModel.Text)]
    [InlineData("TextBlock", UiContentModel.TextRuns)]
    public void BuiltInContentModel_MatchesRuntimeAnnotations(string tag, UiContentModel expected)
    {
        var def = Assert.Single(BuiltIn().Elements, e => e.Tag == tag);
        Assert.Equal(expected, def.Content);
    }

    [Fact]
    public void ContentKind_And_MetadataContentModel_NumbersMatch()
    {
        Assert.Equal((int)UiContentModel.None, (int)UiContentKind.None);
        Assert.Equal((int)UiContentModel.Children, (int)UiContentKind.Children);
        Assert.Equal((int)UiContentModel.Text, (int)UiContentKind.Text);
        Assert.Equal((int)UiContentModel.TextRuns, (int)UiContentKind.TextRuns);
    }

    [Fact]
    public void ContentTargetMissing_ReportsEui021AndDegradesToNone()
    {
        var catalog = Scan("""
            using BubuUI;
            namespace Probe;
            [UiContent(UiContentKind.Text)]
            public class NoTargetBox : StackPanel { }
            """);
        var box = catalog.Elements.Single(e => e.Tag == "NoTargetBox");
        Assert.Equal(UiContentModel.None, box.Content);
        Assert.Contains(box.Problems, p => p.Code == UiDiagnostics.ContentTargetInvalid);
    }

    [Fact]
    public void ContentModelIsInheritedFromNearestAncestor()
    {
        var catalog = Scan("""
            using BubuUI;
            namespace Probe;
            [UiContent(UiContentKind.Text, Target = nameof(TextLike.Title))]
            public class TextLike : Element
            {
                public static readonly StyledProperty<string> TitleProperty =
                    StyledProperty<string>.Register<TextLike>("Title", "");
                public string Title { get => ""; set { } }
            }
            public class DerivedText : TextLike { }
            """);
        var def = catalog.Elements.Single(e => e.Tag == "DerivedText");
        Assert.Equal(UiContentModel.Text, def.Content);
        Assert.Equal("Title", def.ContentTarget);
    }

    // ---- 与注册表/运行时的交叉一致性 ----

    [Fact]
    public void EveryDerivedProperty_ExistsInTheRegistryWithMatchingOwner()
    {
        foreach (var element in BuiltIn().Elements)
            foreach (var def in element.Properties.Values)
            {
                var registered = RegisteredProperty(element, def.Name);
                Assert.Equal(def.Name, registered.Name);
                var owner = ClrType(def.DeclaringTypeName);
                Assert.NotNull(owner);
                Assert.Equal(owner!, registered.OwnerType);
            }
    }

    [Fact]
    public void TemplateVisibleProperties_HaveNonNoneInvalidationExceptWhitelist()
    {
        // These input-only properties do not invalidate layout or painted content.
        // Cursor is resolved by the platform from the hovered/captured element.
        // Match registered property identities so an unrelated property with the
        // same name cannot accidentally bypass this guard.
        StyledProperty[] allowed = [Element.IsHitTestVisibleProperty, Element.CursorProperty];
        foreach (var element in BuiltIn().Elements)
            foreach (var name in element.Properties.Keys)
            {
                var registered = RegisteredProperty(element, name);
                if (registered.Invalidate != InvalidateKind.None) continue;

                _output.WriteLine($"Invalidate=None: {element.Tag}.{name}");
                Assert.Contains(registered, allowed);
            }
    }

    // ---- 表面快照 ----

    [Fact]
    public void EveryDerivedEvent_FieldExistsWithRoutedEventType()
    {
        foreach (var element in BuiltIn().Elements)
            foreach (var evt in element.Events.Values)
            {
                var owner = ClrType(evt.DeclaringTypeName);
                Assert.NotNull(owner);
                var field = owner!.GetField(evt.FieldName,
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                Assert.NotNull(field);
                Assert.True(field!.IsStatic && field.IsInitOnly);
                Assert.Equal(typeof(RoutedEvent), field.FieldType);
                var declared = (RoutedEvent)field.GetValue(null)!;
                Assert.Equal(evt.EventName, declared.Name);
            }
    }

    /// <summary>
    /// D62 收敛证据：EUI040 的目标可绑性不再依赖手写 `UiPropDef.Bindable` 标注——
    /// 符号派生的 <c>CanWrite</c> 对每个模板可见属性都为 true。
    /// </summary>
    [Fact]
    public void EveryTemplateVisibleProperty_IsWritable()
    {
        var unwritable = BuiltIn().Elements
            .SelectMany(e => e.Properties.Values.Where(p => !p.CanWrite).Select(p => $"{e.Tag}.{p.Name}"))
            .ToList();
        Assert.Empty(unwritable);
    }

    /// <summary>
    /// design/14 §14.11.1：内置表面固化成仓库内的文本 golden（<c>TestData/BuiltInSurface.golden.txt</c>）。
    /// 手写目录已删除，golden 是扫描结果的渲染快照而不是第二事实源——扫描器仍是唯一真相，
    /// 本测试的作用是让控件 API 变化强制触发一次显式评审（用 <c>BUBUUI_UPDATE_SURFACE_GOLDEN=1</c> 重新生成并看 diff）。
    /// </summary>
    [Fact]
    public void Surface_MatchesGoldenSnapshotFile()
    {
        var actual = UiTestCatalogs.RenderSurface(BuiltIn());
        var binaryPath = Path.Combine(AppContext.BaseDirectory, "TestData", "BuiltInSurface.golden.txt");

        if (Environment.GetEnvironmentVariable("BUBUUI_UPDATE_SURFACE_GOLDEN") == "1")
        {
            var sourcePath = Path.Combine(FindProjectDirectory().FullName, "TestData", "BuiltInSurface.golden.txt");
            foreach (var target in new[] { sourcePath, binaryPath })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, actual);
            }
        }

        Assert.True(File.Exists(binaryPath),
            $"golden snapshot missing: {binaryPath} — run once with BUBUUI_UPDATE_SURFACE_GOLDEN=1");
        Assert.Equal(Normalize(File.ReadAllText(binaryPath)), Normalize(actual));
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static DirectoryInfo FindProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BubuUI.Compiler.Tests.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!;
    }

    // ---- 类型形状：查表键与生成文本用的显示名不得串键（design/14 D15）----

    [Fact]
    public void CanonicalKey_UsesKeywordForPrimitives_NotDisplayNames()
    {
        var catalog = Scan("""
            using BubuUI;
            namespace Probe;
            public class ShapeBox : StackPanel
            {
                public static readonly StyledProperty<double> SizeProperty = StyledProperty<double>.Register<ShapeBox>("Size", 0);
                public static readonly StyledProperty<WrapDirection> DirProperty = StyledProperty<WrapDirection>.Register<ShapeBox>("Dir", default);
                public double Size { get => 0; set { } }
                public WrapDirection Dir { get => default; set { } }
            }
            public enum WrapDirection { Horizontal, Vertical }
            """);
        var box = catalog.Elements.Single(e => e.Tag == "ShapeBox");

        Assert.Equal("double", box.Properties["Size"].TypeShape.CanonicalKey);
        Assert.Equal("double", box.Properties["Size"].ClrTypeDisplayName);
        Assert.Equal(UiTypeShapeKind.Primitive, box.Properties["Size"].TypeShape.Kind);

        var dir = box.Properties["Dir"].TypeShape;
        Assert.Equal(UiTypeShapeKind.Enum, dir.Kind);
        Assert.Equal("Probe.WrapDirection", dir.CanonicalKey);
        Assert.Equal(["Horizontal", "Vertical"], dir.EnumMembers.ToArray());
    }

    // ---- 组件参数与插槽（C1 只做元数据，标签可用性属 C4）----

    private const string ComponentProbe = """
        using BubuUI;
        namespace Probe;
        public class CardBox : global::BubuUI.Elements.StackPanel
        {
            public string Title { get; set; } = "";
            public int Count { get; set; }
            public Action<int>? CountChanged { get; set; }
            public Action<string>? OnDone { get; set; }
            public string Internal { get; set; } = "";
            [UiIgnore] public string Hidden { get; set; } = "";
            [UiSlot("Header")] public Element? Header { get; set; }
        }
        """;

    [Fact]
    public void CodeControl_IsDiscoveredByItsElementBaseType()
    {
        var def = Scan(ComponentProbe)
            .Elements.Single(e => e.Tag == "CardBox");
        Assert.True(def.IsEmbeddable);
        Assert.Equal("global::BubuUI.Elements.StackPanel", def.BaseTypeDisplayName);
    }

    [Fact]
    public void ComponentParameters_CarryDelegateAndChangedCallback()
    {
        var def = Scan(ComponentProbe)
            .Elements.Single(e => e.Tag == "CardBox");

        Assert.Equal(["Classes", "Count", "CountChanged", "Internal", "IsEnabled", "OnDone", "Title"], def.Parameters.Keys.ToArray());
        Assert.True(def.Parameters["OnDone"].IsDelegate);
        Assert.False(def.Parameters["Title"].IsDelegate);
        // {P}Changed 只在同名参数存在且为委托时回填
        Assert.Equal("CountChanged", def.Parameters["Count"].ChangedCallbackName);
        Assert.Null(def.Parameters["Title"].ChangedCallbackName);
        // 普通 CLR 属性与显式声明共用模板属性表面。
        Assert.True(def.Parameters.ContainsKey("Internal"));
        // [UiIgnore] keeps an ordinary property out of the template surface.
        Assert.False(def.Parameters.ContainsKey("Hidden"));
        Assert.DoesNotContain(def.Problems, p => p.MemberName == "Hidden");
    }

    [Fact]
    public void ComponentSlot_IsCollectedWithNameAndProperty()
    {
        var def = Scan(ComponentProbe)
            .Elements.Single(e => e.Tag == "CardBox");
        Assert.False(def.Parameters.ContainsKey("Header"));
        var slot = def.Slots["Header"];
        Assert.Equal("Header", slot.Name);
        Assert.Equal("Header", slot.PropertyName);
        Assert.Equal(UiSlotCardinality.Single, slot.Cardinality);
    }

    [Fact]
    public void ComponentInitOnlyProperty_IsNotExposed()
    {
        var catalog = Scan("""
            using BubuUI;
            namespace Probe;
            public class InitBox : global::BubuUI.Elements.StackPanel
            {
                protected override void InitializeUi() { }
                public string Title { get; init; } = "";
            }
            """);
        var def = catalog.Elements.Single(e => e.Tag == "InitBox");
        Assert.DoesNotContain("Title", def.Parameters.Keys);
        Assert.DoesNotContain(def.Problems, p => p.MemberName == "Title");
    }

    [Fact]
    public void EmptyCompilationWithoutRuntimeAnchors_YieldsEmptyCatalog()
    {
        var compilation = CSharpCompilation.Create(
            "NoAnchors",
            new[] { CSharpSyntaxTree.ParseText("public class Plain { }") },
            new[] { (MetadataReference)MetadataReference.CreateFromFile(
                typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var catalog = UiCatalogFactory.Build(compilation);
        Assert.Empty(catalog.Elements);
        Assert.Empty(catalog.Problems);
    }

    // ---- 快照 ----

    /// <summary>
    /// 内置控件目录：完整类型清单和公共输入/布局契约；逐成员形状由 golden 文件约束。
    /// 布局契约 9 属性（design/05 §布局契约）随 Element 基类继承到每个标签。
    /// `Element` 的 7 个输入/焦点事件按 §14.4.3 声明在元素基类上，继承到每个标签；
    /// `Button.OnClick` 与 `CheckBox.CheckedChanged` 声明在具体控件上。
    /// 与文本 golden 的分工：这里钉住键的排序与数量，golden 文件钉住每一项的完整形状。
    /// </summary>
    [Fact]
    public void BuiltInSurface_MatchesEnumeratedSnapshot()
    {
        string[] tags = ["Border", "Button", "Calendar", "Canvas", "CheckBox", "ComboBox", "ContentControl", "ContentPresenter", "ContextMenu", "DataGrid", "DataGridColumnHeader", "DatePicker", "DockPanel", "DropDownButton", "Ellipse", "Grid", "Image", "ItemsControl", "ItemsPresenter", "Line", "ListBox", "ListBoxItem", "Menu", "MenuItem", "NumericUpDown", "PasswordBox", "Path", "Popup", "ProgressBar", "RadioButton", "Rectangle", "ScrollViewer", "Slider", "StackPanel", "TabControl", "TabItem", "TemplatedControl", "TextBlock", "TextBox", "TextPresenter", "ToggleButton", "ToggleSwitch", "ToolTip", "TreeView", "TreeViewItem", "VirtualizingStackPanel", "Window", "WrapPanel"];
        var catalog = BuiltIn();
        Assert.Equal(tags.OrderBy(t => t, StringComparer.Ordinal), catalog.Elements.Select(e => e.Tag).OrderBy(t => t, StringComparer.Ordinal));
        Assert.Empty(catalog.Problems);
        foreach (var element in catalog.Elements)
        {
            Assert.Equal(element.Properties.Keys.OrderBy(k => k, StringComparer.Ordinal), element.Properties.Keys);
            if (element.Tag == "Window") continue;
            Assert.Contains("IsVisible", element.Properties.Keys);
            Assert.Contains("OnTextInput", element.Events.Keys);
            Assert.Contains("OnTextComposition", element.Events.Keys);
            Assert.Contains("OnKeyUp", element.Events.Keys);
        }
        var attached = catalog.AttachedProperties.Select(p => $"{p.OwnerTag}.{p.Name}").OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "Canvas.Left", "Canvas.Top", "DockPanel.Dock", "Grid.Column", "Grid.ColumnSpan", "Grid.Row", "Grid.RowSpan" }, attached);
        var row = catalog.FindAttached("Grid", "Row")!;
        Assert.Equal("global::BubuUI.Elements.Grid", row.OwnerClrName);
        Assert.Equal("SetRow", row.SetterMethod);
        Assert.Equal("int", row.TypeShape.CanonicalKey);
        Assert.Equal(UiTypeShapeKind.Primitive, row.TypeShape.Kind);
        Assert.Null(catalog.FindAttached("Grid", "Rows"));
    }

    [Fact]
    public void Scanner_IsDeterministicAndRepeatable()
    {
        var first = BuiltIn();
        var second = BuiltIn();
        Assert.True(first.Equals(second));
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    // ---- 附加属性扫描规则（G4，design/14 §14.5.5）----

    // ProbeCompilation 的 preamble 已带 using System/BubuUI/BubuUI.Elements；
    // 这里只补命名空间（重复 using 同一命名空间会产生 CS0105 噪声）。
    private const string AttachedProbePreamble = "namespace Probe;\n";

    [Fact]
    public void AttachedProperty_ValidDeclaration_EntersCatalog()
    {
        var catalog = Scan(AttachedProbePreamble + """
            public static class DockHost
            {
                public static readonly AttachedProperty<int> DockProperty = new("Dock", typeof(DockHost));
                public static int GetDock(Element element) => DockProperty.GetValue(element);
                public static void SetDock(Element element, int value) => DockProperty.SetValue(element, value);
            }
            """);
        var def = Assert.Single(catalog.AttachedProperties, p => p.OwnerTag == "DockHost");
        Assert.Equal("DockHost", def.OwnerTag);
        Assert.Equal("Dock", def.Name);
        Assert.Equal("global::Probe.DockHost", def.OwnerClrName);
        Assert.Equal("SetDock", def.SetterMethod);
        Assert.Equal("int", def.TypeShape.CanonicalKey);
        Assert.Empty(catalog.Problems);
    }

    [Fact]
    public void AttachedProperty_MissingAccessorPair_ReportsProblemAndStaysOut()
    {
        var catalog = Scan(AttachedProbePreamble + """
            public static class DockHost
            {
                public static readonly AttachedProperty<int> DockProperty = new("Dock", typeof(DockHost));
            }
            """);
        Assert.DoesNotContain(catalog.AttachedProperties, p => p.OwnerTag == "DockHost");
        var problem = Assert.Single(catalog.Problems);
        Assert.Equal(UiDiagnostics.PropertyNotBindable, problem.Code);
        Assert.Contains("GetDock", problem.Message);
        Assert.Contains("SetDock", problem.Message);
    }

    [Fact]
    public void AttachedProperty_AccessorValueTypeMismatch_ReportsProblem()
    {
        var catalog = Scan(AttachedProbePreamble + """
            public static class DockHost
            {
                public static readonly AttachedProperty<int> DockProperty = new("Dock", typeof(DockHost));
                public static int GetDock(Element element) => 0;
                public static void SetDock(Element element, string value) { }
            }
            """);
        Assert.DoesNotContain(catalog.AttachedProperties, p => p.OwnerTag == "DockHost");
        Assert.Contains(catalog.Problems, p => p.Code == UiDiagnostics.PropertyNotBindable
            && p.Message.Contains("'GetDock'/'SetDock' must use the attached property value type int"));
    }

    [Fact]
    public void AttachedProperty_FieldNotNamedXxxProperty_ReportsProblem()
    {
        var catalog = Scan(AttachedProbePreamble + """
            public static class DockHost
            {
                public static readonly AttachedProperty<int> Dock = new("Dock", typeof(DockHost));
                public static int GetDock(Element element) => 0;
                public static void SetDock(Element element, int value) { }
            }
            """);
        Assert.DoesNotContain(catalog.AttachedProperties, p => p.OwnerTag == "DockHost");
        var problem = Assert.Single(catalog.Problems);
        Assert.Equal(UiDiagnostics.PropertyNotBindable, problem.Code);
        Assert.Contains("XxxProperty", problem.Message);
    }

    [Fact]
    public void AttachedProperty_IgnoreOnHostOrField_LeavesSurface()
    {
        var catalog = Scan(AttachedProbePreamble + """
            [UiIgnore]
            public static class IgnoredHost
            {
                public static readonly AttachedProperty<int> DockProperty = new("Dock", typeof(IgnoredHost));
                public static int GetDock(Element element) => 0;
                public static void SetDock(Element element, int value) { }
            }
            public static class FieldIgnoredHost
            {
                [UiIgnore]
                public static readonly AttachedProperty<int> DockProperty = new("Dock", typeof(FieldIgnoredHost));
                public static int GetDock(Element element) => 0;
                public static void SetDock(Element element, int value) { }
            }
            """);
        Assert.DoesNotContain(catalog.AttachedProperties, p => p.OwnerTag is "IgnoredHost" or "FieldIgnoredHost");
        Assert.Empty(catalog.Problems);
    }

    [Fact]
    public void AttachedProperty_NestedHost_IsSkippedSilently()
    {
        var catalog = Scan(AttachedProbePreamble + """
            public static class Outer
            {
                public static class Nested
                {
                    public static readonly AttachedProperty<int> DockProperty = new("Dock", typeof(Nested));
                    public static int GetDock(Element element) => 0;
                    public static void SetDock(Element element, int value) { }
                }
            }
            """);
        Assert.DoesNotContain(catalog.AttachedProperties, p => p.OwnerClrName.StartsWith("global::Probe", StringComparison.Ordinal));
        Assert.Empty(catalog.Problems);
    }

    [Fact]
    public void AttachedProperty_SameOwnerNameConflict_FirstWinsAndReports()
    {
        var catalog = Scan(AttachedProbePreamble + """
            public static class Dock
            {
                public static readonly AttachedProperty<int> ModeProperty = new("Mode", typeof(Dock));
                public static int GetMode(Element element) => 0;
                public static void SetMode(Element element, int value) { }
            }
            namespace Other
            {
                public static class Dock
                {
                    public static readonly AttachedProperty<int> ModeProperty = new("Mode", typeof(Dock));
                    public static int GetMode(Element element) => 0;
                    public static void SetMode(Element element, int value) { }
                }
            }
            """);
        var def = Assert.Single(catalog.AttachedProperties, p => p.OwnerClrName == "global::Probe.Dock");
        Assert.Equal("global::Probe.Dock", def.OwnerClrName);
        Assert.Contains(catalog.Problems, p => p.Code == UiDiagnostics.MetadataDeclarationInvalid
            && p.Message.Contains("already declared by 'global::Probe.Dock'"));
    }

    [Fact]
    public void TagResolution_FoundAmbiguousAndNotFound()
    {
        var catalog = BuiltIn();
        Assert.Equal(UiTagResolutionKind.Found, catalog.Resolve("Button").Kind);

        var ambiguous = UiCatalogFactory.Build(Compile("""
            using BubuUI;
            namespace ProbeA { public class DupBox : StackPanel { } }
            namespace ProbeB { public class DupBox : StackPanel { } }
            """, assemblyName: "AmbiguousProbe"));
        var resolution = ambiguous.Resolve("DupBox");
        Assert.Equal(UiTagResolutionKind.Ambiguous, resolution.Kind);
        Assert.Equal(2, resolution.Candidates?.Count);

        Assert.Equal(UiTagResolutionKind.NotFound, catalog.Resolve("NoSuchTag").Kind);
    }

    [Fact]
    public void HiddenTag_ResolutionReportsUnavailable()
    {
        var catalog = BuiltIn();
        var resolution = catalog.Resolve("Panel");
        Assert.Equal(UiTagResolutionKind.Unavailable, resolution.Kind);
        Assert.NotNull(resolution.Hidden);
    }

    /// <summary>
    /// §14.11.2「冲突：两个程序集同标签」：真正编译出两个独立程序集再同时引用，
    /// 而不是同一源程序集里两个命名空间。
    /// </summary>
    [Fact]
    public void SameTagInTwoReferencedAssemblies_IsAmbiguousWithCandidates()
    {
        var left = CompileAssembly("CrossAsmLeft", "namespace L { public class Badge : BubuUI.Elements.StackPanel { } }");
        var right = CompileAssembly("CrossAsmRight", "namespace R { public class Badge : BubuUI.Elements.StackPanel { } }");

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(RuntimeAssembly.Location),
            MetadataReference.CreateFromFile(left),
            MetadataReference.CreateFromFile(right),
        };
        references.AddRange(((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)));
        var compilation = CSharpCompilation.Create(
            "CrossConsumer",
            new[] { CSharpSyntaxTree.ParseText(string.Empty) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var resolution = UiCatalogFactory.Build(compilation).Resolve("Badge");
        Assert.Equal(UiTagResolutionKind.Ambiguous, resolution.Kind);
        Assert.NotNull(resolution.Candidates);
        Assert.Equal(2, resolution.Candidates!.Count);
        Assert.Contains(resolution.Candidates, c => c.Contains("L.Badge", StringComparison.Ordinal));
        Assert.Contains(resolution.Candidates, c => c.Contains("R.Badge", StringComparison.Ordinal));
    }

    private static string CompileAssembly(string name, string source)
    {
        var compilation = Compile(source, name);
        // 落在测试输出目录：随 bin 一起清理，不污染用户 TEMP
        var path = Path.Combine(AppContext.BaseDirectory, $"{name}_{Guid.NewGuid():N}.dll");
        var emit = compilation.Emit(path);
        Assert.True(emit.Success, string.Join("; ", emit.Diagnostics.Select(d => d.ToString())));
        return path;
    }

    // ---- C4：组件参数/内容/委托的目录派生（design/14 §14.5.2/§14.6、spec/02 §4.12）----

    [Fact]
    public void ComponentParameters_AreDerivedFromPublicWritableProperties()
    {
        var catalog = Scan("""
            using System;
            using BubuUI;

            public partial class Card : global::BubuUI.Elements.StackPanel
            {
                public int Count { get; set; }
                public string? Title { get; set; }
                public string? Hidden { private get; set; }
            }
            """);
        var card = Assert.Single(catalog.Elements, e => e.Tag == "Card");
        Assert.Equal(4, card.Parameters.Count);
        Assert.DoesNotContain("Hidden", card.Parameters.Keys);
        Assert.Equal("global::System.Int32", card.Parameters["Count"].ClrTypeDisplayName);
        Assert.False(card.Parameters["Title"].IsDelegate);
    }

    [Fact]
    public void ComponentDelegateParameter_ShapesAreRecorded()
    {
        var catalog = Scan("""
            using System;
            using BubuUI;

            public partial class Card : global::BubuUI.Elements.StackPanel
            {
                public Action? Closed { get; set; }
                public Action<int>? Picked { get; set; }
            }
            """);
        var card = Assert.Single(catalog.Elements, e => e.Tag == "Card");
        Assert.True(card.Parameters["Closed"].IsDelegate);
        Assert.Equal(0, card.Parameters["Closed"].DelegateArity);
        Assert.True(card.Parameters["Picked"].IsDelegate);
        Assert.Equal(1, card.Parameters["Picked"].DelegateArity);
    }

    [Fact]
    public void ComponentConventionTwoway_LinksParameterWithChangedCallback()
    {
        var catalog = Scan("""
            using System;
            using BubuUI;

            public partial class Card : global::BubuUI.Elements.StackPanel
            {
                public string? Color { get; set; }
                public Action<string?>? ColorChanged { get; set; }
            }
            """);
        var card = Assert.Single(catalog.Elements, e => e.Tag == "Card");
        Assert.Equal("ColorChanged", card.Parameters["Color"].ChangedCallbackName);
        Assert.Null(card.Parameters["ColorChanged"].ChangedCallbackName);
    }

    [Fact]
    public void ComponentParameter_InvalidDelegateShape_IsEUI028()
    {
        var catalog = Scan("""
            using System;
            using BubuUI;

            public partial class Card : global::BubuUI.Elements.StackPanel
            {
                public Func<int>? Compute { get; set; }
                public ActionRef? Weird { get; set; }
            }
            public delegate void ActionRef(ref int value);
            """);
        var card = Assert.Single(catalog.Elements, e => e.Tag == "Card");
        var problems = card.Problems.Where(p => p.Code == UiDiagnostics.ParameterDeclarationInvalid).ToList();
        Assert.Single(problems);
        Assert.False(card.Parameters["Compute"].IsDelegate);
        Assert.Contains(problems, p => p.MemberName == "Weird" && p.Message.Contains("ref"));
    }

    [Fact]
    public void ComponentParameter_ForbiddenType_IsEUI028()
    {
        var catalog = Scan("""
            using System;
            using BubuUI;

            public partial class Card : global::BubuUI.Elements.StackPanel
            {
                public Span<int> Slice { get; set; }
            }
            """);
        var card = Assert.Single(catalog.Elements, e => e.Tag == "Card");
        var problem = Assert.Single(card.Problems, p => p.Code == UiDiagnostics.ParameterDeclarationInvalid);
        Assert.Equal("Slice", problem.MemberName);
        Assert.Contains("Span", problem.Message);
    }

    [Fact]
    public void ComponentContentParameter_TypeAndCardinality()
    {
        var catalog = Scan("""
            using System;
            using System.Collections.Generic;
            using BubuUI;

            public partial class Card : global::BubuUI.Elements.StackPanel
            {
                [UiContent]
                public Element? Body { get; set; }
            }
            public partial class List : global::BubuUI.Elements.StackPanel
            {
                [UiContent]
                public IReadOnlyList<Element> Items { get; set; }
            }
            """);
        var card = Assert.Single(catalog.Elements, e => e.Tag == "Card");
        Assert.True(card.Parameters["Body"].IsContent);
        Assert.False(card.Parameters["Body"].ContentIsMultiple);
        var list = Assert.Single(catalog.Elements, e => e.Tag == "List");
        Assert.True(list.Parameters["Items"].IsContent);
        Assert.True(list.Parameters["Items"].ContentIsMultiple);
    }

    [Fact]
    public void ComponentContentParameter_WrongType_IsEUI021()
    {
        var catalog = Scan("""
            using System;
            using BubuUI;

            public partial class Card : global::BubuUI.Elements.StackPanel
            {
                [UiContent]
                public string? Body { get; set; }
            }
            """);
        var card = Assert.Single(catalog.Elements, e => e.Tag == "Card");
        var problem = Assert.Single(card.Problems, p => p.Code == UiDiagnostics.ContentTargetInvalid);
        Assert.Equal("Body", problem.MemberName);
    }

    [Fact]
    public void ControlClassLevelUiContent_UsesDeclaredContentMember()
    {
        var catalog = Scan("""
            using System;
            using System.Collections.Generic;
            using BubuUI;

            [UiContent]
            public partial class Card : global::BubuUI.Elements.StackPanel
            {
                [UiContent]
                public Element? Body { get; set; }
            }
            """);
        var card = Assert.Single(catalog.Elements, e => e.Tag == "Card");
        Assert.Equal(UiContentModel.Children, card.Content);
        Assert.Equal("Children", card.ContentTarget);
        Assert.DoesNotContain(card.Problems, p => p.Message.Contains("on a component type is invalid"));
    }

    [Fact]
    public void Component_MoreThanOneContentParameter_IsRejected()
    {
        var catalog = Scan("""
            using System;
            using System.Collections.Generic;
            using BubuUI;

            public partial class Card : global::BubuUI.Elements.StackPanel
            {
                [UiContent]
                public Element? Body { get; set; }
                [UiContent]
                public IReadOnlyList<Element> Extra { get; set; }
            }
            """);
        var card = Assert.Single(catalog.Elements, e => e.Tag == "Card");
        Assert.Contains(card.Problems,
            p => p.Code == UiDiagnostics.MetadataDeclarationInvalid && p.Message.Contains("more than one"));
    }

    [Fact]
    public void ComponentInheritsParameters_AndChangedLinkFromBaseType()
    {
        var catalog = Scan("""
            using System;
            using BubuUI;

            public abstract class Base : global::BubuUI.Elements.StackPanel
            {
                public string? Name { get; set; }
                public Action<string?>? NameChanged { get; set; }
            }
            public partial class Card : Base
            {
                public int Count { get; set; }
            }
            """);
        var card = Assert.Single(catalog.Elements, e => e.Tag == "Card");
        Assert.Equal(5, card.Parameters.Count);
        Assert.Equal("NameChanged", card.Parameters["Name"].ChangedCallbackName);
    }

    // ---- C6：插槽声明侧（design/14 §14.6.3、spec/01 §4.15、spec/02 §4.12.5）----

    [Fact]
    public void ComponentSlot_InheritedFromBase_IsCollected()
    {
        var catalog = Scan("""
            using System.Collections.Generic;
            using BubuUI;

            public abstract class Base : global::BubuUI.Elements.StackPanel
            {
                [UiSlot("Rows")] public IReadOnlyList<Element> Rows { get; set; } = [];
            }
            public partial class Card : Base
            {
                [UiSlot("Header")] public Element? Header { get; set; }
            }
            """);
        var card = Assert.Single(catalog.Elements, e => e.Tag == "Card");
        Assert.Equal(2, card.Slots.Count);
        Assert.Equal(UiSlotCardinality.Multiple, card.Slots["Rows"].Cardinality);
        Assert.Equal(UiSlotCardinality.Single, card.Slots["Header"].Cardinality);
        Assert.Empty(card.Problems);
    }

    [Fact]
    public void ComponentSlot_OverrideOnDerived_CountsOnce_AndDerivedDeclarationWins()
    {
        // 基链必须自己走（GetAttributes 不含继承特性），但 override 不是第二个插槽：
        // 派生声明胜出，不得报重复。
        var catalog = Scan("""
            using BubuUI;

            public class Base : global::BubuUI.Elements.StackPanel
            {
                [UiSlot("Header")] public virtual Element? Header { get; set; }
            }
            public partial class Card : Base
            {
                [UiSlot("Header")] public override Element? Header { get; set; }
            }
            """);
        var card = Assert.Single(catalog.Elements, e => e.Tag == "Card");
        var slot = Assert.Single(card.Slots.Values);
        Assert.Equal(UiSlotCardinality.Single, slot.Cardinality);
        Assert.DoesNotContain(card.Problems, p => p.Message.Contains("already declared"));
    }

    [Fact]
    public void ComponentSlot_OverrideWithoutAttribute_KeepsInheritedDeclaration()
    {
        // 同名成员去重后，派生侧符号是唯一的读取点：Roslyn 必须把基类特性带进 override，
        // 否则 override 会把插槽静默丢掉（与反射 Inherited=true 的语义一致才可接受）。
        var catalog = Scan("""
            using BubuUI;

            public class Base : global::BubuUI.Elements.StackPanel
            {
                [UiSlot("Header")] public virtual Element? Header { get; set; }
            }
            public partial class Card : Base
            {
                public override Element? Header { get; set; }
            }
            """);
        var card = Assert.Single(catalog.Elements, e => e.Tag == "Card");
        var slot = Assert.Single(card.Slots.Values);
        Assert.Equal("Header", slot.Name);
        Assert.Equal(UiSlotCardinality.Single, slot.Cardinality);
    }

    [Fact]
    public void ComponentSlot_IgnoreOnOverride_ExitsInheritedSlot()
    {
        // [UiIgnore] 是派生侧对同名声明的显式退出：基类的插槽不再进表面（design/14 §14.6.1）。
        var catalog = Scan("""
            using BubuUI;

            public class Base : global::BubuUI.Elements.StackPanel
            {
                [UiSlot("Header")] public virtual Element? Header { get; set; }
            }
            public partial class Card : Base
            {
                [UiIgnore] public override Element? Header { get; set; }
            }
            """);
        var card = Assert.Single(catalog.Elements, e => e.Tag == "Card");
        Assert.Empty(card.Slots);
    }

    [Theory]
    [InlineData("string? Header", "string")]
    [InlineData("List<Element> Header", "System.Collections.Generic.List")]
    [InlineData("Element[] Header", "Element[]")]
    public void ComponentSlot_TypeNotElementOrElementList_IsEUI021_AndLeavesSurface(string declaration, string typeFragment)
    {
        var catalog = Scan($$"""
            using System.Collections.Generic;
            using BubuUI;

            public partial class Card : global::BubuUI.Elements.StackPanel
            {
                [UiSlot("Header")] public {{declaration}} { get; set; }
            }
            """);
        var card = Assert.Single(catalog.Elements, e => e.Tag == "Card");
        Assert.Empty(card.Slots);
        var problem = Assert.Single(card.Problems, p => p.Code == UiDiagnostics.ContentTargetInvalid);
        Assert.Contains("Header", problem.Message);
        Assert.Contains(typeFragment, problem.Message);
    }

    [Fact]
    public void ComponentSlot_DuplicateName_IsEUI028()
    {
        var catalog = Scan("""
            using BubuUI;

            public partial class Card : global::BubuUI.Elements.StackPanel
            {
                [UiSlot("Header")] public Element? First { get; set; }
                [UiSlot("Header")] public Element? Second { get; set; }
            }
            """);
        var card = Assert.Single(catalog.Elements, e => e.Tag == "Card");
        var slot = Assert.Single(card.Slots.Values);
        Assert.Equal("First", slot.PropertyName);
        var problem = Assert.Single(card.Problems, p => p.Code == UiDiagnostics.ParameterDeclarationInvalid);
        Assert.Equal("Second", problem.MemberName);
        Assert.Contains("already declared", problem.Message);
    }

    [Theory]
    [InlineData("[UiSlot(\"\")]")]          // 空名：AttributeArgument 存在但值为空串
    [InlineData("[UiSlot(null)]")]          // null 名：构造参数为 null
    public void ComponentSlot_WithoutUsableName_IsEUI028(string attribute)
    {
        // UiSlotAttribute 只有 (string name) 一个构造，缺实参是 C# 错误；可达的非法形态是空串/null。
        var catalog = Scan($$"""
            using BubuUI;

            public partial class Card : global::BubuUI.Elements.StackPanel
            {
                {{attribute}} public Element? Header { get; set; }
            }
            """);
        var card = Assert.Single(catalog.Elements, e => e.Tag == "Card");
        Assert.Empty(card.Slots);
        var problem = Assert.Single(card.Problems, p => p.Code == UiDiagnostics.ParameterDeclarationInvalid);
        Assert.Contains("must name the slot", problem.Message);
    }

    [Fact]
    public void ComponentSlot_IsNotAlsoAnOrdinaryParameter()
    {
        // A writable slot remains a slot, rather than becoming an ordinary value parameter.
        var catalog = Scan("""
            using BubuUI;

            public partial class Card : global::BubuUI.Elements.StackPanel
            {
                [UiSlot("Header")] public Element? Header { get; set; }
            }
            """);
        var card = Assert.Single(catalog.Elements, e => e.Tag == "Card");
        Assert.False(card.Parameters.ContainsKey("Header"));
        Assert.Equal("Header", Assert.Single(card.Slots.Values).PropertyName);
        Assert.Empty(card.Problems);
    }

    [Fact]
    public void ComponentSlot_WithUiIgnore_IgnoreWinsAndMemberLeaves_IsEUI028()
    {
        var catalog = Scan("""
            using BubuUI;

            public partial class Card : global::BubuUI.Elements.StackPanel
            {
                [UiIgnore] [UiSlot("Header")] public Element? Header { get; set; }
            }
            """);
        var card = Assert.Single(catalog.Elements, e => e.Tag == "Card");
        Assert.Empty(card.Slots);
        Assert.False(card.Parameters.ContainsKey("Header"));
        Assert.Contains(card.Problems, p => p.Code == UiDiagnostics.ParameterDeclarationInvalid &&
            p.Message.Contains("[UiIgnore] wins"));
    }

    [Theory]
    [InlineData("Slot")]
    [InlineData("ContentSlot")]
    public void ControlRegisteringReservedSlotTag_IsHiddenWithEUI031(string tag)
    {
        // spec/01 §3.1：`<Slot>`/`<ContentSlot>` 是解析器特判的句法保留名，同名控件挡在面板外。
        var catalog = Scan($$"""
            using BubuUI;

            public class {{tag}} : StackPanel { }
            """);
        Assert.DoesNotContain(catalog.Elements, e => e.Tag == tag);
        var hidden = Assert.Single(catalog.HiddenTags, h => h.Tag == tag);
        Assert.Equal(UiDiagnostics.StyleAttributeInvalid, hidden.ReasonCode);
        Assert.Contains("reserved template tag", hidden.Reason);
        Assert.Contains(catalog.Problems, p => p.Code == UiDiagnostics.StyleAttributeInvalid &&
            p.Message.Contains("rename the type"));
    }
}
