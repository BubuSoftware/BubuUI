using BubuUI.Compiler;
using BubuUI.Compiler.Metadata;
using BubuUI.LanguageServer.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace BubuUI.Ide.Tests;

/// <summary>
/// K37 <c>@inject</c> 的 IDE 支持（spec/02 §8）：指令关键字补全与悬停、双参数位分流——
/// 类型名参数给项目符号中的非泛型可访问类型与命名空间（不限 UI 目录）、属性名参数无候选。
/// </summary>
public class InjectDirectiveCompletionTests
{
    private static readonly Lazy<Compilation> ServicesCompilation = new(() =>
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Append(typeof(BubuUI.Window).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static path => MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create("InjectServices", new[]
        {
            CSharpSyntaxTree.ParseText("""
                namespace Plain.Models { public class Options { } }
                namespace Plain.Services
                {
                    public interface IChannel { }
                    public class Channel : IChannel { }
                    public class GenericService<T> { }
                }
                """),
        }, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    });

    private static readonly Lazy<UiCatalog> ServicesCatalog =
        new(() => UiCatalogFactory.Build(ServicesCompilation.Value));

    private static (DocumentAnalysis Analysis, int Offset) Analyze(string marked)
    {
        var offset = marked.IndexOf('|');
        Assert.True(offset >= 0);
        var text = marked.Remove(offset, 1);
        var analysis = DocumentAnalysis.Create(text, "InjectProbe.bui", ServicesCatalog.Value,
            ServicesCompilation.Value);
        return (analysis, offset);
    }

    private static UiCompletionItem Item(UiCompletionList list, string label)
        => Assert.Single(list.Items, item => item.Label == label);

    // ---- 光标扫描：双参数位 ----

    [Fact]
    public void Scanner_SplitsInjectArguments()
    {
        var onType = CursorScanner.Scan("@inject IC", "@inject IC".Length);
        Assert.Equal(CursorKind.DirectiveArgument, onType.Kind);
        Assert.Equal("inject", onType.Directive);
        Assert.Equal(0, onType.DirectiveArgIndex);
        Assert.Equal("IC", onType.Prefix);

        const string full = "@inject ICache Cache";
        var onName = CursorScanner.Scan(full, full.Length);
        Assert.Equal(1, onName.DirectiveArgIndex);
        Assert.Equal("Cache", onName.Prefix);
    }

    [Fact]
    public void Scanner_KeepsQualifiedTypeNameInOneToken()
    {
        const string full = "@inject Plain.Services.IChannel";
        var context = CursorScanner.Scan(full, full.Length);
        Assert.Equal(0, context.DirectiveArgIndex);
        Assert.Equal("Plain.Services.IChannel", context.Prefix);
    }

    // ---- 指令关键字补全与悬停 ----

    [Fact]
    public void DirectiveCompletion_OffersInject()
    {
        var (analysis, offset) = Analyze("@namespace App\n@inj|");
        var list = CompletionProvider.Complete(analysis, ServicesCatalog.Value, offset);

        var item = Item(list, "@inject");
        Assert.Equal(UiCompletionKind.Directive, item.Kind);
    }

    [Fact]
    public void DirectiveHover_DocumentsInject()
    {
        var (analysis, offset) = Analyze("@namespace App\n@inject|");
        var hover = HoverProvider.Hover(analysis, ServicesCatalog.Value, offset);

        Assert.NotNull(hover);
        Assert.Contains("@inject", hover!.Markdown);
        Assert.Contains("K37", hover.Markdown);
    }

    // ---- 参数①：服务类型名 ----

    [Fact]
    public void TypeNameArgument_OffersProjectTypesIncludingNonUiServices()
    {
        var (analysis, offset) = Analyze(
            "@namespace App\n@using Plain.Models\n@using Plain.Services\n@inject |");
        var list = CompletionProvider.Complete(analysis, ServicesCatalog.Value, offset);

        // 服务不限 UI 目录：纯 POCO / 接口也是候选；命名空间同时在列（限定名输入中段有用）
        Assert.Equal(UiCompletionKind.Class, Item(list, "Plain.Models.Options").Kind);
        Assert.Equal(UiCompletionKind.Class, Item(list, "Plain.Services.IChannel").Kind);
        Assert.Contains(list.Items, i => i.Label == "Plain.Services.Channel");
        Assert.Contains(list.Items, i => i.Kind == UiCompletionKind.Namespace && i.Label == "Plain.Services");
    }

    [Fact]
    public void TypeNameArgument_DottedContainer_CompletesMembers()
    {
        var (analysis, offset) = Analyze("@namespace App\n@inject Plain.Services.|");
        var list = CompletionProvider.Complete(analysis, ServicesCatalog.Value, offset);

        Assert.Contains(list.Items, i => i.Label == "Plain.Services.IChannel");
        Assert.DoesNotContain(list.Items, i => i.Label == "Plain.Models.Options");
    }

    [Fact]
    public void TypeNameArgument_ExcludesGenericTypes()
    {
        var (analysis, offset) = Analyze(
            "@namespace App\n@using Plain.Services\n@inject Gen|");
        var list = CompletionProvider.Complete(analysis, ServicesCatalog.Value, offset);

        Assert.DoesNotContain(list.Items, i => i.Label.Contains("GenericService"));
    }

    [Fact]
    public void TypeNameArgument_ReplacesTheWholeCurrentTokenOnly()
    {
        var (analysis, offset) = Analyze(
            "@namespace App\n@using Plain.Services\n@inject Plain.Services.Ch| Cache");
        var list = CompletionProvider.Complete(analysis, ServicesCatalog.Value, offset);

        // 限定名整 token 替换成规范写法（与 @using 别名目标同口径），但不吞掉后面的属性名 Cache
        Assert.Contains(list.Items, i => i.Label == "Plain.Services.Channel");
        Assert.Equal(offset - "Plain.Services.Ch".Length, list.Replacement.Start);
        Assert.Equal("Plain.Services.Ch".Length, list.Replacement.Length);
    }

    // ---- 参数②：属性名（自由标识符）----

    [Fact]
    public void PropertyNameArgument_HasNoCandidates()
    {
        var (analysis, offset) = Analyze("@namespace App\n@inject Plain.Services.Channel |");
        var list = CompletionProvider.Complete(analysis, ServicesCatalog.Value, offset);

        Assert.Empty(list.Items);
    }
}
