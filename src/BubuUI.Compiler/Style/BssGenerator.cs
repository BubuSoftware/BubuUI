using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BubuUI.Compiler;

/// <summary>
/// Bss → C# 生成器（E1，design/17 §4.6）：解析产物 → <c>registry.Define(...)</c> 调用序列。
/// 类型一律 global:: 全限定（与模板生成器同口径，生成代码不依赖 using）；块 lambda 按声明序
/// 输出变体链——同时匹配多个伪类时后声明胜（D102 运行时语义）。
/// </summary>
public static class BssGenerator
{
    /// <summary>
    /// 发射 Define 序列（每条规则一段，直接拼入外层方法体）。
    /// <paramref name="registryExpression"/> 是目标注册表表达式——.bss 产物传形参名
    /// （如 <c>registry</c>），@style 组件块传 <c>global::BubuUI.Rendering.StyleRegistry.Instance</c>。
    /// <paramref name="properties"/> 是样式属性目录（design/19 §6.1）：内建属性发命名方法
    /// <c>s.Name(...)</c>，自定义属性发泛型逃生口 <c>s.Set(field, value)</c>。
    /// </summary>
    public static string EmitDefines(BssStylesheet sheet, string registryExpression,
        StylePropertyCatalog properties, string indent = "        ")
        => EmitDefines(sheet.Rules, registryExpression, properties, indent);

    /// <summary>塌缩产物入口（E4）：根类与派生类混排的规则列表（<see cref="BssCollapse"/> 产出）。</summary>
    public static string EmitDefines(IReadOnlyList<BssRule> rules, string registryExpression,
        StylePropertyCatalog properties, string indent = "        ")
    {
        var sb = new StringBuilder();
        foreach (var rule in rules)
            EmitRule(sb, rule, registryExpression, properties, indent);
        return sb.ToString();
    }

    /// <summary>
    /// 元素级 <c>Style="…"</c> 的 local 直赋语句（E2，design/17 §4.8②）：每条声明一行
    /// <c>target.SetValue(&lt;目录里的目标字段&gt;, &lt;值&gt;)</c> 写 local 层。
    /// 目标字段与 theme() 资源字段全部来自 <see cref="StylePropertyCatalog"/>——不再为每个属性
    /// 手写 switch 分支；加属性只需在运行时注册字段 + StyleBuilder setter，发射侧零改动。
    /// 返回 (语句, 值 span) 序列，调用方逐条做 #line 映射。
    /// </summary>
    public static IReadOnlyList<(string Statement, TextSpan Span)> EmitLocalSetters(
        IReadOnlyList<BssSetter> setters, string target, StylePropertyCatalog properties)
    {
        var statements = new List<(string, TextSpan)>(setters.Count);
        foreach (var setter in setters)
        {
            var definition = properties.Find(setter.Property)
                ?? throw new InvalidOperationException(
                    $"style property '{setter.Property}' is not in the derived catalog");
            var field = definition.FieldFor(setter.Value is BssTheme)
                ?? throw new InvalidOperationException(
                    $"style property '{setter.Property}' has no theme resource field");
            statements.Add(($"{target}.SetValue({field}, {ValueLiteral(setter.Value, definition)});",
                setter.ValueSpan));
        }
        return statements;
    }

    private static void EmitRule(StringBuilder sb, BssRule rule, string registry,
        StylePropertyCatalog properties, string indent)
    {
        // 变体按首次出现顺序分组（None 组最前，进 Define 体；组内保持属性声明序）。
        // 分组键 = (平台位, 注册名)——注册制伪类（E5）是独立通道，RegisteredState 非空的
        // 声明不与平台位混组；同一注册名的声明同组（见 <see cref="BssSetter.RegisteredState"/>）。
        var groups = new List<KeyValuePair<(BssVariant Variant, string? Registered), List<BssSetter>>>();
        foreach (var setter in rule.Setters)
        {
            var key = (setter.Variant, setter.RegisteredState);
            var index = IndexOfKey(groups, key);
            if (index < 0)
            {
                var group = new List<BssSetter> { setter };
                groups.Add(new KeyValuePair<(BssVariant, string?), List<BssSetter>>(key, group));
            }
            else
            {
                groups[index].Value.Add(setter);
            }
        }

        // 形态（块 lambda；链调用括号必须显式闭合——变体块的 '}' 只闭合 lambda，')' 闭合
        // .Variant(...) 调用本身，最后一个变体与 ';' 同行）：
        //   registry.Define("name", s =>
        //   {
        //       s.Background(...);
        //   })
        //       .Hover(s =>
        //       {
        //           s.Background(...);
        //       })
        //       .Pressed(s =>
        //       {
        //           s.Background(...);
        //       });
        var chain = new List<KeyValuePair<(BssVariant Variant, string? Registered), List<BssSetter>>>();
        foreach (var group in groups)
            if (group.Key != (BssVariant.None, null)) chain.Add(group);

        sb.Append(indent).Append(registry).Append(".Define(\"").Append(Literal(rule.ClassName))
            .Append("\", s =>").AppendLine();
        EmitSetterGroup(sb, indent + "    ", SettersOf(groups, (BssVariant.None, null)), properties);
        if (chain.Count == 0)
        {
            sb.Append(indent).AppendLine(");");
        }
        else
        {
            sb.Append(indent).AppendLine(")");
            for (var i = 0; i < chain.Count; i++)
            {
                var last = i == chain.Count - 1;
                var closing = last ? ");" : ")";
                if (chain[i].Key.Registered is { } registered)
                {
                    // 注册位（E5）：.State(global::…PseudoStatesRegistry.Register("name"), s => …)
                    sb.Append(indent).Append("    .State(global::BubuUI.PseudoStatesRegistry.Register(\"")
                        .Append(Literal(registered)).Append("\"), s =>").AppendLine();
                }
                else
                {
                    sb.Append(indent).Append("    .").Append(VariantMethod(chain[i].Key.Variant))
                        .Append("(s =>").AppendLine();
                }
                EmitSetterGroup(sb, indent + "    ", chain[i].Value, properties, closing);
            }
        }
    }

    private static int IndexOfKey(
        List<KeyValuePair<(BssVariant Variant, string? Registered), List<BssSetter>>> groups,
        (BssVariant Variant, string? Registered) key)
    {
        for (var i = 0; i < groups.Count; i++)
            if (groups[i].Key == key) return i;
        return -1;
    }

    private static List<BssSetter> SettersOf(
        List<KeyValuePair<(BssVariant Variant, string? Registered), List<BssSetter>>> groups,
        (BssVariant Variant, string? Registered) key)
    {
        foreach (var group in groups)
            if (group.Key == key) return group.Value;
        return new List<BssSetter>();
    }

    /// <summary>发射一组 setter（块 lambda 体）。closingSuffix 拼在闭合 '}' 同行——
    /// 变体链用它带上 ')'/');'，避免多一行孤儿括号。
    /// 属性分派（design/19 §6.1）：内建属性走 <c>s.Name(value)</c> 命名方法；自定义属性
    /// （[UiStyleProperty] 标注通道）StyleBuilder 上没有对应方法，走泛型逃生口
    /// <c>s.Set(global::Ns.Type.Field, value)</c>——类型安全由目录扫描期推导保证（R3）。</summary>
    private static void EmitSetterGroup(StringBuilder sb, string indent, List<BssSetter> setters,
        StylePropertyCatalog properties, string closingSuffix = "")
    {
        sb.Append(indent).AppendLine("{");
        foreach (var setter in setters)
        {
            var definition = properties.Find(setter.Property)
                ?? throw new InvalidOperationException(
                    $"style property '{setter.Property}' is not in the derived catalog");
            if (setter.Value is BssTransition transition)
            {
                sb.Append(indent).Append("    s.Transition(").Append(definition.PropertyField)
                    .Append(", new global::BubuUI.TransitionSpec(global::System.TimeSpan.FromMilliseconds(")
                    .Append(transition.Milliseconds.ToString("R", CultureInfo.InvariantCulture))
                    .Append("), global::BubuUI.Easings.").Append(transition.Easing).AppendLine("));");
                continue;
            }
            var target = definition.IsBuiltIn
                ? "s." + definition.Name + "("
                : "s.Set(" + definition.PropertyField + ", ";
            sb.Append(indent).Append("    ").Append(target)
                .Append(ValueLiteral(setter.Value, definition)).AppendLine(");");
        }
        sb.Append(indent).Append('}').Append(closingSuffix).AppendLine();
    }

    /// <summary>变体链调用形态：平台位 → 命名方法（.Hover/.Pressed/…）；注册位（E5）→
    /// <c>.State(PseudoStatesRegistry.Register("name"), …)</c>——掩码在运行时启动期分配，
    /// 生成代码内联幂等 Register（重复调用安全、与宿主 C# 的注册互不干扰）。</summary>
    private static string VariantMethod(BssVariant variant) => variant switch
    {
        BssVariant.Hover => "Hover",
        BssVariant.Pressed => "Pressed",
        BssVariant.Focus => "Focus",
        BssVariant.FocusVisible => "FocusVisible",
        BssVariant.FocusWithin => "FocusWithin",
        BssVariant.Disabled => "Disabled",
        _ => throw new InvalidOperationException("unexpected base variant in chain emission"),
    };

    /// <summary>
    /// 发射 <c>@states</c> 声明的启动期注册序列（E5）：每个声明一行幂等
    /// <c>PseudoStatesRegistry.Register</c>——在 Define 序列之前调用，保证变体链内联的
    /// Register 实参之外，未引用的声明位也完成注册（第三方可 GetMask 查询）。
    /// </summary>
    public static string EmitStateRegisters(IReadOnlyList<BssStateDecl> states, string indent = "        ")
    {
        var sb = new StringBuilder();
        foreach (var state in states)
        {
            sb.Append(indent).Append("global::BubuUI.PseudoStatesRegistry.Register(\"")
                .Append(Literal(state.Name)).AppendLine("\");");
        }
        return sb.ToString();
    }

    /// <summary>
    /// 值 → C# 字面量（design/19 §5.2 分派表的发射一半）。enum keyword 需要目录预计算的
    /// 枚举类型名（成员校验已在解析期完成，这里防御性再查一次）；string 走 C# 转义；
    /// 其余内建形态不依赖目录。
    /// </summary>
    private static string ValueLiteral(BssValue value, StylePropertyDef? definition = null) => value switch
    {
        BssColor color when definition?.IsUiColor == true =>
            "new global::BubuUI.UiColor(" + color.R + ", " + color.G + ", " + color.B + ", " + color.A + ")",
        BssColor color => ColorLiteral(color.R, color.G, color.B, color.A),
        BssTheme theme => ThemeLiteral(theme),
        BssNumber number => NumberLiteral(number),
        BssKeyword keyword => KeywordLiteral(keyword, definition),
        BssString text => StringLiteral(text.Value),
        _ => throw new InvalidOperationException("unknown value kind"),
    };

    /// <summary>枚举 keyword → <c>global::Ns.T.Member</c>；string 属性的 keyword 直通为
    /// 字符串字面量（design/19 §5.2：string 收 keyword 直通）。目录里 enum 属性必有成员表与
    /// 枚举类型名；未命中成员说明发射期与目录数据不一致（防御，不静默产出坏代码）。</summary>
    private static string KeywordLiteral(BssKeyword keyword, StylePropertyDef? definition)
    {
        if (definition is { Kind: StyleValueKind.Text })
            return StringLiteral(keyword.Name);
        if (definition is not { Kind: StyleValueKind.Enum, EnumTypeName: { } enumType })
            throw new InvalidOperationException(
                $"keyword value '{keyword.Name}' has no enum or string style property to target");
        var candidates = definition.Candidates ?? (IReadOnlyList<string>)Array.Empty<string>();
        foreach (var candidate in candidates)
            if (string.Equals(candidate, keyword.Name, StringComparison.Ordinal))
                return enumType + "." + keyword.Name;
        throw new InvalidOperationException(
            $"'{keyword.Name}' is not a member of {enumType}");
    }

    /// <summary>字符串值 → C# 字面量：反斜杠与引号转义，控制字符按 \\uXXXX。</summary>
    private static string StringLiteral(string text)
    {
        var sb = new StringBuilder(text.Length + 2).Append('"');
        foreach (var c in text)
        {
            if (c == '\\') sb.Append("\\\\");
            else if (c == '"') sb.Append("\\\"");
            else if (char.IsControl(c)) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    private static string ThemeLiteral(BssTheme theme) => "new global::BubuUI.Rendering.ThemeColor("
        + ColorLiteral(theme.Dark.R, theme.Dark.G, theme.Dark.B, theme.Dark.A) + ", "
        + ColorLiteral(theme.Light.R, theme.Light.G, theme.Light.B, theme.Light.A) + ")";

    private static string NumberLiteral(BssNumber? number) =>
        (number ?? throw new InvalidOperationException("expected a number value"))
            .Value.ToString("0.################", CultureInfo.InvariantCulture);

    private static string ColorLiteral(byte r, byte g, byte b, byte a) =>
        a == 255
            ? "new global::BubuUI.Rendering.Color(" + r + ", " + g + ", " + b + ")"
            : "new global::BubuUI.Rendering.Color(" + r + ", " + g + ", " + b + ", " + a + ")";

    private static string Literal(string text) =>
        text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
