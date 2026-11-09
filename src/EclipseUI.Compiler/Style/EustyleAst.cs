using System;
using System.Collections.Generic;

namespace EclipseUI.Compiler;

/// <summary>
/// Eustyle 样式表 AST（E1，design/17 §4）：编译期常量值 + 类规则，供生成器直接发射
/// <c>StyleRegistry.Define</c> 调用。运行时零新机制——产物与手写 C# Define 同构。
/// </summary>

/// <summary>样式表值基类：颜色 / theme 双色 / 裸数字（编译期常量，无运行时表达式）。</summary>
public abstract record EustyleValue(TextSpan Span);

/// <summary>hex 颜色字面量：#rgb / #rrggbb / #rrggbbaa（末对为 alpha，CSS 语义）。</summary>
public sealed record EustyleColor(byte R, byte G, byte B, byte A, TextSpan Span) : EustyleValue(Span);

/// <summary>
/// <c>theme(dark, light)</c> 双色 → <c>ThemeColor</c>（D100 资源）：深浅切换自动跟随。
/// </summary>
public sealed record EustyleTheme(EustyleColor Dark, EustyleColor Light, TextSpan Span) : EustyleValue(Span);

/// <summary>裸数字（逻辑单位；单位后缀不被支持，报 EUI052）。</summary>
public sealed record EustyleNumber(double Value, TextSpan Span) : EustyleValue(Span);

/// <summary>
/// 伪类变体（D102 PseudoStates 位掩码的编译期对应物）：
/// None = 基础序列；<c>:active</c> 是 <see cref="Pressed"/> 的 CSS 别名。
/// </summary>
public enum EustyleVariant
{
    None = 0,
    Hover = 1,
    Pressed = 2,
    Focus = 3,
    Disabled = 4,
}

/// <summary>一个样式类：类名 + 属性/变体 setter 序列（声明序，重复声明按后者胜）。</summary>
public sealed record EustyleRule(string ClassName, TextSpan NameSpan, IReadOnlyList<EustyleSetter> Setters);

/// <summary>一条属性声明：所属变体（None = 基础）+ StyleBuilder setter 名 + 值。</summary>
public sealed record EustyleSetter(
    EustyleVariant Variant, string Property, EustyleValue Value, TextSpan PropertySpan, TextSpan ValueSpan);

/// <summary>解析产物：全部类规则（按源顺序）。</summary>
public sealed record EustyleStylesheet(IReadOnlyList<EustyleRule> Rules);
