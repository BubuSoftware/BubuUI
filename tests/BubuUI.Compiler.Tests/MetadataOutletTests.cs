using Path = System.IO.Path;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BubuUI;
using BubuUI.Elements;
using BubuUI.Compiler;
using BubuUI.Compiler.Generator;
using BubuUI.Compiler.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace BubuUI.Compiler.Tests;

/// <summary>
/// D74 ②（design/14 §14.5.4/§14.9.1/§14.9.2）：目录侧元数据问题的编译期出口。
/// ① 源项目符号的声明错误报在 C# 声明处，引用程序集仍只在使用点暴露；
/// ② 标签解析失败按类别分流到 EUI016/EUI017/EUI018/EUI030，只有真正未知才落 EUI001。
/// EUI024（事件处理器签名匹配）不在本片，仍是 G3 余项。
/// </summary>
public class MetadataOutletTests
{
    private const string UiPath = "/templates/Probe.bui";
    private const string CleanCodeBehind = "public partial class Probe { }";

    private sealed class UiAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public UiAdditionalText(string path, string content)
        {
            Path = path;
            _text = SourceText.From(NamespaceDeclarationFixture.Ensure(path, content));
        }

        public override string Path { get; }

        public override SourceText? GetText(CancellationToken cancellationToken = default) => _text;
    }

    private static CSharpCompilation CreateCompilation(string source, string? referencedLibrary = null)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(UiObject).Assembly.Location));
        if (referencedLibrary is not null)
            references.Add(MetadataReference.CreateFromFile(referencedLibrary));
        // 带 path 的语法树：声明处诊断必须能定位到具体的 C# 行列。
        return CSharpCompilation.Create(
            "TemplateProbe",
            new[] { CSharpSyntaxTree.ParseText(source, path: "Probe.cs") },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static GeneratorRunResult Run(string codeBehind, string ui = "@using BubuUI.Elements\n<StackPanel />",
        string? referencedLibrary = null, string[]? extraUiPaths = null)
    {
        var fixture = NamespaceDeclarationFixture.Prepare(UiPath, codeBehind);
        var files = new List<AdditionalText> { new UiAdditionalText(UiPath, fixture.Ui(ui)) };
        if (extraUiPaths is not null)
            foreach (var path in extraUiPaths)
                files.Add(new UiAdditionalText(path, ui));
        var driver = CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() }, files.ToArray());
        return driver.RunGenerators(CreateCompilation(fixture.CodeBehind, referencedLibrary))
            .GetRunResult().Results.Single();
    }

    private static string EmitLibrary(string name, string source)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"{name}_{Guid.NewGuid():N}.dll");
        var emit = CreateCompilation(source).Emit(path);
        Assert.True(emit.Success, string.Join("; ", emit.Diagnostics
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(d => d.ToString())));
        return path;
    }

    private static int LineOf(string source, string fragment)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var index = Array.FindIndex(lines, l => l.Contains(fragment, StringComparison.Ordinal));
        Assert.True(index >= 0, $"fragment '{fragment}' not found in source");
        return index;
    }

    // ---- ① 声明处出口 ----

    [Fact]
    public void CleanCatalog_ReportsNoMetadataDiagnostics()
    {
        var result = Run(CleanCodeBehind);

        Assert.Empty(result.Diagnostics);
        Assert.Single(result.GeneratedSources);
    }

    [Fact]
    public void SourceProject_StaticEventHost_ReportsEUI026AtTypeDeclaration()
    {
        const string source = """
using BubuUI;
using BubuUI.Elements;
public partial class Probe { }
public static class ProbeEventHost
{
public static readonly RoutedEvent FooEvent = new RoutedEvent("Foo", RoutingStrategy.Bubble);
}
""";

        var result = Run(source);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI026");
        var line = diag.Location.GetLineSpan();

        Assert.Equal("Probe.cs", line.Path);
        Assert.Equal(LineOf(source, "class ProbeEventHost"), line.StartLinePosition.Line);
        Assert.Contains("ProbeEventHost", diag.GetMessage());
        Assert.Single(result.GeneratedSources);   // 声明处诊断不阻断生成输出
    }

    [Fact]
    public void SourceProject_ReservedProperty_ReportsEUI026AtMemberDeclaration()
    {
        const string source = """
using BubuUI;
using BubuUI.Elements;
public partial class Probe { }
public class KeyBox : StackPanel
{
public static readonly StyledProperty<string> KeyProperty = StyledProperty<string>.Register<KeyBox>("Key", "");
public string Key { get => ""; set { } }
}
""";

        var result = Run(source);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI026");
        var line = diag.Location.GetLineSpan();
        var sourceLine = source.Replace("\r\n", "\n").Split('\n')[line.StartLinePosition.Line];

        Assert.Equal("Probe.cs", line.Path);
        Assert.Contains("public string Key", sourceLine);
        Assert.Equal(sourceLine.IndexOf("Key {", StringComparison.Ordinal), line.StartLinePosition.Character);
    }

    [Fact]
    public void SourceProject_InvalidComponentParameter_ReportsEUI028AtMemberDeclaration()
    {
        const string source = """
using BubuUI;
using BubuUI.Elements;
public partial class Probe { }
public sealed class BadCard : global::BubuUI.Elements.StackPanel
{
[UiParameter] public int Count { get; }
}
""";

        var result = Run(source);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI028");
        var line = diag.Location.GetLineSpan();
        var sourceLine = source.Replace("\r\n", "\n").Split('\n')[line.StartLinePosition.Line];

        Assert.Equal("Probe.cs", line.Path);
        Assert.Contains("[UiParameter] public int Count", sourceLine);
        Assert.Equal(sourceLine.IndexOf("Count {", StringComparison.Ordinal), line.StartLinePosition.Character);
    }

    [Fact]
    public void SourceProject_PropertyMetadataError_IsFoldedIntoEUI026()
    {
        // design/14 §14.9.2：源项目控件的元数据错误归 EUI026；EUI019 只对引用程序集的使用点成立。
        const string source = """
using BubuUI;
using BubuUI.Elements;
public partial class Probe { }
public class BrokenBox : StackPanel
{
public double Size { get => 0; set { } }
public static readonly StyledProperty<int> SizeProperty = StyledProperty<int>.Register<BrokenBox>("Size", 0);
}
""";

        var result = Run(source);

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI026");
        Assert.Contains("SizeProperty", diag.GetMessage());
        Assert.Equal("Probe.cs", diag.Location.GetLineSpan().Path);
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI019");
    }

    [Fact]
    public void MultipleUiFiles_ReportEachMetadataProblemExactlyOnce()
    {
        const string source = """
using BubuUI;
using BubuUI.Elements;
public partial class Probe { }
public static class ProbeEventHost
{
public static readonly RoutedEvent FooEvent = new RoutedEvent("Foo", RoutingStrategy.Bubble);
}
""";

        // 两个 .bui 都叫 Probe（不同目录），逐文件管道跑两次；目录级出口只跑一次。
        var result = Run(source, extraUiPaths: new[] { "/templates/nested/Probe.bui" });

        Assert.Equal(2, result.GeneratedSources.Length);
        Assert.Single(result.Diagnostics, d => d.Id == "EUI026");
    }

    [Fact]
    public void ReferencedAssembly_Problems_StayAtUseSiteOnly()
    {
        var lib = EmitLibrary("MetadataOutletLib", """
using BubuUI;
using BubuUI.Elements;
namespace ProbeLib;
public class BrokenBox : StackPanel
{
public double Size { get => 0; set { } }
public static readonly StyledProperty<int> SizeProperty = StyledProperty<int>.Register<BrokenBox>("Size", 0);
}
""");

        var result = Run(CleanCodeBehind, "@using ProbeLib\n@using BubuUI.Elements\n<BrokenBox Size=\"4\" />", referencedLibrary: lib);

        Assert.Contains(result.Diagnostics, d => d.Id == "EUI019");       // D74 ① 使用点
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI026"); // 无源声明可定位
    }

    [Fact]
    public void SourceLocations_DoNotBreakCatalogValueEquality()
    {
        const string source = """
using BubuUI;
using BubuUI.Elements;
public class KeyBox : StackPanel
{
public static readonly StyledProperty<string> KeyProperty = StyledProperty<string>.Register<KeyBox>("Key", "");
public string Key { get => ""; set { } }
}
""";

        var first = UiCatalogFactory.Build(CreateCompilation(source));
        var second = UiCatalogFactory.Build(CreateCompilation(source));

        Assert.True(first.Equals(second));
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Contains(first.Elements.SelectMany(e => e.Problems), p => p.Source is not null);
    }

    // ---- ② 标签使用点码分流 ----

    [Fact]
    public void AmbiguousTag_ReportsEUI016()
    {
        // `[UiTag]` 退役后，重名只能来自「不同命名空间的同名类型」——第三方库撞名的主场景。
        const string source = """
using BubuUI;
using BubuUI.Elements;
public partial class Probe { }
namespace A { public class DupBox : StackPanel { } }
namespace B { public class DupBox : StackPanel { } }
""";

        var result = Run(source, "@using A\n@using B\n<DupBox />");

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI016");
        Assert.Contains("A.DupBox", diag.GetMessage());
        Assert.Contains("B.DupBox", diag.GetMessage());
        Assert.Contains("@using DupBoxAlt = A.DupBox", diag.GetMessage());
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI001");
    }

    [Theory]
    [InlineData("public class Outer : StackPanel { public class Inner : StackPanel { } }", "Inner", "EUI017")]
    [InlineData("public abstract class AbstractBox : StackPanel { }", "AbstractBox", "EUI017")]
    [InlineData("public class NoCtorBox : StackPanel { public NoCtorBox(int x) { } }", "NoCtorBox", "EUI018")]
    [InlineData("public class GenericBox<T> : StackPanel { }", "GenericBox", "EUI030")]
    public void UnavailableTag_ReportsItsReasonCodeAtUseSite(string declaration, string tag, string expectedCode)
    {
        var result = Run($"using BubuUI; using BubuUI.Elements; {CleanCodeBehind} {declaration}", $"<{tag} />");

        Assert.Contains(result.Diagnostics, d => d.Id == expectedCode);
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI001");
    }

    [Fact]
    public void UnknownTag_StillReportsEUI001()
    {
        var result = Run(CleanCodeBehind, "@using BubuUI.Elements\n<NoSuchTag />");

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI001");
        Assert.Contains("NoSuchTag", diag.GetMessage());
    }
}
