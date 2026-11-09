using System;
using System.Collections.Generic;
using System.Globalization;

namespace EclipseUI.Compiler;

/// <summary>
/// Eustyle 解析器（E1，design/17 §4）：SCSS 子集 → 规则列表。语法：
/// <code>
/// $var: value;                        // 编译期变量（解析时替换，先声明先用）
/// .name[:state][:state] { prop: value;  &amp;:state { prop: value; ... } }
/// .panel { &gt; Button { … }  TextBlock.title { … }  &gt; Button:hover { … } }   // E4 嵌套
/// </code>
/// value := theme(color, color) | #rgb | #rrggbb | #rrggbbaa | number | $var
/// state := hover | active | pressed | focus | disabled（active 是 pressed 的 CSS 别名）
/// 注释 := // 行注释 | /* 块注释 */
///
/// 诊断：EUI050（语法 / 不支持形态）、EUI051（未知属性）、EUI052（值类型不符，含未定义变量）。
/// 嵌套结构选择器（E4，design/17 §4.5）只产结构（<see cref="EustyleNested"/>）——塌缩命名、
/// 冲突查重（EUI053）与注入规则由 <c>EustyleCollapse</c> 统一完成；变体块内不允许嵌套选择器。
/// </summary>
public static class Eustyle
{
    /// <summary>属性目录（E1）：属性名 → StyleBuilder setter 名 + 期望值形态（color/number）。
    /// 目录随 StyleBuilder 扩容同步扩容（design/17 §4.3：setter 集即单一来源）。</summary>
    private static readonly Dictionary<string, (string Setter, string Kind)> Properties =
        new(StringComparer.Ordinal)
        {
            ["background"] = ("Background", "color"),
            ["foreground"] = ("Foreground", "color"),
            ["padding"] = ("Padding", "number"),
            ["corner-radius"] = ("CornerRadius", "number"),
            ["font-size"] = ("FontSize", "number"),
            ["focus-ring-width"] = ("FocusRingWidth", "number"),
        };

    private static readonly Dictionary<string, EustyleVariant> States =
        new(StringComparer.Ordinal)
        {
            ["hover"] = EustyleVariant.Hover,
            ["pressed"] = EustyleVariant.Pressed,
            ["active"] = EustyleVariant.Pressed,   // CSS :active 别名
            ["focus"] = EustyleVariant.Focus,
            ["disabled"] = EustyleVariant.Disabled,
        };

    /// <summary>
    /// 解析样式表源码。<paramref name="spanOffset"/> 是源码在宿主文件内的起始偏移
    /// （@style 块传 <c>UiStyleBlock.CodeSpan.Start</c>，.eustyle 文件传 0）——
    /// 产出的诊断 span 一律是宿主文件的绝对 span。
    /// </summary>
    public static EustyleStylesheet Parse(string text, string filePath, List<UiDiagnostic> diagnostics, int spanOffset = 0)
        => new Impl(text, diagnostics, spanOffset).Document();

    /// <summary>
    /// 解析元素级 <c>Style="…"</c> 的声明序列（E2，design/17 §4.8②）：
    /// 只接受 <c>prop: value;</c> 声明——类规则、<c>$变量</c>（块级概念）与变体块不适用，
    /// 状态外观一律走类名通道。诊断码与样式表一致（EUI050/EUI051/EUI052），
    /// span 经 <paramref name="spanOffset"/> 映射回宿主 .ui 文件。
    /// </summary>
    public static EustyleDeclarations ParseDeclarations(string text, string filePath, List<UiDiagnostic> diagnostics, int spanOffset = 0)
        => new Impl(text, diagnostics, spanOffset).DeclarationList();

    private sealed class Impl
    {
        private readonly string _s;
        private readonly List<UiDiagnostic> _diags;
        private readonly int _offset;
        private readonly Dictionary<string, EustyleValue> _vars =
            new(StringComparer.Ordinal);
        private int _i;

        public Impl(string s, List<UiDiagnostic> diags, int offset)
        {
            _s = s;
            _diags = diags;
            _offset = offset;
        }

        // ---------- 顶层 ----------

        public EustyleStylesheet Document()
        {
            var rules = new List<EustyleRule>();
            while (true)
            {
                SkipTrivia();
                if (Eof) break;
                var c = _s[_i];
                if (c == '$') { ParseVarDecl(); continue; }
                if (c == '.')
                {
                    var rule = ParseRule();
                    if (rule is not null) rules.Add(rule);
                    continue;
                }
                Error(_i, _i + 1,
                    $"unexpected character '{Printable(c)}'; expected a class rule '.name {{ ... }}' or a '$variable' declaration");
                SkipTo(';', '}');
            }
            return new EustyleStylesheet(rules);
        }

        /// <summary>
        /// Style= 声明序列：循环解析声明直到 EOF。类规则出现报 EUI050 并整块跳过
        /// （大括号配对，避免内部残渣二次报错）；$变量报 EUI050 跳到 ';'
        /// （块级概念不属于元素属性）；空序列合法（Class= 空串同款 no-op 语义）。
        /// </summary>
        public EustyleDeclarations DeclarationList()
        {
            var setters = new List<EustyleSetter>();
            var seenVariants = new List<EustyleVariant>();
            var baseVariant = new List<EustyleVariant>();
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

            // 选择器形态变体：.card:hover / .card:hover:focus（每个状态各得一份声明）
            var selectorStates = new List<EustyleVariant>();
            SkipTrivia();
            while (Peek == ':')
            {
                _i++;
                var state = Ident();
                if (!States.TryGetValue(state, out var variant))
                    Error(_i - Math.Max(1, state.Length), _i,
                        $"unknown pseudo-class ':{state}'; supported: hover/active/pressed/focus/disabled");
                else if (!selectorStates.Contains(variant))
                    selectorStates.Add(variant);
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
            List<EustyleVariant> current, string className, bool allowNestedVariants,
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
                // E4 嵌套结构选择器：'>Button' / 'TextBlock.title' / '.title'（属性名一律小写，
                // PascalCase 开头即类型选择器；'.' 开头是类约束后代）。
                if (c == '>' || c == '.' || char.IsUpper(c))
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

            // 嵌套选择器可带 :state —— 声明归属派生类的对应变体（与根规则同款状态链）。
            var states = new List<EustyleVariant>();
            SkipTrivia();
            while (Peek == ':')
            {
                _i++;
                var state = Ident();
                if (!States.TryGetValue(state, out var variant))
                    Error(_i - Math.Max(1, state.Length), _i,
                        $"unknown pseudo-class ':{state}'; supported: hover/active/pressed/focus/disabled");
                else if (!states.Contains(variant))
                    states.Add(variant);
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
            var state = Ident();
            if (!States.TryGetValue(state, out var variant))
            {
                Error(_i - Math.Max(1, state.Length), _i,
                    $"unknown pseudo-class ':{state}'; supported: hover/active/pressed/focus/disabled");
                SkipTo(';', '}');
                return;
            }
            SkipTrivia();
            if (!Take('{'))
            {
                Error(_i, _i + 1, $"expected '{{' to open the '&:{state}' variant block");
                SkipTo(';', '}');
                return;
            }

            if (!seenVariants.Contains(variant)) seenVariants.Add(variant);
            var current = new List<EustyleVariant> { variant };
            ParseBody(setters, seenVariants, current, className, allowNestedVariants: false, nestedOut: null);
        }

        private void ParseDeclaration(List<EustyleSetter> setters, List<EustyleVariant> seenVariants,
            List<EustyleVariant> current)
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

            if (!Properties.TryGetValue(prop, out var binding))
            {
                ErrorSpan(propSpan,
                    $"unknown style property '{prop}'; supported: " +
                    string.Join(", ", SortedPropertyNames()) + " (EUI051)",
                    UiDiagnostics.StyleUnknownProperty);
                return;
            }

            // 值形态校验：color 属性接受颜色/theme 双色；number 属性只接受裸数字（EUI052）。
            var actualKind = value is EustyleNumber ? "number" : "color";
            if (actualKind != binding.Kind)
            {
                ErrorSpan(value.Span,
                    $"property '{prop}' expects a {binding.Kind} value; " +
                    DescribeValue(value) + " is not one (EUI052)",
                    UiDiagnostics.StyleValueType);
                return;
            }

            if (current.Count == 0)
            {
                setters.Add(new EustyleSetter(EustyleVariant.None, binding.Setter, value, propSpan, value.Span));
            }
            else
            {
                foreach (var variant in current)
                {
                    if (variant != EustyleVariant.None && !seenVariants.Contains(variant))
                        seenVariants.Add(variant);
                    setters.Add(new EustyleSetter(variant, binding.Setter, value, propSpan, value.Span));
                }
            }
        }

        private static string SortedPropertyNames() =>
            string.Join(", ", new[] { "background", "corner-radius", "focus-ring-width", "font-size", "foreground", "padding" });

        private static string DescribeValue(EustyleValue value) => value switch
        {
            EustyleNumber n => $"the number {n.Value.ToString("0.################", CultureInfo.InvariantCulture)}",
            EustyleColor => "a color literal",
            EustyleTheme => "a theme() pair",
            _ => "the value",
        };

        // ---------- 值 ----------

        private EustyleValue? ParseValue()
        {
            SkipTrivia();
            var start = _i;
            if (Eof)
            {
                Error(start, start + 1, "expected a value: theme(dark, light), a color (#hex) or a number");
                return null;
            }
            var c = _s[_i];
            if (c == '#') return ParseColor();
            if (c == '$') return ParseVarRef();
            if (c == '-' || char.IsDigit(c)) return ParseNumber();
            if (char.IsLetter(c))
            {
                var word = Ident();
                if (word == "theme") return ParseTheme(start);
                Error(start, _i, $"unexpected '{word}'; expected theme(dark, light), a color (#hex) or a number");
                return null;
            }
            Error(start, start + 1,
                $"unexpected character '{Printable(c)}'; expected theme(dark, light), a color (#hex) or a number");
            return null;
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
