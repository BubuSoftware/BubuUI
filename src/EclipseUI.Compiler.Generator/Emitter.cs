using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using EclipseUI.Compiler;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MsSyntaxKind = Microsoft.CodeAnalysis.CSharp.SyntaxKind;

namespace EclipseUI.Compiler.Generator;

/// <summary>
/// 代码发射：把模板 AST 翻译成组件构建代码。
/// 生成代码 = 结构创建指令 + 根级 U{N}/块内 R{N} 定向更新 + 块协调器接线；
/// 表达式行用 #line 指向 .ui 源位置（诊断与断点映射共用）。
/// </summary>
public sealed class Emitter
{
    private enum EmitMode { Root, Block, Content }

    private sealed class EmitContext
    {
        public EmitMode Mode { get; init; }
        public string? ScopeVar { get; init; }
        public string ConstructionVar { get; init; } = "__euiConstruction";

        public bool IsRoot => Mode == EmitMode.Root;
        public bool IsContent => Mode == EmitMode.Content;

        public static EmitContext Root() => new() { Mode = EmitMode.Root };

        public static EmitContext Block(string scopeVar)
            => new() { Mode = EmitMode.Block, ScopeVar = scopeVar, ConstructionVar = "__euiConstruction_" + scopeVar };

        public static EmitContext Content(EmitContext context)
            => new() { Mode = EmitMode.Content, ConstructionVar = context.ConstructionVar };
    }

    private sealed record FieldDecl(int N, string Prefix, string Type);
    private sealed record UMethod(string Name, StringBuilder Body);

    /// <summary>
    /// 待回写的映射（design/18 §5.2）：发射时先记在**当时写入的缓冲区**里，
    /// 缓冲区并入目标时随 <see cref="AppendMapped"/> 平移；Emit 收尾只收已并入 _sb 的。
    /// </summary>
    private sealed class PendingMapping
    {
        public TextSpan Original;
        public StringBuilder Buffer = null!;
        public int Offset;
        public int Length;
        public bool Verbatim;
    }

    private readonly UiDocument _doc;
    private readonly string _accessibility;
    private readonly string _namespace;
    private readonly string _className;
    private readonly IReadOnlyDictionary<int, string> _itemTypes;
    /// <summary>@switch selector 的值类型（探针推断，ValueSpan.Start → 全限定类型名；K29）。</summary>
    private readonly IReadOnlyDictionary<int, string> _switchValueTypes;
    /// <summary>模板 @using 的 using 子句正文（K22/D85 命名空间、K31 别名）：
    /// 按模板顺序去重，输出到文件头 using 组。</summary>
    private readonly IReadOnlyList<string> _usings;
    /// <summary>@code 块（K21/D86）：按源码顺序整块透传进类壳，带 span 型 #line 映射。</summary>
    private readonly IReadOnlyList<UiCodeBlock> _codeBlocks;
    /// <summary>@style 组件块的解析产物（E1，design/17 §4.8）：类名已加组件前缀，
    /// 发射为静态幂等注册方法，Build 首行调用。</summary>
    private readonly IReadOnlyList<EustyleRule> _styleRules;
    /// <summary>本组件 @style 块的 @states 声明（E5）：__RegisterStyles 先发注册序列。</summary>
    private readonly IReadOnlyList<EustyleStateDecl> _styleStates;
    /// <summary>编译期符号派生的可样式属性目录（Style= 内联发射的目标字段唯一来源）。</summary>
    private readonly StylePropertyCatalog _styleProperties;
    /// <summary>自身 code-behind 组件的插槽（spec/02 §4.12.5）——TemplateGenerator 按
    /// code-behind 符号精确匹配传入；分析器的短名近似只用于诊断，发射以此为准。</summary>
    private readonly IReadOnlyDictionary<string, UiSlotDef> _ownSlots;
    private readonly string _linePath;
    private readonly string _filePath;
    private readonly bool _captureMappings;
    private readonly List<PendingMapping> _pendingMappings = new();
    private readonly StringBuilder _sb = new();
    private readonly StringBuilder _body = new();
    private readonly List<FieldDecl> _fields = new();
    private readonly List<UMethod> _uMethods = new();
    private readonly List<string> _renderCalls = new();
    private readonly Dictionary<string, string> _contentTargets = new(StringComparer.Ordinal);
    private StringBuilder _out = null!;
    private int _indent;
    private int _varCounter;
    private int _fnCounter;

    public Emitter(UiDocument doc, string accessibility, string namespaceName, string className,
        IReadOnlyDictionary<int, string> itemTypes, string filePath,
        IReadOnlyDictionary<string, UiSlotDef>? ownSlots = null,
        IReadOnlyDictionary<int, string>? switchValueTypes = null,
        IReadOnlyList<string>? usings = null,
        IReadOnlyList<UiCodeBlock>? codeBlocks = null,
        IReadOnlyList<EustyleRule>? styleRules = null,
        IReadOnlyList<EustyleStateDecl>? styleStates = null,
        StylePropertyCatalog? styleProperties = null)
    {
        _doc = doc;
        _accessibility = accessibility;
        _namespace = namespaceName;
        _className = className;
        _itemTypes = itemTypes;
        _switchValueTypes = switchValueTypes ?? new Dictionary<int, string>();
        _usings = usings ?? Array.Empty<string>();
        _codeBlocks = codeBlocks ?? Array.Empty<UiCodeBlock>();
        _styleRules = styleRules ?? Array.Empty<EustyleRule>();
        _styleStates = styleStates ?? Array.Empty<EustyleStateDecl>();
        _styleProperties = styleProperties ?? StylePropertyCatalog.Empty;
        _ownSlots = ownSlots ?? new Dictionary<string, UiSlotDef>(StringComparer.Ordinal);
        // #line 的文件名不是 C# 字符串字面量：反斜杠原样写入，否则映射路径会变成双反斜杠。
        _linePath = filePath.Replace("\"", "\\\"");
        _filePath = filePath;
        // design/18 §5.2：只有 IDE 开了旁路窗口才记录映射；build 路径恒 false，零开销。
        _captureMappings = IdeSourceMappingSideband.IsActive;
    }

    public string Emit()
    {
        var root = (UiElement)_doc.Nodes.First(n => n is UiElement);
        // K37：@inject 声明（spec/01 §4.18）——属性声明进类壳、解析语句进 InitializeUi 最前。
        var injects = _doc.Nodes.OfType<UiInjectDirective>().ToList();
        _out = _body;
        _indent = 2;
        // K37（spec/02 §4.7，R11）：@inject 服务解析——InitializeUi 的最前语句，先于
        // UiConstructionScope、base.InitializeUi() 与根输入；未注册或未构建应用时
        // ?? throw 抛 InvalidOperationException（fail-fast，不构建内容树）。
        foreach (var inject in injects)
            Mapped(inject.Span, ResolutionStatement(inject));
        Line("using var __euiConstruction = new global::EclipseUI.UiConstructionScope();");
        Line("try");
        Line("{");
        _indent++;
        // E1：组件私有样式在 Build 首行幂等注册（类名带组件前缀，树重建重复调用零开销）。
        if (_styleRules.Count > 0)
            Line("__RegisterStyles();");
        EmitElement(root, parentVar: null, EmitContext.Root(), receiver: "this");
        Line("__euiConstruction.Complete();");
        _indent--;
        EmitConstructionCatch("__euiConstruction");

        _out = _sb;
        _indent = 0;
        _sb.AppendLine("// <auto-generated> 由 EclipseUI 模板编译器生成；请勿手动修改。 </auto-generated>");
        _sb.AppendLine("#nullable enable");
        // K22（D85）/K31：模板 @using 的 using 子句在 #nullable 与 namespace 之间，按模板顺序去重。
        // 命名空间形态输出 `using Ns;`，别名形态输出 `using Alias = global::Ns.Type;`
        // （目标全限定，避免被生成类所在命名空间的同名类型劫持）。生成代码仍一律 global:: 全限定，
        // 这些 using 只为 @code 与模板表达式提供短名。
        foreach (var line in _usings)
            _sb.AppendLine($"using {line};");
        if (_namespace.Length > 0)
        {
            _sb.AppendLine($"namespace {_namespace};");
            _sb.AppendLine();
        }
        if (_doc.TemplateTarget is { } ownerType)
        {
            _sb.AppendLine($"{_accessibility} partial class {_className} : global::EclipseUI.ControlTemplate<{ownerType}>");
            _sb.AppendLine("{");
            _sb.AppendLine($"    protected override global::EclipseUI.TemplateInstance Build({ownerType} owner)");
            _sb.AppendLine("    {");
            _sb.AppendLine($"        var view = new {_className}__TemplateView {{ Owner = owner }};");
            _sb.AppendLine("        try { view.EnsureInitialized(); return new global::EclipseUI.TemplateInstance(view, view.UpdateTemplate); }");
            _sb.AppendLine("        catch { new global::EclipseUI.TemplateInstance(view).Dispose(); throw; }");
            _sb.AppendLine("    }");
            _sb.AppendLine("}");
        }
        _sb.AppendLine($"{(_doc.TemplateTarget is null ? _accessibility : "internal")} partial class {_className}{(_doc.TemplateTarget is null ? "" : "__TemplateView")} : " +
                       root.ResolvedDef!.ClrTypeDisplayName);
        _sb.AppendLine("{");
        if (_doc.TemplateTarget is { } templateOwner)
        {
            _sb.AppendLine($"    public {templateOwner} Owner {{ get; set; }} = null!;");
            _sb.AppendLine("    public void UpdateTemplate() => Render();");
        }

        // K21/K23 ④（D86）：@code 成员按块源码顺序透传（spec/02 §2.3 白名单第五类），
        // 整块一个 span 型 #line 区间映射回 .ui（K25：原文不改写、不注入 using）。
        foreach (var block in _codeBlocks)
            EmitCodeBlock(block);
        if (_codeBlocks.Count > 0) _sb.AppendLine();

        // K37（spec/02 §4.7）：@inject 注入属性——get-only 对外、private set 供类内解析赋值。
        // 声明行带 span 型 #line 指向类型名（spec/02 §2.4 口径）；属性名冲突由 EUI045/EUI065 报。
        foreach (var inject in injects)
        {
            _indent = 1;
            Mapped(inject.TypeNameSpan,
                $"public {inject.TypeName} {inject.PropertyName} {{ get; private set; }} = null!;",
                userOffset: "public ".Length, userLength: inject.TypeName.Length);
            _indent = 0;
        }
        if (injects.Count > 0) _sb.AppendLine();

        foreach (var field in _fields.OrderBy(f => f.N))
            _sb.Append("    private ").Append(field.Type).Append(" __").Append(field.Prefix).Append(field.N)
                .AppendLine(" = null!;");
        if (_fields.Count > 0) _sb.AppendLine();

        // E1（design/17 §4.8）：@style 组件块的注册方法——静态幂等（守卫位），类名在
        // TemplateGenerator 已加组件前缀；注册目标是全局 StyleRegistry.Instance（与
        // AppBuilder.UseStyles 装配的 Provider 同源），运行时零新机制。
        if (_styleRules.Count > 0)
        {
            _sb.AppendLine("    private static bool __euiStylesRegistered;");
            _sb.AppendLine();
            _sb.AppendLine("    private static void __RegisterStyles()");
            _sb.AppendLine("    {");
            _sb.AppendLine("        if (__euiStylesRegistered) return;");
            _sb.AppendLine("        __euiStylesRegistered = true;");
            // E5：@states 声明先注册（幂等）——变体链内联 Register 自足，此处保证未引用的
            // 声明位也完成注册。
            _sb.Append(EustyleGenerator.EmitStateRegisters(_styleStates, "        "));
            _sb.Append(EustyleGenerator.EmitDefines(
                new EustyleStylesheet(_styleRules),
                "global::EclipseUI.Rendering.StyleRegistry.Instance",
                _styleProperties));
            _sb.AppendLine("    }");
            _sb.AppendLine();
        }

        _sb.AppendLine("    protected override void InitializeUi()");
        _sb.AppendLine("    {");
        AppendMapped(_sb, _body);
        _sb.AppendLine("    }");

        _sb.AppendLine("    protected override void ReloadUi() => RebuildUi();");

        if (_renderCalls.Count > 0)
        {
            _sb.AppendLine();
            _sb.AppendLine("    protected override void Render()");
            _sb.AppendLine("    {");
            _sb.AppendLine("        base.Render();");
            foreach (var call in _renderCalls)
                _sb.Append("        ").AppendLine(call);
            _sb.AppendLine("    }");
        }

        foreach (var method in _uMethods)
        {
            _sb.AppendLine();
            _sb.Append("    private void ").Append(method.Name).AppendLine("()");
            _sb.AppendLine("    {");
            AppendMapped(_sb, method.Body);
            _sb.AppendLine("    }");
        }

        _sb.AppendLine("}");
        PublishMappings();
        return _sb.ToString();
    }

    /// <summary>
    /// 缓冲区并入：文本追加之外，把记在源缓冲区里的映射平移到目标缓冲区坐标。
    /// 链式并入（R 临时缓冲 → _body → _sb）每次只平移一级，偏移自然累加。
    /// </summary>
    private void AppendMapped(StringBuilder target, StringBuilder source)
    {
        var delta = target.Length;
        target.Append(source);
        if (!_captureMappings) return;
        foreach (var pending in _pendingMappings)
            if (ReferenceEquals(pending.Buffer, source))
            {
                pending.Offset += delta;
                pending.Buffer = target;
            }
    }

    private void RecordPending(TextSpan original, int offset, int length, bool verbatim)
    {
        if (!_captureMappings) return;
        _pendingMappings.Add(new PendingMapping
        {
            Original = original,
            Buffer = _out,
            Offset = offset,
            Length = length,
            Verbatim = verbatim,
        });
    }

    private void PublishMappings()
    {
        if (!_captureMappings) return;
        var mappings = new List<IdeSourceMapping>(_pendingMappings.Count);
        foreach (var pending in _pendingMappings)
        {
            // 防御：只收已并入最终缓冲区的映射（正常路径全部满足）。
            if (!ReferenceEquals(pending.Buffer, _sb)) continue;
            mappings.Add(new IdeSourceMapping(pending.Original.Start, pending.Original.Length,
                pending.Offset, pending.Length, pending.Verbatim));
        }
        IdeSourceMappingSideband.Record(_filePath, mappings.ToArray());
    }

    private string NewVar(string prefix) => $"{prefix}{_varCounter++}";

    /// <summary>
    /// @code 块透传（K21/K25，D86）：块体原文不改写输出进类壳，整块一个 span 型 #line 区间
    /// 映射回 .ui（块体起点到终点），成员级 C# 错误经映射指回 .ui 行（后续行行号递增，D79 实测语义）。
    /// </summary>
    private void EmitCodeBlock(UiCodeBlock block)
    {
        var start = block.CodeSpan.Start;
        var end = System.Math.Max(start, block.CodeSpan.End);
        var sl = _doc.Lines.LineOf(start);
        var sc = _doc.Lines.ColumnOf(start);
        var el = _doc.Lines.LineOf(end);
        var ec = _doc.Lines.ColumnOf(end);
        if (el == sl && ec <= sc) ec = sc + 1;
        _sb.Append("#line (").Append(sl).Append(", ").Append(sc).Append(") - (")
            .Append(el).Append(", ").Append(ec).Append(") \"").Append(_linePath).Append('"').AppendLine();
        // 块体逐字透传 → 1:1 映射（子区间可按偏移差双向换算）。此时 _out == _sb，坐标即最终坐标。
        RecordPending(new TextSpan(block.CodeSpan.Start, block.Code.Length),
            _sb.Length, block.Code.Length, verbatim: true);
        _sb.Append(block.Code);
        // 原文不以换行结尾时补一个，保证 #line hidden 独立成行
        if (block.Code.Length > 0 && !block.Code.EndsWith("\n")) _sb.AppendLine();
        _sb.AppendLine("#line hidden");
    }

    /// <summary>
    /// K37：@inject 解析语句（spec/02 §4.7 冻结形态，单行发射）——
    /// <c>Name = (T)(Application.Current?.Services.GetService(typeof(T)) ?? throw …);</c>。
    /// <c>Current?</c> 短路覆盖「未构建应用」，<c>?? throw</c> 覆盖「服务未注册」，
    /// 两者都是 <see cref="InvalidOperationException"/> fail-fast（R11：不构建内容树）。
    /// </summary>
    private static string ResolutionStatement(UiInjectDirective inject) =>
        $"{inject.PropertyName} = ({inject.TypeName})(global::EclipseUI.Application.Current?.Services" +
        $".GetService(typeof({inject.TypeName})) ?? throw new global::System.InvalidOperationException(" +
        $"\"@inject '{inject.PropertyName}' ({inject.TypeName}) is not registered in Application.Current.Services; " +
        "register it via AppBuilder.ConfigureServices and build the application before creating UI\"));";

    private void Line(string text)
    {
        _out.Append(' ', _indent * 4).AppendLine(text);
    }

    /// <summary>
    /// 用户语句映射到 .ui span（G3 列映射，spec/02 §2.4）：发射 span 型指令
    /// <c>#line (sl, sc) - (el, ec) "path"</c>。实测语义（Roslyn 4.9.2）：下一个生成行行对齐到 sl、
    /// 列平移 sc−1（生成列 c → sc−1+c），长度随生成文本；后续行行号递增、列自然；end 元组不参与
    /// 单行映射但 start &gt; end 会报 CS8939，故 sc 只向左对齐（sc ≤ span 起始列 ≤ ec）。
    /// <paramref name="userOffset"/> 是用户表达式在 <paramref name="text"/> 内的偏移：
    /// 令表达式起点精确落回 span 起始列；生成前缀更长时钳制 sc=1（行仍精确，列尽力）。
    /// 随后 hidden，使未映射行指回生成文件。
    ///
    /// design/18 §5.2：发射的同时记录 SourceMapping。<paramref name="userLength"/> &gt; 0 时
    /// 记表达式级 1:1 映射（原文逐字进入生成代码）；否则记语句级映射（生成代码包了
    /// cast/lambda/前缀，只支持整段对应，子区间换算不猜）。#line 输出不受记录影响。
    /// </summary>
    private void Mapped(TextSpan span, string text, int userOffset = 0, int userLength = 0,
        IReadOnlyList<(TextSpan Span, int Offset, int Length)>? moreExpressions = null,
        TextSpan? mappingSpan = null)
    {
        var genStart = EmitMappedLine(span, text, userOffset);
        if (!_captureMappings) return;
        // #line 用 span（历史逐字不变）；映射原文可另给 mappingSpan——静态 run 的词法
        // Span 会伸进后随表达式，照单记录会与表达式映射重叠而被双向拒绝。
        var original = mappingSpan ?? span;
        if (userLength > 0)
            RecordPending(new TextSpan(TrimmedStart(original), userLength),
                genStart + _indent * 4 + Math.Max(0, userOffset), userLength, verbatim: true);
        else
            RecordPending(TrimmedSpan(original), genStart + _indent * 4, text.Length, verbatim: false);
        // 插值文本一行多个表达式：#line 仍只对齐首个（历史行为），映射每个都记（design/18 §5.2）。
        if (moreExpressions is null) return;
        foreach (var (exprSpan, offset, length) in moreExpressions)
            RecordPending(new TextSpan(TrimmedStart(exprSpan), length),
                genStart + _indent * 4 + offset, length, verbatim: true);
    }

    /// <summary>只发 #line + 文本 + hidden（与历史输出逐字一致），返回文本行在缓冲区内的起点。</summary>
    private int EmitMappedLine(TextSpan span, string text, int userOffset)
    {
        var start = span.Start;
        var end = Math.Max(start, span.End);
        while (start < end && start < _doc.Text.Length && char.IsWhiteSpace(_doc.Text[start])) start++;
        var sl = _doc.Lines.LineOf(start);
        var sc = _doc.Lines.ColumnOf(start);
        var el = _doc.Lines.LineOf(end);
        var ec = _doc.Lines.ColumnOf(end);
        sc = Math.Max(1, sc - (_indent * 4 + Math.Max(0, userOffset)));
        // Roslyn 对 start >= end 报 CS8939；空 span 或列钳制后给 end 留一列宽度。
        if (el == sl && ec <= sc) ec = sc + 1;
        _out.Append("#line (").Append(sl).Append(", ").Append(sc).Append(") - (")
            .Append(el).Append(", ").Append(ec).Append(") \"").Append(_linePath).Append('"').AppendLine();
        var genStart = _out.Length;
        Line(text);
        _out.AppendLine("#line hidden");
        return genStart;
    }

    /// <summary>
    /// 从生成文本里定位用户 token 的列偏移，省去每个调用点手算前缀长度。
    /// #line 沿用首个 IndexOf（历史行为逐字不变）；SourceMapping 则按**标识符边界**
    /// 找出全部出现（bind 的 readSource/writeSource 两次出现都记，重命名才能两处同改），
    /// 原文侧同样按边界定位 token——转换过的字面量（ConvertedLiteral 与原文不同）定位不到
    /// 就退语句级映射，不猜。
    /// </summary>
    private void MappedToken(TextSpan span, string text, string userToken)
    {
        var offset = userToken.Length == 0 ? -1 : text.IndexOf(userToken, StringComparison.Ordinal);
        var genStart = EmitMappedLine(span, text, offset > 0 ? offset : 0);
        if (!_captureMappings) return;

        if (userToken.Length > 0 && FindTokenInOriginal(span, userToken) is { } original)
        {
            var recorded = false;
            foreach (var at in TokenOccurrences(text, userToken))
            {
                RecordPending(original, genStart + _indent * 4 + at, userToken.Length, verbatim: true);
                recorded = true;
            }
            if (recorded) return;
        }
        RecordPending(TrimmedSpan(span), genStart + _indent * 4, text.Length, verbatim: false);
    }

    private int TrimmedStart(TextSpan span)
    {
        var start = span.Start;
        var end = Math.Max(start, span.End);
        while (start < end && start < _doc.Text.Length && char.IsWhiteSpace(_doc.Text[start])) start++;
        return start;
    }

    private TextSpan TrimmedSpan(TextSpan span)
    {
        var start = TrimmedStart(span);
        return new TextSpan(start, Math.Max(start, span.End) - start);
    }

    /// <summary>在原文 span 范围内按标识符边界找 token；找不到（如转换后的字面量）返回 null。</summary>
    private TextSpan? FindTokenInOriginal(TextSpan span, string token)
    {
        var start = Math.Max(0, span.Start);
        var end = Math.Min(Math.Max(start, span.End), _doc.Text.Length);
        if (end - start < token.Length) return null;
        for (var i = start; i <= end - token.Length; i++)
        {
            if (string.CompareOrdinal(_doc.Text, i, token, 0, token.Length) != 0) continue;
            if (!BoundaryOk(_doc.Text, i, token.Length, start, end)) continue;
            return new TextSpan(i, token.Length);
        }
        return null;
    }

    private static IEnumerable<int> TokenOccurrences(string text, string token)
    {
        for (var i = 0; i <= text.Length - token.Length; i++)
        {
            if (string.CompareOrdinal(text, i, token, 0, token.Length) != 0) continue;
            if (!BoundaryOk(text, i, token.Length, 0, text.Length)) continue;
            yield return i;
        }
    }

    /// <summary>token 两端不得与标识符字符粘连（"v" 不该命中 "var" 的首字母）。</summary>
    private static bool BoundaryOk(string text, int start, int length, int windowStart, int windowEnd)
    {
        var end = start + length;
        if (start > windowStart && IsIdentChar(text[start - 1]) && IsIdentChar(text[start])) return false;
        if (end < windowEnd && IsIdentChar(text[end - 1]) && IsIdentChar(text[end])) return false;
        return true;
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@';

    /// <summary>
    /// 根级动态构造写入实例方法，局部变量不可捕获；只提升真正被 U 写入的元素/run。
    /// </summary>
    private static bool NeedsElementField(UiElement element, EmitContext ctx)
    {
        if (!ctx.IsRoot) return false;
        if (element.Attributes.Any(attr => attr.ParamDef is { IsDelegate: false, IsContent: false } && attr.IsBind))
            return true;
        // E2：根级 Class={expr} 走 U{N} 定向更新，与普通动态属性同样需要字段提升。
        var dynamicAttr = element.Attributes.Any(attr =>
            (attr.PropDef is not null || attr.ParamDef is { IsDelegate: false, IsContent: false } || attr.AttachedDef is not null ||
             (attr.Name == "Class" && attr.Kind == AttrValueKind.Expression && attr.ClassNames is null)) &&
            !attr.IsBind &&
            attr.Name != "Key" && attr.Kind == AttrValueKind.Expression);
        if (dynamicAttr) return true;
        return element.ResolvedDef!.Content == UiContentModel.Text &&
               element.Children.OfType<UiText>().SelectMany(t => t.Segments)
                   .Any(s => s.Kind == TextSegmentKind.Expression);
    }

    private string DeclareElement(UiElement element, EmitContext ctx)
    {
        var clrType = element.ResolvedDef!.ClrTypeDisplayName;
        var n = _varCounter++;
        if (NeedsElementField(element, ctx))
        {
            _fields.Add(new FieldDecl(n, "el", clrType));
            var field = $"__el{n}";
            Line($"{field} = new {clrType}();");
            Line($"{ctx.ConstructionVar}.Track({field});");
            return field;
        }

        var local = $"el{n}";
        Line($"var {local} = new {clrType}();");
        Line($"{ctx.ConstructionVar}.Track({local});");
        return local;
    }

    private string CreateU(string statement, TextSpan span, int tokenOffset, int userLength = 0,
        IReadOnlyList<(TextSpan Span, int Offset, int Length)>? moreExpressions = null)
    {
        var name = $"U{_fnCounter++}";
        var sb = new StringBuilder();
        var savedOut = _out;
        var savedIndent = _indent;
        _out = sb;
        _indent = 2;
        Mapped(span, $"{statement};", tokenOffset, userLength, moreExpressions);
        _out = savedOut;
        _indent = savedIndent;
        _uMethods.Add(new UMethod(name, sb));
        return name;
    }

    /// <summary>组件级动态写入：U 方法 + Build 首次调用 + Render 登记。语句形态由调用方拼装。</summary>
    private void RootUpdate(string statement, TextSpan span, int tokenOffset, int userLength = 0,
        IReadOnlyList<(TextSpan Span, int Offset, int Length)>? moreExpressions = null)
    {
        var name = CreateU(statement, span, tokenOffset, userLength, moreExpressions);
        Line($"{name}();");
        _renderCalls.Add($"{name}();");
    }

    /// <summary>块内动态写入：R 局部函数 + 首次调用 + 作用域渲染动作。语句形态由调用方拼装。</summary>
    private void BlockUpdate(string statement, TextSpan span, int tokenOffset, EmitContext ctx, int userLength = 0,
        IReadOnlyList<(TextSpan Span, int Offset, int Length)>? moreExpressions = null)
    {
        var name = $"R{_fnCounter++}";
        var sb = new StringBuilder();
        var savedOut = _out;
        var savedIndent = _indent;
        _out = sb;
        _indent = savedIndent + 1;
        Mapped(span, $"{statement};", tokenOffset, userLength, moreExpressions);
        _out = savedOut;
        _indent = savedIndent;

        Line($"void {name}()");
        Line("{");
        AppendMapped(_out, sb);
        Line("}");
        Line($"{name}();");
        Line($"{ctx.ScopeVar}.AddRenderAction({name});");
    }

    /// <summary>块内无用户表达式的刷新动作（绑定 Refresh / 嵌套块 Refresh）。</summary>
    private void BlockRefresh(string target, EmitContext ctx)
    {
        var name = $"R{_fnCounter++}";
        Line($"void {name}()");
        Line("{");
        _indent++;
        Line($"{target}.Refresh();");
        _indent--;
        Line("}");
        Line($"{name}();");
        Line($"{ctx.ScopeVar}.AddRenderAction({name});");
    }

    /// <summary>
    /// 目录尚无处理器委托类型元数据；按表达式**语法形态**选 AddHandler 的两个重载
    /// （spec/02 §4.6.1 规则 2 固定这两种目标类型）。Tokenizer 切片：用 Roslyn 解析替代正则——
    /// 括号内/引号内的 "=&gt;"、嵌套 lambda、匿名方法不再是猜测。
    /// 解析失败/非 lambda 形态回落 Action（由 C# 编译器经 #line 兜回 .ui 报错）。
    /// internal：EUI024 签名探针必须复用同一结论，避免生成代码与诊断的类型判定漂移。
    /// </summary>
    internal static string EventHandlerType(string expression)
    {
        const string action = "global::System.Action";
        const string routeArgs = "global::System.Action<global::EclipseUI.RoutedEventArgs>";

        var expr = SyntaxFactory.ParseExpression(expression.Trim());
        var inner = expr;
        while (inner is ParenthesizedExpressionSyntax parenthesized)
            inner = parenthesized.Expression;

        if (inner is SimpleLambdaExpressionSyntax)
            return routeArgs; // x => ...（单参数）
        if (inner is ParenthesizedLambdaExpressionSyntax lambda)
            return lambda.ParameterList is { Parameters.Count: > 0 } ? routeArgs : action;
        if (inner is AnonymousMethodExpressionSyntax anonymous)
            return anonymous.ParameterList is { Parameters.Count: > 0 } ? routeArgs : action;
        return action;
    }

    private static TextSpan AttrSpan(UiAttribute attr) =>
        attr.ValueSpan.Length > 0 ? attr.ValueSpan : attr.NameSpan;

    /// <summary>E2：C# 字符串字面量转义（类名数组发射用）。</summary>
    private static string StaticStringLiteral(string text) =>
        "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>E4：类名数组发射文本（解析后的类名 + 塌缩注入的派生类名共用）。</summary>
    private static string ClassArrayLiteral(IEnumerable<string> names) =>
        "new global::System.String[] { " + string.Join(", ", names.Select(StaticStringLiteral)) + " }";

    /// <summary>
    /// Class= 的三种形态（字面量 / 空列表 + 塌缩注入 / 表达式）。控件与组件的差别只有「写到哪里」：
    /// 控件直接赋 <c>Classes</c>；组件的目标是根元素，经 <c>ApplyClasses</c> 与组件自身声明的类
    /// 合并（spec/02 §4.12.6）。<paramref name="call"/> 为真时目标已经是方法名。
    /// </summary>
    private void EmitClassAttribute(UiAttribute attr, UiElement element, string target,
        EmitContext ctx, bool call)
    {
        var pending = element.InjectedClasses;
        // E4：塌缩注入的派生类排在解析类名之后（design/17 §2.2 派生类胜）。
        if (attr.ClassNames is { Count: > 0 } names)
        {
            var all = pending is { Count: > 0 } ? names.Concat(pending) : (IEnumerable<string>)names;
            MappedToken(AttrSpan(attr), ClassStatement(target, ClassArrayLiteral(all), call) + ";", attr.Value.Trim());
        }
        else if (attr.ClassNames is not null)
        {
            // Class=""（空类列表）：仅注入命中时发注入数组；无注入保持 no-op
            if (pending is { Count: > 0 })
                Mapped(attr.ValueSpan, ClassStatement(target, ClassArrayLiteral(pending), call) + ";");
        }
        else if (attr.Kind == AttrValueKind.Expression)
        {
            // 表达式形态拿不到编译期类列表——塌缩注入跳过（design/17 §4.5-3 明示边界）
            var expr = attr.Value.Trim();
            var body = attr.ClassListKind == UiClassListKind.Collection
                ? $"global::System.Linq.Enumerable.ToArray({expr})"
                : $"{expr}.Split(new[] {{ ' ', '\\t', '\\r', '\\n' }}, global::System.StringSplitOptions.RemoveEmptyEntries)";
            // 赋值形态与字面量同源：控件写 Classes 左值，组件的目标已经是方法名。
            EmitDynamicUpdate(body, attr.ValueSpan, ctx,
                call ? target : $"{target}.Classes", interpolate: false, call: call);
        }
    }

    private static string ClassStatement(string target, string names, bool call) =>
        call ? $"{target}({names})" : $"{target}.Classes = {names}";

    private string EmitElement(UiElement element, string? parentVar, EmitContext ctx, string? receiver = null)
    {
        var def = element.ResolvedDef!;
        // The existing instance occupies the root ordinal; child identifiers remain stable.
        if (receiver is not null) _varCounter++;
        var v = receiver ?? DeclareElement(element, ctx);
        if (_doc.TemplateTarget is not null) Line($"{v}.SetTemplatedParent(Owner);");
        if (def.Content == UiContentModel.Children && !def.ContentIsSingle && def.ContentTarget is { } contentTarget)
            _contentTargets[v] = contentTarget;
        var contentParameter = def.Parameters.Values.FirstOrDefault(p => p.IsContent);
        if (receiver is not null)
        {
            // Constructor inputs and slots must be available when a code or UI base builds its content.
            foreach (var input in element.Attributes)
                if (input.ParamDef is { } parameter)
                    EmitParameterAttribute(input, parameter, v, ctx);
            if (contentParameter is not null) EmitContentParameter(element, v, contentParameter, ctx);
            EmitSlotFills(element, v, ctx);
            Line("base.InitializeUi();");
        }

        foreach (var attr in element.Attributes)
        {
            if (attr.Name == "Key") continue;
            if (attr.Name == "Part") { Line($"{v}.Part = {StaticStringLiteral(attr.Value)};"); continue; }
            var span = AttrSpan(attr);

            // C5：Ref（spec/02 §4.14）——Build 时把元素实例赋给 code-behind 字段（源码顺序位置）。
            if (attr.RefTarget is { } refField)
            {
                MappedToken(span, $"{refField} = {v};", refField);
                continue;
            }

            // E2（design/17 §4.8）：样式类——字面量经目录校验的类名数组；表达式按探针形态
            // 拆分/集合化。经 EmitDynamicUpdate 走 U{N}/R{N} 定向更新（Classes setter 幂等短路）。
            // E4：塌缩注入的派生类排在解析类名之后（design/17 §2.2 派生类胜）。
            if (attr.Name == "Class")
            {
                EmitClassAttribute(attr, element,
                    v + (receiver is null ? ".ApplyClasses" : ".ApplyTemplateClasses"), ctx, call: true);
                continue;
            }

            // E2：元素级内联样式——local 层直赋（最高优先级），每条声明独立 #line 映射到值 span。
            if (attr.Name == "Style" && attr.StyleSetters is { Count: > 0 } setters)
            {
                foreach (var (statement, valueSpan) in EustyleGenerator.EmitLocalSetters(setters, v, _styleProperties))
                    Mapped(valueSpan, _doc.TemplateTarget is not null ? $"using ({v}.BeginSkinValues()) {statement}" : receiver is null ? statement : $"using (this.BeginTemplateValues()) {statement}");
                continue;
            }

            // G4：附加属性（design/14 §14.5.5）——直调宿主静态 setter；绑定已由 EUI040 拒绝。
            if (attr.AttachedDef is { } attached)
            {
                if (attr.IsBind) continue;                        // EUI040 已报
                if (attr.Kind == AttrValueKind.Literal)
                {
                    if (attr.ConvertedLiteral is not null)        // EUI003 已报
                        MappedToken(span,
                            (_doc.TemplateTarget is not null ? $"using ({v}.BeginSkinValues()) " : receiver is null ? "" : "using (this.BeginTemplateValues()) ") +
                            $"{attached.OwnerClrName}.{attached.SetterMethod}({v}, {attr.ConvertedLiteral});",
                            attr.ConvertedLiteral);
                }
                else if (attr.Kind == AttrValueKind.Expression)
                {
                    EmitAttachedDynamic(attached, v, attr.Value, attr.ValueSpan, ctx, templateRoot: receiver is not null);
                }
                continue;
            }

            if (attr.EventDef is { } evt)
            {
                var handlerType = EventHandlerType(attr.Value);
                var h = $"h{_varCounter++}";
                MappedToken(span, $"var {h} = ({handlerType})({attr.Value.Trim()});", attr.Value.Trim());
                var wrapper = handlerType == "global::System.Action"
                    ? $"() => {{ {h}(); Invalidate(); }}"
                    : $"e => {{ {h}(e); Invalidate(); }}";
                Line($"{v}.{(receiver is null ? "AddHandler" : "AddTemplateHandler")}({evt.DeclaringTypeName}.{evt.FieldName}, {wrapper});");
            }
            else if (attr.ParamDef is { } parameter)
            {
                if (receiver is null) EmitParameterAttribute(attr, parameter, v, ctx);
            }
            else if (attr.PropDef is null)
            {
                // 未解析属性：Analyzer 已报 EUI002
            }
            else if (attr.IsBind)
            {
                EmitBinding(attr, attr.PropDef, v, ctx);
            }
            else if (attr.Kind == AttrValueKind.Literal)
            {
                var statement = _doc.TemplateTarget is not null ? $"using ({v}.BeginSkinValues()) {v}.{attr.Name} = {attr.ConvertedLiteral};" : receiver is null
                    ? $"{v}.{attr.Name} = {attr.ConvertedLiteral};"
                    : $"using (this.BeginTemplateValues()) {v}.{attr.Name} = {attr.ConvertedLiteral};";
                MappedToken(span, statement, attr.ConvertedLiteral ?? "");
            }
            else
            {
                EmitDynamicUpdate(attr.Value, attr.ValueSpan, ctx, $"{v}.{attr.Name}", interpolate: false,
                    propertyField: receiver is null ? null : $"{attr.PropDef.DeclaringTypeName}.{attr.PropDef.PropertyFieldName}");
            }
        }

        // E4：无 Class 属性但被塌缩注入命中的元素——发注入类列表（派生类直配）。
        if (element.InjectedClasses is { Count: > 0 } injected &&
            element.Attributes.All(static a => a.Name != "Class"))
            Line($"{v}." + (receiver is null ? "ApplyClasses" : "ApplyTemplateClasses") + $"({ClassArrayLiteral(injected)});");

        if (contentParameter is not null)
        {
            if (receiver is null) EmitContentParameter(element, v, contentParameter, ctx);
        }
        else switch (def.Content)
        {
            case UiContentModel.TextRuns:
                EmitTextRuns(element, v, ctx);
                break;
            case UiContentModel.Text:
                EmitLabel(element, v, def, ctx);
                break;
            case UiContentModel.Children:
                if (def.ContentIsSingle)
                {
                    var roots = EmitContentRoots(element.Children, ctx);
                    if (roots.Count > 0) Line($"{v}.{def.ContentTarget} = {roots[0]};");
                }
                else foreach (var child in element.Children)
                    EmitContainerChild(child, v, ctx);
                break;
        }

        if (receiver is null)
        {
            EmitSlotFills(element, v, ctx);
            Line($"{v}.EnsureInitialized();");
        }

        if (parentVar is not null)
            Line($"{ChildCollection(parentVar)}.Add({v});");
        return v;
    }

    private void EmitContentParameter(UiElement element, string target, UiParameterDef parameter, EmitContext ctx)
    {
        if (!element.Children.Any(c => c is UiElement or UiIf or UiForeach or UiSwitch)) return;
        var content = NewVar("content");
        Line($"var {content} = new global::System.Collections.Generic.List<global::EclipseUI.Element>();");
        EmitContentChildren(element.Children, content, EmitContext.Content(ctx));
        Line($"{target}.{parameter.Name} = " +
             (parameter.ContentIsMultiple ? content : $"{content}.Count == 0 ? null : {content}[0]") + ";");
    }

    private void EmitParameterAttribute(UiAttribute attr, UiParameterDef p, string target, EmitContext ctx)
    {
        if (p.IsContent || attr.Name == "Key") return;
        var span = AttrSpan(attr);
        if (p.IsDelegate)
        {
            if (attr.IsBind || attr.Kind != AttrValueKind.Expression) return;
            var handler = NewVar("h");
            MappedToken(span, $"var {handler} = ({p.ClrTypeDisplayName})({attr.Value.Trim()});", attr.Value.Trim());
            var parameters = string.Join(", ", Enumerable.Range(0, p.DelegateArity).Select(i => $"a{i}"));
            var invocation = $"{handler}({parameters})";
            var lambda = p.DelegateArity == 0 ? "()" : $"({parameters})";
            Line($"{target}.{p.Name} = {lambda} => {{ {invocation}; Invalidate(); }};");
        }
        else if (attr.IsBind && p.ChangedCallbackName is not null)
        {
            var expression = attr.Value.Trim();
            Mapped(span, $"{target}.{p.ChangedCallbackName} = value =>");
            Line("{");
            _indent++;
            Line($"if (!global::System.Collections.Generic.EqualityComparer<{p.ClrTypeDisplayName}>.Default.Equals({expression}, value))");
            Line("{");
            _indent++;
            MappedToken(span, $"{expression} = value;", expression);
            Line("Invalidate();");
            _indent--;
            Line("}");
            _indent--;
            Line("};");
            EmitParamPush(p, target, attr.Value, span, ctx);
        }
        else if (attr.Kind == AttrValueKind.Literal)
        {
            if (attr.ConvertedLiteral is not null)
                MappedToken(span, $"{target}.{p.Name} = {attr.ConvertedLiteral};", attr.ConvertedLiteral);
        }
        else if (ctx.IsContent)
            MappedToken(span, $"{target}.{p.Name} = ({p.ClrTypeDisplayName})({attr.Value.Trim()});", attr.Value.Trim());
        else EmitParamPush(p, target, attr.Value, span, ctx);
    }

    private void EmitSlotFills(UiElement element, string target, EmitContext ctx)
    {
        var fills = element.Children.OfType<UiSlotNode>().Where(f => f.ResolvedSlot is not null).ToList();
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fill in fills)
        {
            var slot = fill.ResolvedSlot!;
            if (!emitted.Add(slot.Name)) continue;
            var content = NewVar("slot");
            Mapped(fill.NameAttribute!.ValueSpan,
                $"var {content} = new global::System.Collections.Generic.List<global::EclipseUI.Element>();");
            foreach (var part in fills.Where(f => ReferenceEquals(f.ResolvedSlot, slot)))
                EmitContentChildren(part.Children, content, EmitContext.Content(ctx));
            Mapped(fill.NameAttribute.ValueSpan, $"{target}.{slot.PropertyName} = " +
                (slot.Cardinality == UiSlotCardinality.Multiple ? content : $"{content}.Count == 0 ? null : {content}[0]") + ";");
        }
    }

    /// <summary>组件参数动态推送（§4.3.3）：相等保护下写参数并失效子组件；根级 U{N}、块级 R{N}。</summary>
    private void EmitParamPush(UiParameterDef p, string target, string expression, TextSpan span, EmitContext ctx)
    {
        var type = p.ClrTypeDisplayName;
        if (ctx.IsRoot)
        {
            var name = $"U{_fnCounter++}";
            var sb = new StringBuilder();
            var savedOut = _out;
            var savedIndent = _indent;
            _out = sb;
            _indent = 2;
            MappedToken(span, $"var v = ({type})({expression.Trim()});", expression.Trim());
            Line($"if (!global::System.Collections.Generic.EqualityComparer<{type}>.Default.Equals({target}.{p.Name}, v))");
            Line("{");
            _indent++;
            Mapped(span, $"{target}.{p.Name} = v;");
            Line($"{target}.Invalidate();");
            _indent--;
            Line("}");
            _out = savedOut;
            _indent = savedIndent;
            _uMethods.Add(new UMethod(name, sb));
            Line($"{name}();");
            _renderCalls.Add($"{name}();");
            return;
        }

        var local = $"R{_fnCounter++}";
        var sbLocal = new StringBuilder();
        var savedOutLocal = _out;
        var savedIndentLocal = _indent;
        _out = sbLocal;
        _indent = savedIndentLocal + 1;
        MappedToken(span, $"var v = ({type})({expression.Trim()});", expression.Trim());
        Line($"if (!global::System.Collections.Generic.EqualityComparer<{type}>.Default.Equals({target}.{p.Name}, v))");
        Line("{");
        _indent++;
        Mapped(span, $"{target}.{p.Name} = v;");
        Line($"{target}.Invalidate();");
        _indent--;
        Line("}");
        _out = savedOutLocal;
        _indent = savedIndentLocal;

        Line($"void {local}()");
        Line("{");
        AppendMapped(_out, sbLocal);
        Line("}");
        Line($"{local}();");
        Line($"{ctx.ScopeVar}.AddRenderAction({local});");
    }

    /// <summary>组件内容子节点（§4.12.3）：元素按普通构造，@if/@foreach/@switch 生成普通 C# 语句。</summary>
    private void EmitContentChildren(List<UiNode> children, string contentVar, EmitContext ctx)
    {
        foreach (var child in children)
        {
            switch (child)
            {
                case UiElement el:
                    var root = EmitElement(el, parentVar: null, ctx);
                    Line($"{contentVar}.Add({root});");
                    break;
                case UiIf ifNode:
                    EmitPlainIf(ifNode, contentVar, ctx);
                    break;
                case UiForeach foreachNode:
                    EmitPlainForeach(foreachNode, contentVar, ctx);
                    break;
                case UiSwitch switchNode:
                    EmitPlainSwitch(switchNode, contentVar, ctx);
                    break;
                case UiText:
                    break; // Analyzer 已报 EUI006
                case UiSlotNode or UiContentSlotNode:
                    break; // 槽节点单独发射（§4.12.3/§4.12.5）；Analyzer 已报位置
            }
        }
    }

    /// <summary>内容内 @if：Build 时按当前值展开的普通 C# if（§4.12.3 规则 7）；else 链同构（K23 ③）。</summary>
    private void EmitPlainIf(UiIf node, string contentVar, EmitContext ctx)
    {
        MappedToken(node.ConditionSpan, $"if ({node.Condition.Trim()})", node.Condition.Trim());
        Line("{");
        _indent++;
        EmitContentChildren(node.Children, contentVar, ctx);
        _indent--;
        Line("}");
        foreach (var elseIf in node.ElseIfs)
        {
            MappedToken(elseIf.ConditionSpan, $"else if ({elseIf.Condition.Trim()})", elseIf.Condition.Trim());
            Line("{");
            _indent++;
            EmitContentChildren(elseIf.Children, contentVar, ctx);
            _indent--;
            Line("}");
        }
        if (node.ElseChildren is not null)
        {
            Line("else");
            Line("{");
            _indent++;
            EmitContentChildren(node.ElseChildren, contentVar, ctx);
            _indent--;
            Line("}");
        }
    }

    /// <summary>内容内 @foreach：Build 时展开的普通 C# foreach（§4.12.3 规则 7）。</summary>
    private void EmitPlainForeach(UiForeach node, string contentVar, EmitContext ctx)
    {
        var fqItem = _itemTypes[node.CollectionSpan.Start];
        var itemVar = node.VarName.Length > 0 ? node.VarName : "item";
        MappedToken(node.CollectionSpan, $"foreach (var {itemVar} in {node.Collection.Trim()})",
            node.Collection.Trim());
        Line("{");
        _indent++;
        EmitContentChildren(node.Children, contentVar, ctx);
        _indent--;
        Line("}");
    }

    /// <summary>
    /// 内容内 @switch：Build 时展开的普通 C# switch 语句（§4.12.3 同构；K29 分支体为内容模板，
    /// 每 section 以生成的 break; 结尾）。default 无映射；case 行映射到标签 span。
    /// </summary>
    private void EmitPlainSwitch(UiSwitch node, string contentVar, EmitContext ctx)
    {
        MappedToken(node.ValueSpan, $"switch ({node.Value.Trim()})", node.Value.Trim());
        Line("{");
        _indent++;
        foreach (var section in node.Sections)
        {
            if (section.IsDefault)
                Line("default:");
            else
                MappedToken(section.LabelSpan, $"case {section.Label}:", section.Label);
            _indent++;
            EmitContentChildren(section.Children, contentVar, ctx);
            Line("break;");
            _indent--;
        }
        _indent--;
        Line("}");
    }

    /// <summary>
    /// <c>&lt;ContentSlot&gt;</c> 宿主拼接（§4.12.5）：不产生运行时元素——在组件自身 Build 的该位置
    /// 直接把插槽属性装入父容器；Single 逐一次（属性 null 则跳过），Multiple 读取一次逐项加入。
    /// </summary>
    private void EmitContentSlotSplice(UiContentSlotNode host, string parentVar)
    {
        var slot = _ownSlots.TryGetValue(host.Name, out var ownSlot)
            ? ownSlot                      // 精确匹配 code-behind 的 def 为发射事实源
            : host.ResolvedSlot;           // 分析器短名近似（头显测试/回退）
        if (slot is null || parentVar is null) return; // Analyzer 已报 EUI022

        var s = NewVar("slot");
        var span = host.NameAttribute!.ValueSpan;
        if (slot.Cardinality == UiSlotCardinality.Single)
        {
            Mapped(span, $"if ({slot.PropertyName} is {{ }} {s})");
            Line("{");
            _indent++;
            Mapped(span, $"{ChildCollection(parentVar)}.Add({s});");
            _indent--;
            Line("}");
        }
        else
        {
            Mapped(span, $"foreach (var {s} in {slot.PropertyName})");
            Line("{");
            _indent++;
            Mapped(span, $"{ChildCollection(parentVar)}.Add({s});");
            _indent--;
            Line("}");
        }
    }

    private void EmitBinding(UiAttribute attr, UiPropDef propDef, string elementVar, EmitContext ctx)
    {
        if (ctx.IsContent) return; // Analyzer 已报 EUI040：内容内不生成绑定
        var expression = attr.Value.Trim();
        var call =
            $"global::EclipseUI.UiBinding.TwoWay({elementVar}, {propDef.DeclaringTypeName}.{propDef.PropertyFieldName}, " +
            $"readTarget: () => {elementVar}.{attr.Name}, writeTarget: v => {elementVar}.{attr.Name} = v, " +
            $"readSource: () => {expression}, writeSource: v => {expression} = v, " +
            "invalidate: Invalidate)";
        var span = attr.ValueSpan;

        if (ctx.IsRoot)
        {
            var n = _varCounter++;
            var field = $"__bind{n}";
            _fields.Add(new FieldDecl(n, "bind", "global::EclipseUI.UiBinding"));
            MappedToken(span, $"{field} = {call};", expression);
            Line($"Track({field});");
            _renderCalls.Add($"{field}.Refresh();");
            return;
        }

        var local = $"bind{_varCounter++}";
        MappedToken(span, $"var {local} = {call};", expression);
        Line($"{ctx.ScopeVar}.AddDisposable({local});");
        BlockRefresh(local, ctx);
    }

    private string ChildCollection(string parent) =>
        parent + "." + (_contentTargets.TryGetValue(parent, out var target) ? target : "Children");

    private void EmitContainerChild(UiNode child, string parentVar, EmitContext ctx)
    {
        switch (child)
        {
            case UiElement element:
                EmitElement(element, parentVar, ctx);
                break;
            case UiIf ifNode:
                EmitIf(ifNode, parentVar, ctx);
                break;
            case UiForeach foreachNode:
                EmitForeach(foreachNode, parentVar, ctx);
                break;
            case UiSwitch switchNode:
                EmitSwitch(switchNode, parentVar, ctx);
                break;
            case UiContentSlotNode host:
                EmitContentSlotSplice(host, parentVar);
                break;
            case UiText:
                break; // Analyzer 已报错
        }
    }

    private void EmitTextRuns(UiElement element, string elementVar, EmitContext ctx)
    {
        foreach (var child in element.Children)
        {
            if (child is not UiText text) continue; // Analyzer 已报错
            foreach (var segment in text.Segments)
            {
                if (segment.Kind == TextSegmentKind.Static)
                {
                    // 词法 FlushText 的 Span 会越过静态文本伸进后随的 {expr}（Lexer.cs FlushText
                    // 用 _i 作终点）；映射原文若照单全收会与表达式映射重叠而被双向拒绝。
                    // #line 仍用原 Span（golden 不变），映射原文钳到实际文本长度。
                    Mapped(segment.Span,
                        $"((global::EclipseUI.ITextContentHost){elementVar}).AddRun({UiLiterals.EscapeString(segment.Text)});",
                        mappingSpan: new TextSpan(segment.Span.Start, segment.Text.Length));
                    continue;
                }

                string run;
                if (ctx.IsRoot)
                {
                    var n = _varCounter++;
                    run = $"__run{n}";
                    _fields.Add(new FieldDecl(n, "run", "global::EclipseUI.Elements.TextRun"));
                    Line($"{run} = ((global::EclipseUI.ITextContentHost){elementVar}).AddRun(\"\");");
                }
                else
                {
                    run = NewVar("run");
                    Line($"var {run} = ((global::EclipseUI.ITextContentHost){elementVar}).AddRun(\"\");");
                }

                EmitDynamicUpdate(segment.Text, segment.Span, ctx, $"{run}.Text", interpolate: true);
            }
        }
    }

    private void EmitLabel(UiElement element, string elementVar, UiElementDef def, EmitContext ctx)
    {
        var target = def.ContentTarget;
        if (target is null) return; // 目录缺文本目标（EUI021，C3）

        var segments = element.Children.OfType<UiText>().SelectMany(t => t.Segments).ToList();
        if (segments.Count == 0) return;

        if (segments.All(s => s.Kind == TextSegmentKind.Static))
        {
            var combined = string.Concat(segments.Select(s => s.Text));
            Mapped(segments[0].Span, $"{elementVar}.{target} = {UiLiterals.EscapeString(combined)};");
            return;
        }

        var parts = new StringBuilder("$\"");
        var prefix = $"{elementVar}.{target}";
        var firstExprSpan = segments[0].Span;
        int firstExprOffset = 0;
        int firstExprLength = 0;
        // 一行插值多个表达式：#line 只对齐首个（golden 不变），SourceMapping 每个都记。
        var moreExpressions = new List<(TextSpan Span, int Offset, int Length)>();
        bool seenExpr = false;
        foreach (var segment in segments)
        {
            if (segment.Kind == TextSegmentKind.Static)
            {
                parts.Append(EscapeInterpolation(segment.Text));
                continue;
            }
            var trimmedExpr = segment.Text.Trim();
            // 表达式在生成语句里的偏移：前缀 + " = " + '$"…{' + '('。
            var offset = prefix.Length + 3 + parts.Length + 2;
            if (!seenExpr)
            {
                firstExprSpan = segment.Span;
                firstExprOffset = offset;
                firstExprLength = trimmedExpr.Length;
                seenExpr = true;
            }
            else
            {
                moreExpressions.Add((segment.Span, offset, trimmedExpr.Length));
            }
            parts.Append('{').Append('(').Append(trimmedExpr).Append(')').Append('}');
        }
        parts.Append('"');

        var body = parts.ToString();
        if (ctx.IsRoot)
            RootUpdate($"{prefix} = {body}", firstExprSpan, firstExprOffset, firstExprLength, moreExpressions);
        else
            BlockUpdate($"{prefix} = {body}", firstExprSpan, firstExprOffset, ctx, firstExprLength, moreExpressions);
    }

    /// <summary>
    /// 动态写入的两种语句形态：赋值 <c>target = body</c>（默认）与调用 <c>target(body)</c>
    /// （<paramref name="call"/>，组件标签的 <c>Class={expr}</c> 走 <c>ApplyClasses</c>）。
    /// </summary>
    private void EmitDynamicUpdate(string expression, TextSpan span, EmitContext ctx,
        string target, bool interpolate, bool call = false, string? propertyField = null)
    {
        var trimmed = expression.Trim();
        // 插值文本 $\"{(expr)}\" 的表达式前缀是 $ \" { ( 共 4 个字符。
        var body = interpolate ? $"$\"{{({trimmed})}}\"" : trimmed;
        var bodyOffset = interpolate ? 4 : 0;
        var prefix = propertyField is not null ? $"using (this.BeginTemplateValues()) {target} = " :
            call ? $"{target}(" : $"{target} = ";
        if (_doc.TemplateTarget is not null && !call)
            prefix = $"using ({target.Substring(0, target.LastIndexOf('.'))}.BeginSkinValues()) {target} = ";
        var suffix = call ? ")" : "";
        var statement = $"{prefix}{body}{suffix}";

        if (ctx.IsContent)
        {
            // 组件内容是 build-once 快照：直接赋值，不生成 U/R（spec/02 §4.12.3 规则 8）。
            MappedToken(span, $"{statement};", trimmed);
            return;
        }
        if (ctx.IsRoot)
            RootUpdate(statement, span, prefix.Length + bodyOffset, trimmed.Length);
        else
            BlockUpdate(statement, span, prefix.Length + bodyOffset, ctx, trimmed.Length);
    }

    /// <summary>
    /// G4：附加属性的动态写入（design/14 §14.5.5）——目标不是赋值左值而是静态调用
    /// <c>Owner.Setter(el, expr)</c>，语句形态由本方法拼装；用户表达式前的生成前缀
    /// 即调用前缀，供 #line 列映射。内容上下文仍是 build-once 快照。
    /// </summary>
    private void EmitAttachedDynamic(UiAttachedPropDef attached, string v,
        string expression, TextSpan span, EmitContext ctx, bool templateRoot = false)
    {
        var trimmed = expression.Trim();
        var prefix = (_doc.TemplateTarget is not null ? $"using ({v}.BeginSkinValues()) " : templateRoot ? "using (this.BeginTemplateValues()) " : "") +
            $"{attached.OwnerClrName}.{attached.SetterMethod}({v}, ";
        var statement = $"{prefix}{trimmed})";

        if (ctx.IsContent)
        {
            Mapped(span, $"{statement};", prefix.Length, trimmed.Length);
            return;
        }
        if (ctx.IsRoot)
            RootUpdate(statement, span, prefix.Length, trimmed.Length);
        else
            BlockUpdate(statement, span, prefix.Length, ctx, trimmed.Length);
    }

    private string EmitIf(UiIf node, string? parentVar, EmitContext ctx)
    {
        bool root = ctx.IsRoot;
        string v;
        if (root)
        {
            var n = _varCounter++;
            v = $"__blk{n}";
            _fields.Add(new FieldDecl(n, "blk", "global::EclipseUI.ConditionalBlock"));
            Line($"{v} = new global::EclipseUI.ConditionalBlock();");
        }
        else
        {
            v = NewVar("blk");
            Line($"var {v} = new global::EclipseUI.ConditionalBlock();");
        }

        Line($"{ctx.ConstructionVar}.Track({v});");

        if (parentVar is not null)
            Line($"{ChildCollection(parentVar)}.Add({v});");

        if (!node.HasElseChain)
        {
            // 单分支：SetCondition/SetContent（v2 形态，golden 稳定）
            EmitBranchContent(node.ConditionSpan, node.Condition, node.Children, $"{v}.SetCondition", $"{v}.SetContent", ctx);
        }
        else
        {
            // 多分支（K23 ③ / D87）：AddBranch（if / else if）+ SetDefault（else）。
            // 每个 AddBranch 调用行映射到对应条件 span（design/16 附录 B.2）；SetDefault 无条件不映射。
            EmitBranchContent(node.ConditionSpan, node.Condition, node.Children, $"{v}.AddBranch", $"{v}.AddBranch", ctx);
            foreach (var elseIf in node.ElseIfs)
                EmitBranchContent(elseIf.ConditionSpan, elseIf.Condition, elseIf.Children, $"{v}.AddBranch", $"{v}.AddBranch", ctx);
            if (node.ElseChildren is not null)
                EmitDefaultBranch(node.ElseChildren, $"{v}.SetDefault", ctx);
        }

        if (root)
        {
            Line($"Track({v});");
            Line($"{v}.Refresh();");
            _renderCalls.Add($"{v}.Refresh();");
        }
        else
        {
            Line($"{ctx.ScopeVar}.AddDisposable({v});");
            BlockRefresh(v, ctx);
        }

        return v;
    }

    /// <summary>
    /// 单个分支的发射：condition + 内容 lambda。单分支用 SetCondition/SetContent 两个调用；
    /// 多分支统一为 AddBranch(() => cond, scopeN => { … })，lambda 空体也生成（分支完整性）。
    /// </summary>
    private void EmitBranchContent(TextSpan conditionSpan, string condition, List<UiNode> children,
        string conditionCall, string contentCall, EmitContext ctx)
    {
        var trimmed = condition.Trim();
        if (string.Equals(conditionCall, contentCall, StringComparison.Ordinal))
        {
            // 多分支 AddBranch：条件与内容在一条调用里
            var scopeVar = NewVar("scope");
            var blockCtx = EmitContext.Block(scopeVar);
            MappedToken(conditionSpan, $"{contentCall}(() => {trimmed}, {scopeVar} =>", trimmed);
            Line("{");
            _indent++;
            EmitBlockRoots(children, blockCtx);
            _indent--;
            Line("});");
            return;
        }

        // 单分支：SetCondition 行映射条件 span；SetContent 仅在非空时生成（v2 golden 形态）
        MappedToken(conditionSpan, $"{conditionCall}(() => {trimmed});", trimmed);
        if (children.Count == 0) return;
        var scope = NewVar("scope");
        var branchCtx = EmitContext.Block(scope);
        Line($"{contentCall}({scope} =>");
        Line("{");
        _indent++;
        EmitBlockRoots(children, branchCtx);
        _indent--;
        Line("});");
    }

    /// <summary>else / default 分支：无条件表达式，调用行不映射（没有用户表达式）。</summary>
    private void EmitDefaultBranch(List<UiNode> children, string call, EmitContext ctx)
    {
        var scopeVar = NewVar("scope");
        var blockCtx = EmitContext.Block(scopeVar);
        Line($"{call}({scopeVar} =>");
        Line("{");
        _indent++;
        EmitBlockRoots(children, blockCtx);
        _indent--;
        Line("});");
    }

    private string EmitForeach(UiForeach node, string? parentVar, EmitContext ctx)
    {
        var fqItem = _itemTypes[node.CollectionSpan.Start];
        bool root = ctx.IsRoot;
        string v;
        if (root)
        {
            var n = _varCounter++;
            v = $"__blk{n}";
            _fields.Add(new FieldDecl(n, "blk", $"global::EclipseUI.ForEachBlock<{fqItem}>"));
            Line($"{v} = new global::EclipseUI.ForEachBlock<{fqItem}>();");
        }
        else
        {
            v = NewVar("blk");
            Line($"var {v} = new global::EclipseUI.ForEachBlock<{fqItem}>();");
        }

        Line($"{ctx.ConstructionVar}.Track({v});");

        if (parentVar is not null)
            Line($"{ChildCollection(parentVar)}.Add({v});");

        MappedToken(node.CollectionSpan, $"{v}.SetItems(() => {node.Collection.Trim()});",
            node.Collection.Trim());

        var itemVar = node.VarName.Length > 0 ? node.VarName : "item";

        var firstElement = node.Children.OfType<UiElement>().FirstOrDefault();
        var keyAttr = firstElement?.Attributes.FirstOrDefault(a => a.Name == "Key");
        if (keyAttr is not null && !string.IsNullOrWhiteSpace(keyAttr.Value))
        {
            MappedToken(keyAttr.ValueSpan,
                $"{v}.SetKeySelector(({fqItem} {itemVar}) => (object?)({keyAttr.Value.Trim()}));",
                keyAttr.Value.Trim());
        }

        var scopeVar = NewVar("scope");
        var blockCtx = EmitContext.Block(scopeVar);
        Line($"{v}.SetTemplate(({fqItem} {itemVar}, global::EclipseUI.BlockScope {scopeVar}) =>");
        Line("{");
        _indent++;
        EmitBlockRoots(node.Children, blockCtx);
        _indent--;
        Line("});");

        if (root)
        {
            Line($"Track({v});");
            Line($"{v}.Refresh();");
            _renderCalls.Add($"{v}.Refresh();");
        }
        else
        {
            Line($"{ctx.ScopeVar}.AddDisposable({v});");
            BlockRefresh(v, ctx);
        }

        return v;
    }

    /// <summary>
    /// @switch（K29 / D87）：SwitchBlock&lt;T&gt; + SetSelector / AddCase / SetDefault。
    /// T 来自探针对 selector 的类型推断（失败退化为 object，交 C# 经 #line 兜底）；
    /// case 标签是编译期常量表达式原文（K29 ②），AddCase 调用行映射到标签 span；
    /// SetDefault 无用户表达式不映射。
    /// </summary>
    private string EmitSwitch(UiSwitch node, string? parentVar, EmitContext ctx)
    {
        var t = _switchValueTypes.TryGetValue(node.ValueSpan.Start, out var vt)
            ? vt
            : "global::System.Object";
        bool root = ctx.IsRoot;
        string v;
        if (root)
        {
            var n = _varCounter++;
            v = $"__blk{n}";
            _fields.Add(new FieldDecl(n, "blk", $"global::EclipseUI.SwitchBlock<{t}>"));
            Line($"{v} = new global::EclipseUI.SwitchBlock<{t}>();");
        }
        else
        {
            v = NewVar("blk");
            Line($"var {v} = new global::EclipseUI.SwitchBlock<{t}>();");
        }

        Line($"{ctx.ConstructionVar}.Track({v});");

        if (parentVar is not null)
            Line($"{ChildCollection(parentVar)}.Add({v});");

        MappedToken(node.ValueSpan, $"{v}.SetSelector(() => {node.Value.Trim()});", node.Value.Trim());

        foreach (var section in node.Sections)
        {
            if (section.IsDefault)
            {
                EmitDefaultBranch(section.Children, $"{v}.SetDefault", ctx);
                continue;
            }
            var scopeVar = NewVar("scope");
            var blockCtx = EmitContext.Block(scopeVar);
            MappedToken(section.LabelSpan, $"{v}.AddCase({section.Label}, {scopeVar} =>", section.Label);
            Line("{");
            _indent++;
            EmitBlockRoots(section.Children, blockCtx);
            _indent--;
            Line("});");
        }

        if (root)
        {
            Line($"Track({v});");
            Line($"{v}.Refresh();");
            _renderCalls.Add($"{v}.Refresh();");
        }
        else
        {
            Line($"{ctx.ScopeVar}.AddDisposable({v});");
            BlockRefresh(v, ctx);
        }

        return v;
    }

    private void EmitBlockRoots(List<UiNode> children, EmitContext ctx)
    {
        Line($"using var {ctx.ConstructionVar} = new global::EclipseUI.UiConstructionScope();");
        Line("try");
        Line("{");
        _indent++;
        var roots = EmitContentRoots(children, ctx);
        foreach (var contentRoot in roots)
            Line($"{ctx.ScopeVar}.AddRoot({contentRoot});");
        Line($"{ctx.ConstructionVar}.Complete();");
        _indent--;
        EmitConstructionCatch(ctx.ConstructionVar);
    }

    private void EmitConstructionCatch(string scope)
    {
        Line("}");
        Line("catch (global::System.Exception __euiConstructionError)");
        Line("{");
        _indent++;
        Line($"{scope}.Abort(__euiConstructionError);");
        Line("throw;");
        _indent--;
        Line("}");
    }

    private List<string> EmitContentRoots(List<UiNode> children, EmitContext ctx)
    {
        var roots = new List<string>();
        foreach (var child in children)
        {
            switch (child)
            {
                case UiElement element:
                    roots.Add(EmitElement(element, parentVar: null, ctx));
                    break;
                case UiIf ifNode:
                    roots.Add(EmitIf(ifNode, parentVar: null, ctx));
                    break;
                case UiForeach foreachNode:
                    roots.Add(EmitForeach(foreachNode, parentVar: null, ctx));
                    break;
                case UiSwitch switchNode:
                    roots.Add(EmitSwitch(switchNode, parentVar: null, ctx));
                    break;
                case UiSlotNode or UiContentSlotNode:
                    break; // Analyzer 已报 EUI022（槽不进块，§4.12.5）
            }
        }
        return roots;
    }

    private static string EscapeInterpolation(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("{", "{{").Replace("}", "}}");
}
