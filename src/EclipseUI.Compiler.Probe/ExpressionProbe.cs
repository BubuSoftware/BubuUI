using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EclipseUI.Compiler.Probe;

/// <summary>
/// 表达式内一个已解析标识符的绑定：相对表达式首字符的偏移、长度与符号。
/// 重命名（<see cref="ClassifyIdentifiers"/>/<see cref="LocateWithIdentifier"/>）与
/// 未来的语义 token 共用这个载体。readonly struct：Probe 项目跑 netstandard2.0。
/// </summary>
public readonly struct IdentifierBinding
{
    public IdentifierBinding(int offset, int length, ISymbol symbol)
    {
        Offset = offset;
        Length = length;
        Symbol = symbol;
    }

    /// <summary>相对表达式首字符的偏移（调用方加表达式在文档里的起点得文档偏移）。</summary>
    public int Offset { get; }

    /// <summary>标识符长度（改名的替换区间）。</summary>
    public int Length { get; }

    /// <summary>绑定符号：成员符号或环境注入的局部变量（foreach 迭代变量）。</summary>
    public ISymbol Symbol { get; }
}

/// <summary>
/// 表达式投影探针（design/06 §6.3 的核心机制）：把模板表达式放进合成 partial 方法体，
/// 借 Roslyn <see cref="SemanticModel"/> 回答「类型是什么 / 可写吗 / 兼容目标委托吗」。
///
/// 原来是生成器的 private <c>TemplateProbe</c>（编译期 EUI029/EUI037/EUI047/EUI049 在用），
/// 抽出为本项目后由两侧共享：生成器以源码并入（csproj Compile Include，与 Compiler/Metadata
/// 同模式），LSP 以 ProjectReference 引用。**改动必须保持两侧行为逐字节一致**——
/// IDE 的表达式答案与构建期诊断同源，靠的就是这一份实现。
///
/// 线程约束：无锁，非线程安全；调用方与生成器/LSP 一样按文件顺序使用。
/// </summary>
public sealed class ExpressionProbe
{
    private readonly Compilation _compilation;
    private readonly INamedTypeSymbol? _type;
    private readonly string _className;
    private readonly string _namespace;
    private readonly ImmutableArray<string> _usings;
    private int _counter;

    public ExpressionProbe(Compilation compilation, INamedTypeSymbol? type, string className,
        string namespaceName, ImmutableArray<string> usings)
    {
        _compilation = compilation;
        _type = type;
        _className = className;
        _namespace = namespaceName;
        _usings = usings;
    }

    /// <summary>包含根继承关系、code-behind 和 @code 的统一符号编译单元。</summary>
    public Compilation Compilation => _compilation;

    /// <summary>当前模板类型符号（含继承成员和 @code）；裸前缀补全由此枚举。</summary>
    public INamedTypeSymbol? ThisType => _type;

    /// <summary>
    /// 集合类型的元素类型（<see cref="ExpressionEnvironment"/> 建环境用）：
    /// 数组取 ElementType，其余按 IEnumerable&lt;T&gt; 全接口找；都不是 → null。
    /// </summary>
    public static ITypeSymbol? ItemTypeOf(ITypeSymbol type, Compilation compilation)
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
    /// 表达式求值类型（含流调整可空性）：合成 <c>var __value = (expr);</c> 携带语句，
    /// 取 <c>GetTypeInfo(初始化式).Type</c>。不能用 <c>GetDeclaredSymbol</c> 的 var 局部符号
    /// 当返回值——其注解未经 nullable 流精化（一律 Annotated，实测 var d = task 会得到 TaskItem?）。
    /// </summary>
    public ITypeSymbol? InferType(string expression, IReadOnlyList<(ITypeSymbol Type, string Name)> env)
    {
        var tree = ProbeTree("var __value = (" + expression + ");", env);
        var model = _compilation.AddSyntaxTrees(tree).GetSemanticModel(tree);
        var initializer = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.ValueText == "__value")?.Initializer?.Value;
        if (initializer is null) return null;
        var info = model.GetTypeInfo(initializer);
        var type = info.Type ?? info.ConvertedType;
        if (type is null || type.TypeKind == TypeKind.Error || type is IDynamicTypeSymbol) return null;
        return type;
    }

    /// <summary>
    /// 方法组解析（IDE 悬停事件处理器用）：<c>var __value = (expr);</c> 的初始化式符号是
    /// 普通方法时返回它；lambda/构造/未解析返回 null。
    /// </summary>
    public IMethodSymbol? ResolveMethod(string expression, IReadOnlyList<(ITypeSymbol Type, string Name)> env)
    {
        var tree = ProbeTree("var __value = (" + expression + ");", env);
        var model = _compilation.AddSyntaxTrees(tree).GetSemanticModel(tree);
        var initializer = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.ValueText == "__value")?.Initializer?.Value;
        return initializer is null ? null : model.GetSymbolInfo(initializer).Symbol as IMethodSymbol;
    }

    /// <summary>
    /// 光标处符号反查（跳转定义用，L3）：同一探针语句里找包含 <paramref name="cursor"/>
    /// （表达式内偏移）的标识符，返回它的绑定符号。光标落在点号/空白/括号上、
    /// 或标识符解析不到（半截输入、未知名字）返回 null——不猜。
    /// </summary>
    public ISymbol? Locate(string expression, int cursor, IReadOnlyList<(ITypeSymbol Type, string Name)> env) =>
        LocateWithIdentifier(expression, cursor, env)?.Symbol;

    /// <summary>
    /// 光标处标识符的绑定 + 区间（重命名/prepareRename 用，L3 第二片）。offset 相对表达式
    /// 首字符——符号与位置一起给，调用方不用再猜替换区间落在哪。
    /// </summary>
    public IdentifierBinding? LocateWithIdentifier(
        string expression, int cursor, IReadOnlyList<(ITypeSymbol Type, string Name)> env)
    {
        var probed = ProbeInitializer(expression, env);
        if (probed is null) return null;
        var (model, initializer) = probed.Value;

        // 表达式原文按 "(" + expression 合成：初始化式 SpanStart 就是那个 "("，
        // +1 即表达式首字符——与 expression 内容（自身含括号/泛型）无关，偏移永远对齐。
        // （netstandard2.0 没有 Math.Clamp，手写钳制。）
        var clamped = cursor < 0 ? 0 : cursor > expression.Length ? expression.Length : cursor;
        var cursorInTree = initializer.SpanStart + 1 + clamped;

        IdentifierNameSyntax? target = null;
        foreach (var identifier in initializer.DescendantNodes().OfType<IdentifierNameSyntax>())
            if (cursorInTree >= identifier.SpanStart && cursorInTree <= identifier.Span.End) target = identifier;
        if (target is null) return null;

        var symbol = ResolveIdentifier(model, target);
        if (symbol is null) return null;
        return new IdentifierBinding(
            target.SpanStart - (initializer.SpanStart + 1), target.Span.Length, symbol);
    }

    /// <summary>
    /// 表达式里**所有**可解析标识符的绑定（重命名引用扫描用，L3 第二片）：
    /// 成员符号与 foreach 环境注入的局部变量都原样报告，由调用方按目标分类；
    /// 解析不到（半截输入、未知名、ErrorType）的跳过——引用扫描宁可少报不可错报。
    /// </summary>
    public IReadOnlyList<IdentifierBinding> ClassifyIdentifiers(
        string expression, IReadOnlyList<(ITypeSymbol Type, string Name)> env)
    {
        var probed = ProbeInitializer(expression, env);
        if (probed is null) return Array.Empty<IdentifierBinding>();
        var (model, initializer) = probed.Value;
        var exprStart = initializer.SpanStart + 1;

        var hits = new List<IdentifierBinding>();
        foreach (var identifier in initializer.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var symbol = ResolveIdentifier(model, identifier);
            if (symbol is null) continue;
            hits.Add(new IdentifierBinding(identifier.SpanStart - exprStart, identifier.Span.Length, symbol));
        }
        return hits;
    }

    /// <summary>合成 <c>var __value = (expr);</c> 并取语义模型与括号初始化式；解析失败返回 null。</summary>
    private (SemanticModel Model, ParenthesizedExpressionSyntax Initializer)? ProbeInitializer(
        string expression, IReadOnlyList<(ITypeSymbol Type, string Name)> env)
    {
        var tree = ProbeTree("var __value = (" + expression + ");", env);
        var model = _compilation.AddSyntaxTrees(tree).GetSemanticModel(tree);
        var initializer = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.ValueText == "__value")?.Initializer?.Value
            as ParenthesizedExpressionSyntax;
        return initializer is null ? null : (model, initializer);
    }

    /// <summary>标识符解析：零候选当未解析（方法组转换失败等），单候选收下，ErrorType 不算解析成功。</summary>
    private static ISymbol? ResolveIdentifier(SemanticModel model, IdentifierNameSyntax identifier)
    {
        var info = model.GetSymbolInfo(identifier);
        if (info.Symbol is not null) return info.Symbol is IErrorTypeSymbol ? null : info.Symbol;
        return info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null;
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
            // 全限定：本文件与 Compiler 源码同编译时，非限定名会命中 EclipseUI.Compiler.DiagnosticSeverity。
            if (diagnostic.Severity != Microsoft.CodeAnalysis.DiagnosticSeverity.Error) continue;
            if (!assignment.Span.Contains(diagnostic.Location.SourceSpan)) continue;
            reason = $"{diagnostic.GetMessage()} ({diagnostic.Id})";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// <c>Ref={field}</c> 用赋值探针取 C# 结论：把 <c>{target} = __refValue</c>（__refValue 为
    /// 元素/组件实例类型）放进合成方法体。缺失（CS0103）/类型不符（CS0029）/只读（CS0191）由
    /// 赋值语句内的 CS 错误回答（与 <see cref="IsWritableSource"/> 同法）；编译干净后再要求
    /// 目标是 code-behind 的非静态实例字段（static/const/readonly 兜底，防语义合法但形态非法）。
    /// </summary>
    public bool IsRefTarget(string target, string valueClrType,
        IReadOnlyList<(ITypeSymbol Type, string Name)> env, out string? reason)
    {
        if (!IsLvalueShape(SyntaxFactory.ParseExpression(target), out _))
        {
            reason = "it must be a simple identifier";
            return false;
        }

        // Ref 目标不允许落在 @foreach item 名上：真实生成代码里 item 是迭代变量，不可赋值。
        foreach (var (_, name) in env)
        {
            if (name != target) continue;
            reason = $"'{target}' is the @foreach item; the Ref target must be an instance field " +
                "declared on the code-behind";
            return false;
        }

        var tree = ProbeTree($"{target} = __refValue;", env,
            $"        {ClrTypeOf(valueClrType)} __refValue = default!;");
        var augmented = _compilation.AddSyntaxTrees(tree);
        var model = augmented.GetSemanticModel(tree);
        var assignment = tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>().FirstOrDefault();
        if (assignment is null)
        {
            reason = "it is not an assignment target";
            return false;
        }

        // C# 诊断优先：缺失/类型不符/readonly 在 bound 阶段就把左值标记为失败，Symbol 会变 null，
        // 所以必须先问诊断再问符号（readonly 实测 CS0191 + null symbol）。
        foreach (var diagnostic in model.GetDiagnostics())
        {
            if (diagnostic.Severity != Microsoft.CodeAnalysis.DiagnosticSeverity.Error) continue;
            if (!assignment.Span.Contains(diagnostic.Location.SourceSpan)) continue;
            reason = $"{diagnostic.GetMessage()} ({diagnostic.Id})";
            return false;
        }

        if (model.GetSymbolInfo(assignment.Left).Symbol is not IFieldSymbol field)
        {
            reason = "the Ref target must be an instance field of the code-behind class";
            return false;
        }
        if (field.IsStatic)
        {
            reason = $"'{field.Name}' is a static field and cannot hold a template instance";
            return false;
        }
        if (field.IsConst)
        {
            reason = $"'{field.Name}' is a constant";
            return false;
        }
        if (field.IsReadOnly)
        {
            reason = $"'{field.Name}' is readonly and can only be assigned in a constructor";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// 「委托转换」类 C# 错误：这些说明表达式本身可解析，但签/形态与目标委托不匹配。
    /// 其它错误（CS0103 未解析、CS1501/CS1503 表达式内部调用失败等）一律视为
    /// 静态不可判定，交回生成代码经 #line 由 C# 报告。
    /// </summary>
    private static readonly HashSet<string> DelegateConversionErrors = new(StringComparer.Ordinal)
    {
        "CS0029", "CS0030", "CS0123", "CS1593",
        "CS1660", "CS1661", "CS1678", "CS0411", "CS0428", "CS0119",
    };

    /// <summary>
    /// true = 可转换为目标委托；false = 确定不匹配（可报 EUI024）；
    /// null = 静态不可判定（符号/上下文不足），由 C# 经 #line 兜底。
    /// 复用 <see cref="ProbeTree"/>：与 EUI037/EUI029 同一套合成 partial 方法体机制。
    /// </summary>
    public bool? IsHandlerCompatible(string expression, string delegateType,
        IReadOnlyList<(ITypeSymbol Type, string Name)> env, out string? reason)
    {
        reason = null;
        expression = expression.Trim();
        if (expression.Length == 0) return null;

        var target = ClrTypeOf(delegateType);
        var tree = ProbeTree("var __handler = (" + target + ")(" + expression + ")", env);
        var model = _compilation.AddSyntaxTrees(tree).GetSemanticModel(tree);
        var initializer = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.ValueText == "__handler")?.Initializer?.Value;
        if (initializer is not CastExpressionSyntax cast) return null;
        var inner = cast.Expression;

        // (1) 表达式自身必须语义干净。方法组转换失败时 Symbol 可能为 null 但候选非空，
        //     因此只把「零候选」当作未解析；歧义/重载失败留给下面的错误分类处理。
        foreach (var name in inner.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
        {
            var info = model.GetSymbolInfo(name);
            if (info.Symbol is null && info.CandidateSymbols.Length == 0) return null;
            if (info.Symbol is IErrorTypeSymbol) return null;
        }
        if (model.GetTypeInfo(inner).Type is IErrorTypeSymbol or IDynamicTypeSymbol) return null;

        // (2) 落在 cast 内的 Error 必须全部是委托转换类；否则说明表达式本身有问题。
        string? failure = null;
        foreach (var diagnostic in model.GetDiagnostics())
        {
            if (diagnostic.Severity != Microsoft.CodeAnalysis.DiagnosticSeverity.Error) continue;
            if (!cast.Span.Contains(diagnostic.Location.SourceSpan)) continue;
            if (!DelegateConversionErrors.Contains(diagnostic.Id)) return null;
            failure ??= $"{diagnostic.GetMessage()} ({diagnostic.Id})";
        }

        if (failure is null) return true;
        reason = failure;
        return false;
    }

    /// <summary>
    /// EUI049（表达式语义分析最小闭环）：把模板表达式放进合成方法体做探针编译，
    /// 落在表达式本体 span 内的 CS 错误逐条返回（消息 + 代码）——拼错成员（CS0103）、
    /// 类型不匹配（CS0029/CS0266）等在使用点直接报。带 <paramref name="targetClrType"/> 时
    /// 先声明目标类型的局部变量再赋值（生成代码同形态：左值 = 表达式），类型转换错误一并捕获；
    /// 无目标类型（条件/文本段）用 var 探针。脚手架自身的句外错误忽略；
    /// 生成代码仍经 #line 由 C# 兜底（同一错误两处可见是探针校验的既定权衡）。
    /// </summary>
    public IReadOnlyList<string> ProbeExpressionErrors(string expression,
        IReadOnlyList<(ITypeSymbol Type, string Name)> env, string? targetClrType = null)
    {
        SyntaxTree tree;
        if (targetClrType is null)
        {
            tree = ProbeTree("var __value = (" + expression + ");", env);
        }
        else
        {
            // 与生成代码同形态：目标局部 = 表达式。错误过滤锚定赋值右侧。
            tree = ProbeTree(
                ClrTypeOf(targetClrType) + " __value = default!; __value = (" + expression + ");", env);
        }

            var model = _compilation.AddSyntaxTrees(tree).GetSemanticModel(tree);
            var valueNode = targetClrType is null
                ? tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>()
                    .FirstOrDefault(v => v.Identifier.ValueText == "__value")?.Initializer?.Value
                : tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>()
                    .FirstOrDefault()?.Right;
        if (valueNode is null) return Array.Empty<string>();

        var errors = new List<string>();
        foreach (var diagnostic in model.GetDiagnostics())
        {
            if (diagnostic.Severity != Microsoft.CodeAnalysis.DiagnosticSeverity.Error) continue;
            if (!valueNode.Span.Contains(diagnostic.Location.SourceSpan)) continue;
            errors.Add($"{diagnostic.GetMessage()} ({diagnostic.Id})");
        }
        return errors;
    }

    /// <summary>
    /// K29/EUI047：case 标签编译期常量校验——把 <c>const T __caseKey = expr</c> 放进探针，
    /// 只有 CS0133（"must be constant"）落在声明上才判非常量；类型不匹配、符号未解析等
    /// 其它错误静默返回 true，交生成代码经 #line 由 C# 兜底（§2.4 规则 8：不重复报 C# 错）。
    /// </summary>
    public bool IsConstant(string expression, ITypeSymbol targetType,
        IReadOnlyList<(ITypeSymbol Type, string Name)> env, out string? reason)
    {
        reason = null;
        var tree = ProbeTree(
            "const " + ClrTypeOf(targetType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)) +
            " __caseKey = " + expression, env);
        var model = _compilation.AddSyntaxTrees(tree).GetSemanticModel(tree);
        var declarator = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.ValueText == "__caseKey");
        if (declarator?.Initializer is null) return true;
        foreach (var diagnostic in model.GetDiagnostics())
        {
            if (diagnostic.Id != "CS0133" ||
                diagnostic.Severity != Microsoft.CodeAnalysis.DiagnosticSeverity.Error ||
                !declarator.Span.Contains(diagnostic.Location.SourceSpan)) continue;
            reason = "use a literal, a const field, an enum member or a constant expression";
            return false;
        }
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

    /// <summary>
    /// 合成探针方法：与 code-behind（或 @code 类壳，K23 ④）同命名空间的 partial 类，
    /// item 以局部变量注入。模板 @using 逐行输出——using 是语法树局部的，成员合成树里的
    /// using 行帮不到本树的表达式，两边都要有。
    /// </summary>
    private SyntaxTree ProbeTree(string statement, IReadOnlyList<(ITypeSymbol Type, string Name)> env,
        string? preamble = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#nullable enable");
        foreach (var ns in _usings)
            sb.AppendLine($"using {ns};");
        if (_namespace.Length > 0)
        {
            sb.Append("namespace ").Append(_namespace).AppendLine(";");
            sb.AppendLine();
        }
        sb.Append(_type is null || _type.DeclaredAccessibility != Accessibility.Public ? "internal" : "public")
            .Append(" partial class ").Append(_type?.Name ?? _className).AppendLine();
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
