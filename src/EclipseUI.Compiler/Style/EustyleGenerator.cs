using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace EclipseUI.Compiler;

/// <summary>
/// Eustyle → C# 生成器（E1，design/17 §4.6）：解析产物 → <c>registry.Define(...)</c> 调用序列。
/// 类型一律 global:: 全限定（与模板生成器同口径，生成代码不依赖 using）；块 lambda 按声明序
/// 输出变体链——同时匹配多个伪类时后声明胜（D102 运行时语义）。
/// </summary>
public static class EustyleGenerator
{
    /// <summary>
    /// 发射 Define 序列（每条规则一段，直接拼入外层方法体）。
    /// <paramref name="registryExpression"/> 是目标注册表表达式——.eustyle 产物传形参名
    /// （如 <c>registry</c>），@style 组件块传 <c>global::EclipseUI.Rendering.StyleRegistry.Instance</c>。
    /// </summary>
    public static string EmitDefines(EustyleStylesheet sheet, string registryExpression, string indent = "        ")
    {
        var sb = new StringBuilder();
        foreach (var rule in sheet.Rules)
            EmitRule(sb, rule, registryExpression, indent);
        return sb.ToString();
    }

    private static void EmitRule(StringBuilder sb, EustyleRule rule, string registry, string indent)
    {
        // 变体按首次出现顺序分组（None 组最前，进 Define 体）；组内保持属性声明序。
        var groups = new List<KeyValuePair<EustyleVariant, List<EustyleSetter>>>();
        foreach (var setter in rule.Setters)
        {
            var index = IndexOfVariant(groups, setter.Variant);
            if (index < 0)
            {
                var group = new List<EustyleSetter> { setter };
                groups.Add(new KeyValuePair<EustyleVariant, List<EustyleSetter>>(setter.Variant, group));
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
        var chain = new List<KeyValuePair<EustyleVariant, List<EustyleSetter>>>();
        foreach (var group in groups)
            if (group.Key != EustyleVariant.None) chain.Add(group);

        sb.Append(indent).Append(registry).Append(".Define(\"").Append(Literal(rule.ClassName))
            .Append("\", s =>").AppendLine();
        EmitSetterGroup(sb, indent + "    ", SettersOf(groups, EustyleVariant.None));
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
                sb.Append(indent).Append("    .").Append(VariantMethod(chain[i].Key)).Append("(s =>").AppendLine();
                EmitSetterGroup(sb, indent + "    ", chain[i].Value, last ? ");" : ")");
            }
        }
    }

    private static int IndexOfVariant(List<KeyValuePair<EustyleVariant, List<EustyleSetter>>> groups, EustyleVariant variant)
    {
        for (var i = 0; i < groups.Count; i++)
            if (groups[i].Key == variant) return i;
        return -1;
    }

    private static List<EustyleSetter> SettersOf(
        List<KeyValuePair<EustyleVariant, List<EustyleSetter>>> groups, EustyleVariant variant)
    {
        foreach (var group in groups)
            if (group.Key == variant) return group.Value;
        return new List<EustyleSetter>();
    }

    /// <summary>发射一组 setter（块 lambda 体）。closingSuffix 拼在闭合 '}' 同行——
    /// 变体链用它带上 ')'/');'，避免多一行孤儿括号。</summary>
    private static void EmitSetterGroup(StringBuilder sb, string indent, List<EustyleSetter> setters,
        string closingSuffix = "")
    {
        sb.Append(indent).AppendLine("{");
        foreach (var setter in setters)
            sb.Append(indent).Append("    s.").Append(setter.Property).Append('(')
                .Append(ValueLiteral(setter.Value)).AppendLine(");");
        sb.Append(indent).Append('}').Append(closingSuffix).AppendLine();
    }

    private static string VariantMethod(EustyleVariant variant) => variant switch
    {
        EustyleVariant.Hover => "Hover",
        EustyleVariant.Pressed => "Pressed",
        EustyleVariant.Focus => "Focus",
        EustyleVariant.Disabled => "Disabled",
        _ => throw new InvalidOperationException("unexpected base variant in chain emission"),
    };

    private static string ValueLiteral(EustyleValue value) => value switch
    {
        EustyleColor color => ColorLiteral(color.R, color.G, color.B, color.A),
        EustyleTheme theme => "new global::EclipseUI.Rendering.ThemeColor("
            + ColorLiteral(theme.Dark.R, theme.Dark.G, theme.Dark.B, theme.Dark.A) + ", "
            + ColorLiteral(theme.Light.R, theme.Light.G, theme.Light.B, theme.Light.A) + ")",
        EustyleNumber number => number.Value.ToString("0.################", CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException("unknown value kind"),
    };

    private static string ColorLiteral(byte r, byte g, byte b, byte a) =>
        a == 255
            ? "new global::EclipseUI.Rendering.Color(" + r + ", " + g + ", " + b + ")"
            : "new global::EclipseUI.Rendering.Color(" + r + ", " + g + ", " + b + ", " + a + ")";

    private static string Literal(string text) =>
        text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
