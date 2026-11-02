using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using MsTextSpan = Microsoft.CodeAnalysis.Text.TextSpan;
using UiSpan = EclipseUI.Compiler.TextSpan;
using CompilerSeverity = EclipseUI.Compiler.DiagnosticSeverity;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using EclipseUI.Compiler.Metadata;

namespace EclipseUI.Compiler.Generator;

/// <summary>
/// .ui → C# 增量源生成器：编译期类型检查（表达式原样进入生成代码，由 Roslyn 检查并经
/// #line 映射回 .ui）、@foreach item 类型推断、@bind 源可写性校验、定向更新代码发射。
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class TemplateGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var uiFiles = context.AdditionalTextsProvider
            .Where(static file => Path.GetExtension(file.Path) is ".ui");

        // 模板表面每轮从符号重建（design/14 §14.10.3：派生目录是唯一事实源，不允许手写缓存）。
        // UiCatalog 逐字段实现结构相等，所以控件 API 没变时下游仍按值命中缓存、不重生成代码。
        var catalogProvider = context.CompilationProvider
            .Select(static (compilation, _) => UiCatalogFactory.Build(compilation));

        var pipeline = uiFiles.Collect().Combine(catalogProvider).Combine(context.CompilationProvider);
        context.RegisterSourceOutput(pipeline, static (spc, data) =>
            Execute(spc, data.Left.Left, data.Left.Right, data.Right));
    }

    private static void Execute(SourceProductionContext spc, ImmutableArray<AdditionalText> files,
        UiCatalog catalog, Compilation compilation)
    {
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

            // 编译期语义探针：@foreach item 类型推断 + @bind 源可写性校验
            var itemTypes = type is null
                ? new Dictionary<UiForeach, string>()
                : ProbeSemantics(doc, type, compilation, diags);

            bool hasErrors = false;
            foreach (var diag in diags)
            {
                Report(spc, file.Path, sourceText, diag);
                if (diag.IsError) hasErrors = true;
            }
            if (hasErrors || type is null) continue;

            var code = new Emitter(doc,
                AccessibilityOf(type), NamespaceName(type), className,
                itemTypes, file.Path).Emit();

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

    /// <summary>
    /// 逐块推断 item 类型，并校验每个 <c>@bind</c> 源：两者都把表达式放进合成 partial 方法体，
    /// 借 code-behind 的符号上下文让 Roslyn 回答；外层 item 以局部变量注入，支持嵌套作用域。
    /// </summary>
    private static Dictionary<UiForeach, string> ProbeSemantics(
        UiDocument doc, INamedTypeSymbol type, Compilation compilation, List<UiDiagnostic> diags)
    {
        var itemTypes = new Dictionary<UiForeach, string>();
        var probe = new TemplateProbe(compilation, type);
        var env = new List<(ITypeSymbol Type, string Name)>();
        Walk(doc.Nodes, env);
        return itemTypes;

        void Walk(IEnumerable<UiNode> nodes, List<(ITypeSymbol Type, string Name)> scope)
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case UiElement element:
                        foreach (var attr in element.Attributes)
                            ValidateBindSource(attr, probe, scope, diags);
                        Walk(element.Children, scope);
                        break;
                    case UiIf ifNode:
                        Walk(ifNode.Children, scope);
                        break;
                    case UiForeach foreachNode:
                        var collectionType = probe.InferType(foreachNode.Collection, scope);
                        var itemType = collectionType is null ? null : ElementTypeOf(collectionType, compilation);
                        if (itemType is null)
                        {
                            diags.Add(UiDiagnostics.Error(UiDiagnostics.ForEachItemTypeUnknown,
                                $"cannot infer the item type of '{foreachNode.Collection.Trim()}'; " +
                                "the collection must be an IEnumerable<T> (array, List<T>, dictionary, LINQ result or custom implementation)",
                                foreachNode.CollectionSpan));
                            break;
                        }

                        itemTypes[foreachNode] = itemType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        var inner = new List<(ITypeSymbol Type, string Name)>(scope)
                        {
                            (itemType, foreachNode.VarName.Length > 0 ? foreachNode.VarName : "item"),
                        };
                        Walk(foreachNode.Children, inner);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// <c>@bind-P={expr}</c> 的源必须是可写左值（spec/01 §4.5）：句法白名单先拒绝调用/索引器/
    /// <c>?.</c>/<c>??</c>/转换/new/lambda，再用赋值探针取 C# 的可写性与类型结论。
    /// 目标非法（EUI040）与非表达式值（EUI037）已由分析器报错，这里不重复报。
    /// </summary>
    private static void ValidateBindSource(UiAttribute attr, TemplateProbe probe,
        IReadOnlyList<(ITypeSymbol Type, string Name)> env, List<UiDiagnostic> diags)
    {
        if (!attr.IsBind || attr.PropDef is not { CanWrite: true }) return;
        if (attr.Kind != AttrValueKind.Expression || string.IsNullOrWhiteSpace(attr.Value)) return;

        var expression = attr.Value.Trim();
        if (!probe.IsWritableSource(expression, attr.PropDef.ClrTypeDisplayName, env, out var reason))
            diags.Add(UiDiagnostics.Error(UiDiagnostics.BindSourceInvalid,
                $"@bind-{attr.Name} source '{expression}' is not writable: {reason}", attr.ValueSpan));
    }

    private sealed class TemplateProbe
    {
        private readonly Compilation _compilation;
        private readonly INamedTypeSymbol _type;
        private int _counter;

        public TemplateProbe(Compilation compilation, INamedTypeSymbol type)
        {
            _compilation = compilation;
            _type = type;
        }

        public ITypeSymbol? InferType(string expression, IReadOnlyList<(ITypeSymbol Type, string Name)> env)
        {
            var tree = ProbeTree("var __value = (" + expression + ");", env);
            var augmented = _compilation.AddSyntaxTrees(tree);
            var model = augmented.GetSemanticModel(tree);
            var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().LastOrDefault();
            if (method is null) return null;
            var declarator = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .FirstOrDefault(v => v.Identifier.ValueText == "__value");
            if (declarator is null) return null;
            if (model.GetDeclaredSymbol(declarator) is not ILocalSymbol local) return null;
            return local.Type.TypeKind == TypeKind.Error || local.Type is IDynamicTypeSymbol ? null : local.Type;
        }

        /// <summary>把 <c>{expr} = __bindValue</c> 交给 C# 判定：落在赋值语句内的错误即左值/类型问题。</summary>
        public bool IsWritableSource(string expression, string targetClrType,
            IReadOnlyList<(ITypeSymbol Type, string Name)> env, out string? reason)
        {
            if (!IsLvalueShape(SyntaxFactory.ParseExpression(expression), out var rootName))
            {
                reason = "it must be a writable field or property of the component or of the item " +
                    "(not a method call, indexer, '?.', '??', cast, 'new' or lambda)";
                return false;
            }

            // 值类型 item 是副本：写它的成员既不报错也传不回去，必须静态拒绝（spec/01 §4.5）。
            if (rootName is not null)
            {
                foreach (var (envType, name) in env)
                {
                    if (name != rootName || !envType.IsValueType) continue;
                    reason = $"the @foreach item '{name}' is a value type ({envType.Name}), " +
                        $"so '{expression}' would write into a copy";
                    return false;
                }
            }

            var tree = ProbeTree($"{expression} = __bindValue;", env,
                $"        {ClrTypeOf(targetClrType)} __bindValue = default!;");
            var augmented = _compilation.AddSyntaxTrees(tree);
            var model = augmented.GetSemanticModel(tree);
            var assignment = tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>().FirstOrDefault();
            if (assignment is null)
            {
                reason = "it is not an assignment target";
                return false;
            }

            switch (model.GetSymbolInfo(assignment.Left).Symbol)
            {
                case IFieldSymbol { IsStatic: true }:
                    reason = "a static field cannot be written back";
                    return false;
                case IFieldSymbol { IsConst: true }:
                    reason = "a constant cannot be written back";
                    return false;
                case IPropertySymbol { IsStatic: true }:
                    reason = "a static property cannot be written back";
                    return false;
            }

            foreach (var diagnostic in model.GetDiagnostics())
            {
                if (diagnostic.Severity != Microsoft.CodeAnalysis.DiagnosticSeverity.Error) continue;
                if (!assignment.Span.Contains(diagnostic.Location.SourceSpan)) continue;
                reason = $"{diagnostic.GetMessage()} ({diagnostic.Id})";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>句法白名单：标识符 / this / base，以及它们之间的简单成员访问链（可加括号）。</summary>
        private static bool IsLvalueShape(ExpressionSyntax expression, out string? rootName)
        {
            rootName = null;
            while (true)
            {
                if (expression is ParenthesizedExpressionSyntax paren)
                {
                    expression = paren.Expression;
                    continue;
                }
                if (expression is IdentifierNameSyntax identifier)
                {
                    rootName = identifier.Identifier.ValueText;
                    return true;
                }
                if (expression is ThisExpressionSyntax or BaseExpressionSyntax) return true;
                if (expression is MemberAccessExpressionSyntax access &&
                    access.IsKind(SyntaxKind.SimpleMemberAccessExpression))
                {
                    expression = access.Expression;
                    continue;
                }
                return false;
            }
        }

        /// <summary>目录里的 CLR 类型名 → 探针里可用的全限定类型。</summary>
        private static string ClrTypeOf(string clrType) => clrType switch
        {
            "bool" => "global::System.Boolean",
            "byte" => "global::System.Byte",
            "char" => "global::System.Char",
            "double" => "global::System.Double",
            "float" => "global::System.Single",
            "int" => "global::System.Int32",
            "long" => "global::System.Int64",
            "object" => "global::System.Object",
            "string" => "global::System.String",
            _ => clrType.StartsWith("global::", StringComparison.Ordinal) ? clrType : "global::" + clrType,
        };

        /// <summary>合成探针方法：与 code-behind 同命名空间的 partial 类，item 以局部变量注入。</summary>
        private SyntaxTree ProbeTree(string statement, IReadOnlyList<(ITypeSymbol Type, string Name)> env,
            string? preamble = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#nullable enable");
            if (!_type.ContainingNamespace.IsGlobalNamespace)
            {
                sb.Append("namespace ").Append(_type.ContainingNamespace.ToDisplayString()).AppendLine(";");
                sb.AppendLine();
            }
            sb.Append(_type.DeclaredAccessibility == Accessibility.Public ? "public" : "internal")
                .Append(" partial class ").Append(_type.Name).AppendLine();
            sb.AppendLine("{");
            sb.Append("    private void __EclipseUiProbe").Append(_counter++).AppendLine("()");
            sb.AppendLine("    {");
            foreach (var (envType, name) in env)
                sb.Append("        ")
                    .Append(envType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                    .Append(' ').Append(name).AppendLine(" = default!;");
            if (preamble is not null) sb.AppendLine(preamble);
            sb.Append("        ").Append(statement).AppendLine(";");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            // 探针树必须与编译单元共享解析选项，否则 AddSyntaxTrees 拒绝混合特征。
            var parseOptions = _compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions
                ?? CSharpParseOptions.Default;
            return CSharpSyntaxTree.ParseText(sb.ToString(), parseOptions);
        }
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
