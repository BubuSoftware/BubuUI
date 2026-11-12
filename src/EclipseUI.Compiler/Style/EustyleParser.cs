using System;
using System.Collections.Generic;
using System.Globalization;

namespace EclipseUI.Compiler;

/// <summary>
/// Eustyle 解析器（E1，design/17 §4）：SCSS 子集 → 规则列表。语法：
/// <code>
/// $var: value;                        // 编译期变量（解析时替换，先声明先用）
/// @states {{ name; name; }}           // 注册制伪类声明（E5，design/17 §3.2）
/// .name[:state][:state] { prop: value;  &amp;:state { prop: value; ... } }
/// .panel { &gt; Button { … }  TextBlock.title { … }  &gt; Button:hover { … } }   // E4 嵌套
/// </code>
/// value := theme(color, color) | #rgb | #rrggbb | #rrggbbaa | number | $var
///        | keyword（枚举成员，按属性目录成员表校验）| "string"（design/19 §5.1）
/// state := hover | active | pressed | focus | disabled（active 是 pressed 的 CSS 别名）
///        | @states 声明的注册名（E5；本文件声明优先，未声明查 <paramref name="externalStates"/>）
/// 注释 := // 行注释 | /* 块注释 */
///
/// 属性名就是控件/属性对象的原始 CLR 名（<c>CornerRadius</c>），<b>大小写敏感、精确匹配</b>：
/// 无别名、无归一化，<c>corner-radius</c> / <c>cornerradius</c> 一律报 EUI051。
/// 可写属性集、值形态与目标字段一律来自编译期符号派生的 <see cref="StylePropertyCatalog"/>。
/// 诊断：EUI050（语法 / 不支持形态 / 注册名未声明）、EUI051（未知属性）、EUI052（值类型不符，含未定义变量）。
/// 嵌套结构选择器（E4，design/17 §4.5）只产结构（<see cref="EustyleNested"/>）——塌缩命名、
/// 冲突查重（EUI053）与注入规则由 <c>EustyleCollapse</c> 统一完成；变体块内不允许嵌套选择器。
/// <c>@states</c> 采用预扫两遍解析（声明序无关——CSS 声明先于/后于使用都合法）；
/// 声明名是全局掩码命名空间（运行时全局注册，不做类名式组件前缀）。
/// </summary>
public static class Eustyle
{
    private static readonly Dictionary<string, EustyleVariant> States =
        new(StringComparer.Ordinal)
        {
            ["hover"] = EustyleVariant.Hover,
            ["pressed"] = EustyleVariant.Pressed,
            ["active"] = EustyleVariant.Pressed,   // CSS :active 别名
            ["focus"] = EustyleVariant.Focus,
            ["disabled"] = EustyleVariant.Disabled,
        };

    /// <summary>平台保留伪类名（E5）：@states 不得重声明，运行时 Register 同名单拒绝。</summary>
    internal static readonly string[] PlatformStateNames =
        ["hover", "pressed", "active", "focus", "disabled"];

    /// <summary>
    /// 解析样式表源码。<paramref name="spanOffset"/> 是源码在宿主文件内的起始偏移
    /// （@style 块传 <c>UiStyleBlock.CodeSpan.Start</c>，.eustyle 文件传 0）——
    /// 产出的诊断 span 一律是宿主文件的绝对 span。<paramref name="externalStates"/> 是
    /// 全局目录里已声明的注册名（其他样式来源的 @states 并集，E5）——本文件未声明时兜底校验。
    /// <paramref name="properties"/> 是编译期从符号派生的可样式属性目录（唯一事实源）。
    /// </summary>
    public static EustyleStylesheet Parse(string text, string filePath, List<UiDiagnostic> diagnostics,
        StylePropertyCatalog properties, int spanOffset = 0, IReadOnlyCollection<string>? externalStates = null)
        => new Impl(text, diagnostics, spanOffset, externalStates, properties).Document();

    /// <summary>
    /// 只收集 <c>@states</c> 声明（E5 第一级目录收集）：预扫全文——不做规则解析，
    /// 便宜且声明序无关。诊断（撞平台保留名等）照常产出，调用方决定报告或丢弃。
    /// </summary>
    public static IReadOnlyList<EustyleStateDecl> CollectStates(string text, string filePath,
        List<UiDiagnostic> diagnostics, int spanOffset = 0)
        // 预扫只看 @states 块，不解析声明——属性目录不参与，传空目录即可。
        => new Impl(text, diagnostics, spanOffset, null, StylePropertyCatalog.Empty).SnapshotDeclaredStates();

    /// <summary>
    /// 解析元素级 <c>Style="…"</c> 的声明序列（E2，design/17 §4.8②）：
    /// 只接受 <c>prop: value;</c> 声明——类规则、<c>$变量</c>（块级概念）与变体块不适用，
    /// 状态外观一律走类名通道。诊断码与样式表一致（EUI050/EUI051/EUI052），
    /// span 经 <paramref name="spanOffset"/> 映射回宿主 .ui 文件。
    /// </summary>
    public static EustyleDeclarations ParseDeclarations(string text, string filePath, List<UiDiagnostic> diagnostics,
        StylePropertyCatalog properties, int spanOffset = 0)
        => new Impl(text, diagnostics, spanOffset, externalStates: null, properties: properties,
            scanStates: false).DeclarationList();

    private sealed class Impl
    {
        private readonly string _s;
        private readonly List<UiDiagnostic> _diags;
        private readonly int _offset;
        private readonly Dictionary<string, EustyleValue> _vars =
            new(StringComparer.Ordinal);
        private readonly IReadOnlyCollection<string> _externalStates;
        private readonly StylePropertyCatalog _properties;
        private readonly List<EustyleStateDecl> _declaredStates = [];
        private int _i;

        public Impl(string s, List<UiDiagnostic> diags, int offset, IReadOnlyCollection<string>? externalStates,
            StylePropertyCatalog properties, bool scanStates = true)
        {
            _s = s;
            _diags = diags;
            _offset = offset;
            _externalStates = externalStates ?? Array.Empty<string>();
            _properties = properties ?? StylePropertyCatalog.Empty;
            // E5 预扫：@states 声明序无关（先声明先可用）。Style= 的声明序列不开预扫——
            // 元素属性没有状态语境，@ 块应走 '@' 分支报错而不是被预扫静默吞掉。
            if (scanStates) CollectStates();
        }

        // ---------- 顶层 ----------

        public EustyleStylesheet Document()
        {
            _i = 0; // 预扫已推进 _i——正式解析从头开始（@states 块本体由 '@' 分支消费）
            var rules = new List<EustyleRule>();
            while (true)
            {
                SkipTrivia();
                if (Eof) break;
                var c = _s[_i];
                if (c == '$') { ParseVarDecl(); continue; }
                if (c == '@')
                {
                    // @states 已预扫收集并报告；此处只消费整块防二次解析。其他 @ 一律报错。
                    if (MatchesWord(_s, _i + 1, "states")) ConsumeStatesBlock();
                    else
                    {
                        Error(_i, _i + 1,
                            "unexpected '@'; only '@states { ... }' blocks are supported at the top level");
                        SkipTo(';', '}');
                    }
                    continue;
                }
                if (c == '.')
                {
                    var rule = ParseRule();
                    if (rule is not null) rules.Add(rule);
                    continue;
                }
                Error(_i, _i + 1,
                    $"unexpected character '{Printable(c)}'; expected a class rule '.name {{ ... }}', an '@states' block or a '$variable' declaration");
                SkipTo(';', '}');
            }
            return new EustyleStylesheet(rules) { States = _declaredStates };
        }

        // ---------- E5：@states 预扫与状态解析 ----------

        /// <summary>
        /// 预扫全文收集 <c>@states</c> 声明（两遍解析的第一遍）：跳过注释逐字符扫描，
        /// 命中 <c>@states</c> 后完整解析声明块（分隔符 ';' ',' 空白容忍）。撞平台保留名
        /// 报 EUI050；重复名去重。声明序无关——写在规则之后同样先于规则解析可用。
        /// </summary>
        private void CollectStates()
        {
            var i = 0;
            var s = _s;
            while (i < s.Length)
            {
                var c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '/' && i + 1 < s.Length)
                {
                    if (s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
                    if (s[i + 1] == '*')
                    {
                        var close = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                        i = close < 0 ? s.Length : close + 2;
                        continue;
                    }
                }
                if (c == '@' && MatchesWord(s, i + 1, "states"))
                {
                    _i = i;
                    ParseStatesBlock();
                    i = _i;
                    continue;
                }
                i++;
            }
        }

        /// <summary>解析一个 <c>@states {{ ... }}</c> 声明块（_i 定位在 '@'）。</summary>
        private void ParseStatesBlock()
        {
            var start = _i;
            _i += 7; // '@states'
            SkipTrivia();
            if (!Take('{'))
            {
                Error(start, _i, "expected '{' to open the @states block: @states { name; name; }");
                SkipTo(';', '}');
                return;
            }
            while (true)
            {
                SkipTrivia();
                if (Eof)
                {
                    Error(start, _i, "unterminated @states block; add the closing '}'");
                    return;
                }
                var c = _s[_i];
                if (c == '}') { _i++; return; }
                if (c == ';' || c == ',') { _i++; continue; }
                if (c == '/') { SkipTrivia(); continue; } // SkipTrivia 跳过注释
                var nameStart = _i;
                var name = Ident();
                if (name.Length == 0)
                {
                    Error(_i, _i + 1,
                        $"unexpected character '{Printable(c)}' in @states block; expected state names separated by ';' or ','");
                    SkipTo(';', '}');
                    continue;
                }
                if (Array.IndexOf(PlatformStateNames, name) >= 0)
                    ErrorSpan(SpanAt(nameStart, name.Length),
                        $"'{name}' is a platform pseudo-class (hover/active/pressed/focus/disabled) " +
                        "and cannot be redeclared in @states (EUI050)",
                        UiDiagnostics.StyleSyntax);
                else if (!DeclaredContains(name))
                    _declaredStates.Add(new EustyleStateDecl(name, SpanAt(nameStart, name.Length)));
                SkipTrivia();
            }
        }

        /// <summary>正式解析时消费一个已预扫的 @states 块（只前进 _i，收集与报告都在预扫完成）。</summary>
        private void ConsumeStatesBlock()
        {
            _i += 7; // '@states'
            SkipTrivia();
            if (!Take('{')) { SkipTo(';', '}'); return; }
            var depth = 1;
            while (_i < _s.Length && depth > 0)
            {
                var c = _s[_i];
                if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '/')
                {
                    while (_i < _s.Length && _s[_i] != '\n') _i++;
                    continue;
                }
                if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '*')
                {
                    var close = _s.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                    _i = close < 0 ? _s.Length : close + 2;
                    continue;
                }
                if (c == '{') depth++;
                else if (c == '}') depth--;
                _i++;
            }
        }

        /// <summary><paramref name="s"/> 在 <paramref name="index"/> 处以词 <paramref name="word"/> 开头
        /// 且其后不是 Ident 字符（防 '@statesX' 误配）。</summary>
        private static bool MatchesWord(string s, int index, string word)
        {
            if (index + word.Length > s.Length ||
                string.CompareOrdinal(s, index, word, 0, word.Length) != 0) return false;
            var after = index + word.Length;
            return after >= s.Length || !(char.IsLetterOrDigit(s[after]) || s[after] is '-' or '_');
        }

        /// <summary>统一状态解析（E5）：平台位 → 本文件 @states 声明 → 全局已声明目录（跨文件注册名）。
        /// 全未命中报 EUI050 并返回 (None, null)——调用方以 default 判别未命中。</summary>
        private (EustyleVariant Variant, string? Registered) ResolveState(string state, int nameStart)
        {
            if (States.TryGetValue(state, out var variant))
                return (variant, null);
            if (DeclaredContains(state) || EnumerableContains(_externalStates, state))
                return (EustyleVariant.None, state);
            ErrorSpan(SpanAt(nameStart, Math.Max(1, state.Length)),
                $"unknown pseudo-class ':{state}'; platform states are hover/active/pressed/focus/disabled — " +
                "declare custom states in a '@states {{ name; }}' block (EUI050)",
                UiDiagnostics.StyleSyntax);
            return (EustyleVariant.None, null);
        }

        private bool DeclaredContains(string name)
        {
            foreach (var decl in _declaredStates)
                if (decl.Name == name) return true;
            return false;
        }

        /// <summary>netstandard2.0 兼容的线性查找（该文件未启用 Linq）。</summary>
        private static bool EnumerableContains(IReadOnlyCollection<string> source, string value)
        {
            foreach (var item in source)
                if (item == value) return true;
            return false;
        }

        /// <summary>CollectStates 公开入口的快照（预扫已在构造时完成）。</summary>
        internal IReadOnlyList<EustyleStateDecl> SnapshotDeclaredStates() => _declaredStates.ToArray();

        /// <summary>
        /// Style= 声明序列：循环解析声明直到 EOF。类规则出现报 EUI050 并整块跳过
        /// （大括号配对，避免内部残渣二次报错）；$变量报 EUI050 跳到 ';'
        /// （块级概念不属于元素属性）；空序列合法（Class= 空串同款 no-op 语义）。
        /// </summary>
        public EustyleDeclarations DeclarationList()
        {
            var setters = new List<EustyleSetter>();
            var seenVariants = new List<EustyleVariant>();
            var baseVariant = new List<(EustyleVariant Variant, string? Registered)>();
            while (true)
            {
                SkipTrivia();
                if (Eof) break;
                var c = _s[_i];
                if (c == '$')
                {
                    Error(_i, _i + 1,
                        "'$variable' declarations are not allowed in Style=; variables belong to a stylesheet or @style block");
                    SkipTo(';');
                    continue;
                }
                if (c == '.')
                {
                    Error(_i, _i + 1,
                        "class rules are not allowed in Style=; declare classes in a stylesheet or @style block and reference them with Class=");
                    SkipBracedBlock();
                    continue;
                }
                if (c == '@')
                {
                    // 元素属性没有「状态」语境（design/17 §4.8②）：@states 与一切 @ 指令不适用
                    Error(_i, _i + 1,
                        "'@states' (and any '@' directive) is not allowed in Style=; custom states belong to a stylesheet or @style block");
                    SkipBracedBlock();
                    continue;
                }
                var before = _i;
                ParseDeclaration(setters, seenVariants, baseVariant);
                if (_i == before) break; // 防御：解析器未推进（理论不可达）时终止避免死循环
            }
            return new EustyleDeclarations(setters);
        }

        /// <summary>跳过一个完整大括号块（含嵌套与注释）：从当前位置找到 '{' 并消费到配对 '}'。
        /// 无 '{' 时跳到 EOF。DeclarationList 的类规则错误恢复用。</summary>
        private void SkipBracedBlock()
        {
            while (_i < _s.Length && _s[_i] != '{') _i++;
            if (_i >= _s.Length) return;
            var depth = 0;
            while (_i < _s.Length)
            {
                var c = _s[_i];
                if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '/')
                {
                    while (_i < _s.Length && _s[_i] != '\n') _i++;
                    continue;
                }
                if (c == '{') depth++;
                else if (c == '}' && --depth == 0) { _i++; return; }
                _i++;
            }
        }

        private void ParseVarDecl()
        {
            var start = _i;
            _i++; // '$'
            var name = Ident();
            if (name.Length == 0)
            {
                Error(start, _i + 1, "'$' must be followed by a variable name: $name: <value>;");
                SkipTo(';', '}');
                return;
            }

            SkipTrivia();
            if (!Take(':'))
            {
                Error(_i, _i + 1, $"expected ':' after variable '${name}'");
                SkipTo(';', '}');
                return;
            }

            var value = ParseValue();
            if (value is EustyleKeyword or EustyleString)
            {
                // design/19 §5.1：$变量仍限于数字/颜色——keyword/string 是字面量形态，
                // 不做变量插值也不进变量表（P1 保持简单）。
                ErrorSpan(value.Span,
                    "'$variable' values are limited to numbers and colors; " +
                    "enum keywords and string literals must be written inline (EUI050)",
                    UiDiagnostics.StyleSyntax);
                value = null;
            }
            SkipTrivia();
            if (Take(';'))
            {
                if (value is not null) _vars[name] = value;
                return;
            }
            Error(_i, _i + 1, $"expected ';' after variable '${name}' declaration");
            SkipTo(';', '}'); // 恢复（已消费停止字符）
            if (value is not null) _vars[name] = value;
        }

        private EustyleRule? ParseRule()
        {
            var start = _i;
            _i++; // '.'
            var name = Ident();
            if (name.Length == 0)
            {
                Error(start, _i + 1, "'.' must be followed by a class name");
                SkipTo(';', '}');
                return null;
            }
            var nameSpan = SpanAt(start, _i - start);

            // 选择器形态变体：.card:hover / .card:hover:focus（每个状态各得一份声明，
            // E5 起含注册名——混合书写时各得一份独立变体，与「声明序表达组合态」一致）。
            var selectorStates = new List<(EustyleVariant Variant, string? Registered)>();
            SkipTrivia();
            while (Peek == ':')
            {
                _i++;
                var nameStart = _i;
                var state = Ident();
                var resolved = ResolveState(state, nameStart);
                if (resolved != default && !selectorStates.Contains(resolved))
                    selectorStates.Add(resolved);
                SkipTrivia();
            }

            if (!Take('{'))
            {
                Error(_i, _i + 1, $"expected '{{' to open rule '.{name}'");
                SkipTo(';', '}');
                return null;
            }

            var setters = new List<EustyleSetter>();
            var seenVariants = new List<EustyleVariant>();
            var nested = new List<EustyleNested>();
            ParseBody(setters, seenVariants, selectorStates, name, allowNestedVariants: true, nested);
            return new EustyleRule(name, nameSpan, setters, nested);
        }

        /// <summary>
        /// 规则体：属性声明 + <c>&amp;:state</c> 变体块 + 嵌套结构选择器（E4，design/17 §4.5），
        /// 直到 '}' 或 EOF。<paramref name="current"/> 是本层声明归属的变体集合
        /// （选择器形态带入；空 = 基础序列）；<paramref name="nestedOut"/> 是本层嵌套规则容器
        /// （null = 变体块内——嵌套选择器不允许出现在变体块里，语义上「状态 × 结构」无对应物）。
        /// </summary>
        private void ParseBody(List<EustyleSetter> setters, List<EustyleVariant> seenVariants,
            List<(EustyleVariant Variant, string? Registered)> current, string className, bool allowNestedVariants,
            List<EustyleNested>? nestedOut)
        {
            while (true)
            {
                SkipTrivia();
                if (Eof)
                {
                    Error(_i, _i + 1, $"unterminated rule '.{className}'; add the closing '}}'");
                    return;
                }
                var c = _s[_i];
                if (c == '}') { _i++; return; }
                if (c == '&')
                {
                    if (!allowNestedVariants)
                    {
                        Error(_i, _i + 1,
                            "nested variant blocks are not supported in E1; declare variants on the class rule");
                        SkipTo(';', '}');
                        continue;
                    }
                    ParseVariantBlock(setters, seenVariants, className);
                    continue;
                }
                // E4 嵌套结构选择器：'>Button' / 'TextBlock.title' / '.title'。
                // 属性规范名改用 CLR 原名（PascalCase）后，大写开头不再是类型选择器的充分条件：
                // 前瞻到第一个 '{' 或 ';'/'}' ——嵌套选择器一定开块，声明一定以分号（或块尾）收尾。
                if (c == '>' || c == '.' || (char.IsUpper(c) && LooksLikeNestedSelector()))
                {
                    if (nestedOut is null)
                    {
                        Error(_i, _i + 1,
                            "nested selectors are not allowed inside a variant block; declare them on the class rule body");
                        SkipBracedBlock();
                        continue;
                    }
                    nestedOut.Add(ParseNested());
                    continue;
                }
                ParseDeclaration(setters, seenVariants, current);
            }
        }

        /// <summary>
        /// E4 嵌套结构选择器（design/17 §4.5）：
        /// <code>
        /// &gt; Button { … }              // 直接子类型 → 派生类 {parent}__button
        /// TextBlock.title { … }        // 后代 + 类约束 → {parent}__text-block-title
        /// .title { … }                 // 后代类约束 → {parent}__title
        /// &gt; Button:hover { … }        // 直接子类型 + 伪类 → 派生类的变体
        /// </code>
        /// 命名与查重由 <c>EustyleCollapse</c> 统一完成（解析器只产结构）。体内允许声明、
        /// <c>&amp;:state</c> 变体块（派生类变体）与再嵌套（派生名链式累加）。
        /// </summary>
        private EustyleNested ParseNested()
        {
            var start = _i;
            var direct = Take('>');
            SkipTrivia();

            string? typeName = null;
            string? constraint = null;
            if (Peek == '.')
            {
                _i++; // '.'
                constraint = Ident();
            }
            else
            {
                typeName = Ident();
                SkipTrivia();
                if (Peek == '.')
                {
                    _i++; // '.'
                    constraint = Ident();
                }
            }

            if ((typeName is null || typeName.Length == 0) && (constraint is null || constraint.Length == 0))
            {
                Error(start, _i,
                    "expected a nested selector: '> TypeName', 'TypeName.class' or '.class'");
                SkipTo(';', '}');
                return new EustyleNested(direct, null, null, SpanAt(start, Math.Max(1, _i - start)),
                    Array.Empty<EustyleSetter>(), Array.Empty<EustyleNested>());
            }

            // 嵌套选择器可带 :state —— 声明归属派生类的对应变体（与根规则同款状态链，
            // E5 起含注册名）。
            var states = new List<(EustyleVariant Variant, string? Registered)>();
            SkipTrivia();
            while (Peek == ':')
            {
                _i++;
                var nameStart = _i;
                var state = Ident();
                var resolved = ResolveState(state, nameStart);
                if (resolved != default && !states.Contains(resolved))
                    states.Add(resolved);
                SkipTrivia();
            }

            var selectorSpan = SpanAt(start, Math.Max(1, _i - start));

            if (!Take('{'))
            {
                Error(_i, _i + 1,
                    $"expected '{{' to open the nested rule block '{SelectorLabel(typeName, constraint)}'");
                SkipTo(';', '}');
                return new EustyleNested(direct, typeName, constraint, selectorSpan,
                    Array.Empty<EustyleSetter>(), Array.Empty<EustyleNested>());
            }

            var setters = new List<EustyleSetter>();
            var seenVariants = new List<EustyleVariant>();
            var nested = new List<EustyleNested>();
            ParseBody(setters, seenVariants, states, SelectorLabel(typeName, constraint),
                allowNestedVariants: true, nested);
            return new EustyleNested(direct, typeName, constraint, selectorSpan, setters, nested);
        }

        /// <summary>
        /// 区分「大写开头的属性规范名」与「大写开头的嵌套类型选择器」：从当前位置前瞻到
        /// 第一个 '{' 或 ';'/'}'——选择器一定开块，声明一定以分号（或所在块尾）收尾。
        /// ':' 之后选择器跟的是状态名（后续还有 ':' 或 '{'），属性跟的是值（#hex / 数字 / theme(...)），
        /// 两者同样由这一条前瞻规则分开，不需要目录参与。
        /// </summary>
        private bool LooksLikeNestedSelector()
        {
            var i = _i;
            while (i < _s.Length && (char.IsLetterOrDigit(_s[i]) || _s[i] == '-' || _s[i] == '_')) i++;
            while (i < _s.Length)
            {
                while (i < _s.Length && char.IsWhiteSpace(_s[i])) i++;
                if (i >= _s.Length) return false;
                var c = _s[i];
                if (c is '{' or '>' or '.') return true;
                if (c is ';' or '}') return false;
                if (c == ':')
                {
                    i++;
                    while (i < _s.Length && (char.IsLetterOrDigit(_s[i]) || _s[i] == '-' || _s[i] == '_')) i++;
                    continue;
                }
                return false; // 值或注释的起始字符
            }
            return false;
        }

        /// <summary>错误消息里的选择器显示名（类型/约束组合）。</summary>
        private static string SelectorLabel(string? typeName, string? constraint) =>
            typeName is null ? "." + constraint
                : constraint is null ? typeName
                : typeName + "." + constraint;

        /// <summary>&amp;:state { ... } 变体块：块内声明归属该状态（E1 不支持再嵌套）。</summary>
        private void ParseVariantBlock(List<EustyleSetter> setters, List<EustyleVariant> seenVariants, string className)
        {
            var start = _i;
            _i++; // '&'
            SkipTrivia();
            if (!Take(':'))
            {
                Error(_i, _i + 1, "expected ':' after '&' — only '&:hover'-style variant blocks are supported (E1)");
                SkipTo(';', '}');
                return;
            }
            var nameStart = _i;
            var state = Ident();
            var resolved = ResolveState(state, nameStart);
            if (resolved == default) return; // 未命中：ResolveState 已报 EUI050
            SkipTrivia();
            if (!Take('{'))
            {
                Error(_i, _i + 1, $"expected '{{' to open the '&:{state}' variant block");
                SkipTo(';', '}');
                return;
            }

            if (resolved.Registered is null && !seenVariants.Contains(resolved.Variant))
                seenVariants.Add(resolved.Variant);
            var current = new List<(EustyleVariant Variant, string? Registered)> { resolved };
            ParseBody(setters, seenVariants, current, className, allowNestedVariants: false, nestedOut: null);
        }

        private void ParseDeclaration(List<EustyleSetter> setters, List<EustyleVariant> seenVariants,
            List<(EustyleVariant Variant, string? Registered)> current)
        {
            var start = _i;
            var prop = Ident();
            if (prop.Length == 0)
            {
                Error(start, _i + 1, "expected a property name");
                SkipTo(';', '}');
                return;
            }
            var propSpan = SpanAt(start, prop.Length);

            SkipTrivia();
            if (!Take(':'))
            {
                Error(_i, _i + 1, $"expected ':' after property '{prop}'");
                SkipTo(';', '}');
                return;
            }

            var value = ParseValue();
            SkipTrivia();
            if (Peek == '}' || Eof)
            {
                // 允许最后一个声明省略分号（CSS 惯例）——块尾 '}' 或 Style= 值的 EOF 都容忍
            }
            else if (Take(';'))
            {
            }
            else
            {
                Error(_i, _i + 1, $"expected ';' after '{prop}' declaration");
                SkipTo(';', '}');
                return;
            }
            if (value is null) return;

            // 属性目录是编译期从 StyleBuilder setter + StyledProperty 字段派生的唯一事实源：
            // 名字（大小写敏感的 CLR 原名）、值形态、theme() 准入与目标字段都从这里取。
            var definition = _properties.Find(prop);
            if (definition is null)
            {
                ErrorSpan(propSpan,
                    $"unknown style property '{prop}'; use the case-sensitive CLR name; supported: " +
                    string.Join(", ", _properties.SortedNames()) + " (EUI051)",
                    UiDiagnostics.StyleUnknownProperty);
                return;
            }

            // theme 准入先于值形态检查（design/19 §5.2「theme() 准入另行检查」）：
            // theme() 落在 number/enum/string 属性上时，读者需要的是「这个属性不收 theme()」
            // 而不是「这个属性期望 number」——形态检查会把 theme 误报成类别不符。
            if (value is EustyleTheme && !definition.AllowsTheme)
            {
                ErrorSpan(value.Span,
                    $"property '{prop}' does not accept theme(dark, light); " +
                    "only properties with a ThemeColor resource overload do (EUI052)",
                    UiDiagnostics.StyleValueType);
                return;
            }

            // 值形态校验（design/19 §5.2 分派表）：number 属性只收裸数字；color 属性收
            // 颜色/theme 双色（theme 准入已在上面检查）；enum 属性收 keyword（编译期成员表校验）；
            // string 属性收字符串字面量或 keyword 直通（EUI052）。
            if (!ValueMatches(value, definition))
            {
                if (value is EustyleKeyword keyword && definition.Kind == StyleValueKind.Enum &&
                    EnumMembersOf(definition, out var members))
                {
                    ErrorSpan(value.Span,
                        $"'{keyword.Name}' is not a member of {definition.EnumTypeName}; " +
                        "supported: " + string.Join(", ", members) + " (EUI052)",
                        UiDiagnostics.StyleValueType);
                }
                else
                {
                    ErrorSpan(value.Span,
                        $"property '{prop}' expects a {KindName(definition.Kind)} value; " +
                        DescribeValue(value) + " is not one (EUI052)",
                        UiDiagnostics.StyleValueType);
                }
                return;
            }

            // 规范名（CLR 原名）进 AST：类通道 s.Name(...) 与 local 通道查目录都用它。
            if (current.Count == 0)
            {
                setters.Add(new EustyleSetter(EustyleVariant.None, definition.Name, value, propSpan, value.Span));
            }
            else
            {
                foreach (var (variant, registered) in current)
                {
                    if (registered is null && variant != EustyleVariant.None && !seenVariants.Contains(variant))
                        seenVariants.Add(variant);
                    setters.Add(new EustyleSetter(variant, definition.Name, value, propSpan, value.Span, registered));
                }
            }
        }

        /// <summary>值形态分派（design/19 §5.2）：枚举成员是否命中 <see cref="StylePropertyDef.Candidates"/>。
        /// 目录里 enum 属性必有成员表（扫描期从枚举符号派生）；缺失视为目录数据不合规，一律拒绝。</summary>
        private static bool EnumMembersOf(StylePropertyDef definition, out IReadOnlyList<string> members)
        {
            var candidates = definition.Candidates;
            if (candidates is { Count: > 0 })
            {
                members = candidates;
                return true;
            }
            members = Array.Empty<string>();
            return false;
        }

        /// <summary>值形态分派（design/19 §5.2）：
        /// number → 裸数字；color → hex/theme；enum → 命中成员表的 keyword；
        /// string → 字符串字面量或任意 keyword（CSS keyword 风格直通）。</summary>
        private static bool ValueMatches(EustyleValue value, StylePropertyDef definition) => value switch
        {
            EustyleNumber => definition.Kind == StyleValueKind.Number,
            EustyleColor => definition.Kind == StyleValueKind.Color,
            EustyleTheme => definition.Kind == StyleValueKind.Color,   // theme 准入另行检查
            EustyleKeyword keyword => definition.Kind switch
            {
                StyleValueKind.Enum => EnumMembersOf(definition, out var members)
                    && IndexOf(members, keyword.Name) >= 0,
                StyleValueKind.Text => true,
                _ => false,
            },
            EustyleString => definition.Kind == StyleValueKind.Text,
            _ => false,
        };

        /// <summary>netstandard2.0 兼容的成员表线性查找（成员表不建字典）。</summary>
        private static int IndexOf(IReadOnlyList<string> source, string value)
        {
            for (var i = 0; i < source.Count; i++)
                if (string.Equals(source[i], value, StringComparison.Ordinal)) return i;
            return -1;
        }

        private static string KindName(StyleValueKind kind) => kind switch
        {
            StyleValueKind.Number => "number",
            StyleValueKind.Color => "color",
            StyleValueKind.Enum => "keyword (an enum member)",
            _ => "string",
        };

        private static string DescribeValue(EustyleValue value) => value switch
        {
            EustyleNumber n => $"the number {n.Value.ToString("0.################", CultureInfo.InvariantCulture)}",
            EustyleColor => "a color literal",
            EustyleTheme => "a theme() pair",
            EustyleKeyword k => $"the keyword '{k.Name}'",
            EustyleString => "a string literal",
            _ => "the value",
        };

        // ---------- 值 ----------

        private EustyleValue? ParseValue()
        {
            SkipTrivia();
            var start = _i;
            if (Eof)
            {
                Error(start, start + 1,
                    "expected a value: theme(dark, light), a color (#hex), a number, an enum keyword or a string");
                return null;
            }
            var c = _s[_i];
            if (c == '#') return ParseColor();
            if (c == '$') return ParseVarRef();
            if (c == '"') return ParseString();
            if (c == '-' || char.IsDigit(c)) return ParseNumber();
            if (char.IsLetter(c))
            {
                var word = Ident();
                if (word == "theme") return ParseTheme(start);
                // design/19 §5.1：theme 之外的裸标识符 = keyword 值（枚举成员 / string 直通）——
                // 是否合法由声明处的属性目录校验（ParseDeclaration 的值形态分派）。
                return new EustyleKeyword(word, SpanAt(start, _i - start));
            }
            Error(start, start + 1,
                "unexpected character; expected theme(dark, light), a color (#hex), a number, an enum keyword or a string");
            return null;
        }

        /// <summary>带引号字符串值（design/19 §5.1）：直到未转义的闭合引号；支持
        /// <c>\\</c> / <c>\"</c> 转义，其余字符原样保留。未闭合 → EUI050。</summary>
        private EustyleValue? ParseString()
        {
            var start = _i;
            _i++; // 开引号
            var sb = new System.Text.StringBuilder();
            while (true)
            {
                if (Eof)
                {
                    Error(start, _i, "unterminated string literal; add the closing '\"'");
                    return null;
                }
                var c = _s[_i];
                if (c == '\\')
                {
                    if (_i + 1 >= _s.Length)
                    {
                        _i++;
                        Error(start, _i, "unterminated string literal; add the closing '\"'");
                        return null;
                    }
                    var next = _s[_i + 1];
                    if (next is '"' or '\\') { sb.Append(next); _i += 2; continue; }
                    sb.Append(c); // 未知转义原样保留反斜杠（不做 \n 等扩展）
                    _i++;
                    continue;
                }
                if (c == '"') { _i++; break; }
                sb.Append(c);
                _i++;
            }
            return new EustyleString(sb.ToString(), SpanAt(start, _i - start));
        }

        private EustyleValue? ParseTheme(int start)
        {
            SkipTrivia();
            if (!Take('('))
            {
                Error(_i, _i + 1, "expected '(' after theme: theme(dark, light)");
                return null;
            }
            var dark = ParseColorArg();
            SkipTrivia();
            if (!Take(','))
            {
                Error(_i, _i + 1, "expected ',' between theme colors: theme(dark, light)");
                return null;
            }
            var light = ParseColorArg();
            SkipTrivia();
            if (!Take(')'))
            {
                Error(_i, _i + 1, "expected ')' to close theme(dark, light)");
                return null;
            }
            if (dark is null || light is null) return null;
            return new EustyleTheme(dark, light, SpanAt(start, _i - start));
        }

        private EustyleColor? ParseColorArg()
        {
            SkipTrivia();
            if (Peek == '#') return ParseColor();
            Error(_i, _i + 1, "theme() arguments must be color literals: theme(#22c55e, #16a34a)");
            SkipTo(',', ')');
            return null;
        }

        private EustyleColor? ParseColor()
        {
            var start = _i;
            _i++; // '#'
            var hexStart = _i;
            while (_i < _s.Length && IsHex(_s[_i])) _i++;
            var hex = _s.Substring(hexStart, _i - hexStart);

            byte r, g, b, a = 255;
            if (hex.Length == 3)
            {
                r = (byte)(HexVal(hex[0]) * 17);
                g = (byte)(HexVal(hex[1]) * 17);
                b = (byte)(HexVal(hex[2]) * 17);
            }
            else if (hex.Length == 6 || hex.Length == 8)
            {
                r = (byte)(HexVal(hex[0]) * 16 + HexVal(hex[1]));
                g = (byte)(HexVal(hex[2]) * 16 + HexVal(hex[3]));
                b = (byte)(HexVal(hex[4]) * 16 + HexVal(hex[5]));
                if (hex.Length == 8) a = (byte)(HexVal(hex[6]) * 16 + HexVal(hex[7]));
            }
            else
            {
                // 连带消费后续字母数字（如 '#12g' 整体作废），恢复点落在 ';'——单条诊断
                while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '#')) _i++;
                Error(start, _i,
                    $"invalid color literal '#{hex}'; use #rgb, #rrggbb or #rrggbbaa (EUI052)",
                    UiDiagnostics.StyleValueType);
                return null;
            }
            return new EustyleColor(r, g, b, a, SpanAt(start, _i - start));
        }

        private EustyleValue? ParseVarRef()
        {
            var start = _i;
            _i++; // '$'
            var name = Ident();
            if (name.Length == 0)
            {
                Error(start, _i + 1, "'$' must be followed by a variable name");
                return null;
            }
            if (!_vars.TryGetValue(name, out var value))
            {
                Error(start, _i,
                    $"undefined variable '${name}'; declare it before use: ${name}: <value>; (EUI052)",
                    UiDiagnostics.StyleValueType);
                return null;
            }
            return value;
        }

        private EustyleValue? ParseNumber()
        {
            var start = _i;
            if (Peek == '-') _i++;
            var digitsStart = _i;
            while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            if (_i < _s.Length && _s[_i] == '.')
            {
                _i++;
                while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            }

            var text = _s.Substring(start, _i - start);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                Error(start, _i, "expected a number (logical unit, e.g. 16 or 1.5)");
                return null;
            }

            // 单位后缀（px/em/…）显式拒绝（design/17 §4.4：逻辑单位是唯一单位）。
            if (_i < _s.Length && (char.IsLetter(_s[_i]) || _s[_i] == '%'))
            {
                _i++;
                while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '%')) _i++;
                Error(start, _i,
                    "unit suffixes are not supported; the logical unit is the only unit — write '16', not '16px' (EUI052)",
                    UiDiagnostics.StyleValueType);
                return null;
            }
            return new EustyleNumber(number, SpanAt(start, _i - start));
        }

        // ---------- 扫描辅助 ----------

        private bool Eof => _i >= _s.Length;

        private char Peek => Eof ? '\0' : _s[_i];

        private bool Take(char c)
        {
            if (Peek != c) return false;
            _i++;
            return true;
        }

        private string Ident()
        {
            var start = _i;
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '-' || _s[_i] == '_')) _i++;
            return _s.Substring(start, _i - start);
        }

        private void SkipTrivia()
        {
            while (_i < _s.Length)
            {
                var c = _s[_i];
                if (char.IsWhiteSpace(c)) { _i++; continue; }
                if (c == '/' && _i + 1 < _s.Length)
                {
                    if (_s[_i + 1] == '/')
                    {
                        while (_i < _s.Length && _s[_i] != '\n') _i++;
                        continue;
                    }
                    if (_s[_i + 1] == '*')
                    {
                        var close = _s.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                        _i = close < 0 ? _s.Length : close + 2;
                        continue;
                    }
                }
                break;
            }
        }

        /// <summary>
        /// 错误恢复：推进到任一停止字符并**消费它**——不消费会让恢复点停在原地，
        /// 下一轮重复报同一条错误（死循环）。
        /// </summary>
        private void SkipTo(params char[] stops)
        {
            while (_i < _s.Length && Array.IndexOf(stops, _s[_i]) < 0) _i++;
            if (_i < _s.Length) _i++;
        }

        private TextSpan SpanAt(int localStart, int localLength) =>
            new(_offset + localStart, Math.Max(1, localLength));

        private void Error(int localStart, int localEnd, string message, string code = UiDiagnostics.StyleSyntax) =>
            ErrorSpan(SpanAt(localStart, Math.Max(1, localEnd - localStart)), message, code);

        private void ErrorSpan(TextSpan span, string message, string code) =>
            _diags.Add(UiDiagnostics.Error(code, message, span));

        private static bool IsHex(char c) =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

        private static int HexVal(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

        private static string Printable(char c) => char.IsControl(c) ? " " : c.ToString();
    }
}
