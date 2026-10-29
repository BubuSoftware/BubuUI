using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using MsTextSpan = Microsoft.CodeAnalysis.Text.TextSpan;
using UiSpan = EclipseUI.Compiler.TextSpan;
using CompilerSeverity = EclipseUI.Compiler.DiagnosticSeverity;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace EclipseUI.Compiler.Generator;

/// <summary>
/// .ui → C# 增量源生成器：编译期类型检查（表达式原样进入生成代码，由 Roslyn 检查并经
/// #line 映射回 .ui）、code-behind 信号发现、@foreach item 类型推断、定向更新代码发射。
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class TemplateGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var uiFiles = context.AdditionalTextsProvider
            .Where(static file => Path.GetExtension(file.Path) is ".ui");
        var pipeline = uiFiles.Collect().Combine(context.CompilationProvider);
        context.RegisterSourceOutput(pipeline, static (spc, data) => Execute(spc, data.Left, data.Right));
    }

    private static void Execute(SourceProductionContext spc, ImmutableArray<AdditionalText> files, Compilation compilation)
    {
        var catalog = UiCatalog.CreateDefault();

        foreach (var file in files)
        {
            var sourceText = file.GetText();
            if (sourceText is null) continue;

            var className = Path.GetFileNameWithoutExtension(file.Path);
            var diags = new List<UiDiagnostic>();

            if (!SyntaxFacts.IsValidIdentifier(className))
            {
                Report(spc, file.Path, sourceText, UiDiagnostics.Error(UiDiagnostics.InvalidClassName,
                    $"'{className}' is not a valid class name; rename the .ui file", UiSpan.Zero));
                continue;
            }

            var doc = FrontEnd.Parse(sourceText.ToString(), file.Path, diags);
            FrontEnd.Analyze(doc, catalog, diags);

            // code-behind 发现：与 .ui 同名的 partial class
            var type = FindCodeBehind(compilation, className);
            if (type is null)
            {
                diags.Add(UiDiagnostics.Error(UiDiagnostics.CodeBehindMissing,
                    $"no code-behind class '{className}' found; add a partial class with that name", UiSpan.Zero));
            }
            else
            {
                if (type.ContainingType is not null)
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.CodeBehindNested,
                        $"code-behind class '{className}' must be top-level", UiSpan.Zero));
                else if (!IsPartial(type))
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.CodeBehindNotPartial,
                        $"code-behind class '{className}' must be declared partial", UiSpan.Zero));
            }

            // signals：code-behind 中 Signal<T> 类型的字段/属性
            var signals = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            if (type is not null)
            {
                var signalGeneric = compilation.GetTypeByMetadataName("EclipseUI.Signal`1");
                if (signalGeneric is not null)
                {
                    foreach (var member in type.GetMembers())
                    {
                        var memberType = member switch
                        {
                            IFieldSymbol field => field.Type,
                            IPropertySymbol property => property.Type,
                            _ => null,
                        };
                        if (memberType is INamedTypeSymbol named &&
                            SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, signalGeneric))
                            signals[member.Name] = named.TypeArguments[0];
                    }
                }
            }

            // @foreach item 类型推断
            var itemTypes = new Dictionary<UiForeach, string>();
            foreach (var foreachNode in EnumerateNodes(doc.Nodes).OfType<UiForeach>())
            {
                var name = foreachNode.Collection.Trim();
                if (signals.TryGetValue(name, out var collectionType))
                {
                    var itemType = ElementTypeOf(collectionType, compilation);
                    if (itemType is null)
                        diags.Add(UiDiagnostics.Error(UiDiagnostics.ForEachItemTypeUnknown,
                            $"cannot infer the item type of '{foreachNode.Collection}'; use Signal<List<T>>, Signal<T[]> or Signal<IEnumerable<T>>",
                            foreachNode.CollectionSpan));
                    else
                        itemTypes[foreachNode] = itemType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                }
                else
                {
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.ForEachItemTypeUnknown,
                        $"cannot infer the item type of '{foreachNode.Collection}'; it must be a Signal<T> field or property in the code-behind",
                        foreachNode.CollectionSpan));
                }
            }

            // item 模板内的 signal 引用 / 双向绑定 → 暂不支持
            foreach (var foreachNode in EnumerateNodes(doc.Nodes).OfType<UiForeach>())
                CheckItemTemplate(foreachNode, signals, diags);

            bool hasErrors = false;
            foreach (var diag in diags)
            {
                Report(spc, file.Path, sourceText, diag);
                if (diag.IsError) hasErrors = true;
            }
            if (hasErrors || type is null) continue;

            var code = new Emitter(doc,
                AccessibilityOf(type), NamespaceName(type), className,
                itemTypes, signals.Keys.ToArray(), file.Path).Emit();

            spc.AddSource($"{className}.{StableHash(file.Path)}.g.cs", code);
        }
    }

    private static string StableHash(string path)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(path));
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    private static INamedTypeSymbol? FindCodeBehind(Compilation compilation, string name)
    {
        foreach (var symbol in compilation.GetSymbolsWithName(name, SymbolFilter.Type))
            if (symbol is INamedTypeSymbol type && type.TypeKind == TypeKind.Class)
                return type;
        return null;
    }

    private static bool IsPartial(INamedTypeSymbol type)
    {
        foreach (var reference in type.DeclaringSyntaxReferences)
            if (reference.GetSyntax() is TypeDeclarationSyntax declaration &&
                declaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
                return true;
        return false;
    }

    private static string AccessibilityOf(INamedTypeSymbol type) =>
        type.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";

    private static string NamespaceName(INamedTypeSymbol type) =>
        type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();

    private static ITypeSymbol? ElementTypeOf(ITypeSymbol type, Compilation compilation)
    {
        if (type is IArrayTypeSymbol array) return array.ElementType;
        var enumerable = compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1");
        if (enumerable is null) return null;
        if (type is INamedTypeSymbol named &&
            SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, enumerable))
            return named.TypeArguments[0];
        foreach (var iface in type.AllInterfaces)
            if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, enumerable))
                return iface.TypeArguments[0];
        return null;
    }

    private static IEnumerable<UiNode> EnumerateNodes(IEnumerable<UiNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            switch (node)
            {
                case UiElement element:
                    foreach (var n in EnumerateNodes(element.Children)) yield return n;
                    break;
                case UiIf ifNode:
                    foreach (var n in EnumerateNodes(ifNode.Children)) yield return n;
                    break;
                case UiForeach foreachNode:
                    foreach (var n in EnumerateNodes(foreachNode.Children)) yield return n;
                    break;
            }
        }
    }

    private static void CheckItemTemplate(UiForeach foreachNode, Dictionary<string, ITypeSymbol> signals, List<UiDiagnostic> diags)
    {
        foreach (var node in foreachNode.Children)
        {
            switch (node)
            {
                case UiElement element:
                    foreach (var attr in element.Attributes)
                    {
                        if (attr.Name == "Key") continue;
                        if (attr.IsBind)
                            diags.Add(UiDiagnostics.Error(UiDiagnostics.SignalInItemTemplate,
                                "two-way binding (:bind) inside an @foreach template is not supported yet", attr.ValueSpan));
                        else if (attr.Kind == AttrValueKind.Expression && ReferencesSignal(attr.Value, signals))
                            diags.Add(UiDiagnostics.Error(UiDiagnostics.SignalInItemTemplate,
                                "signal references inside an @foreach template are not supported yet; restructure the binding", attr.ValueSpan));
                    }
                    break;
                case UiText text:
                    foreach (var segment in text.Segments)
                        if (segment.Kind == TextSegmentKind.Expression && ReferencesSignal(segment.Text, signals))
                            diags.Add(UiDiagnostics.Error(UiDiagnostics.SignalInItemTemplate,
                                "signal references inside an @foreach template are not supported yet; restructure the binding", segment.Span));
                    break;
            }
        }
    }

    private static bool ReferencesSignal(string expression, Dictionary<string, ITypeSymbol> signals)
    {
        foreach (var name in signals.Keys)
            if (Regex.IsMatch(expression, $@"\b{Regex.Escape(name)}\b"))
                return true;
        return false;
    }

    private static void Report(SourceProductionContext spc, string path, SourceText text, UiDiagnostic diag)
    {
        var span = new MsTextSpan(diag.Span.Start, Math.Max(0, diag.Span.Length));
        var location = Location.Create(path, span, text.Lines.GetLinePositionSpan(span));
        var descriptor = new DiagnosticDescriptor(
            diag.Code, diag.Code, "{0}", "EclipseUI",
            diag.Severity switch
            {
                CompilerSeverity.Error => RoslynSeverity.Error,
                CompilerSeverity.Warning => RoslynSeverity.Warning,
                _ => RoslynSeverity.Info,
            },
            isEnabledByDefault: true);
        spc.ReportDiagnostic(Diagnostic.Create(descriptor, location, diag.Message));
    }
}
