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
/// 裸标识符值（design/19 §5.1，keyword 风格）：枚举属性按成员表校验后发射
/// <c>global::Ns.T.Member</c>；string 属性直通为字符串字面量。值限于数字/颜色的
/// <c>$变量</c> 不对新形态插值（P1 保持简单）。
/// </summary>
public sealed record EustyleKeyword(string Name, TextSpan Span) : EustyleValue(Span);

/// <summary>带引号字符串值（design/19 §5.1）：string 属性直通；其余属性形态不符（EUI052）。</summary>
public sealed record EustyleString(string Value, TextSpan Span) : EustyleValue(Span);

/// <summary>
/// 伪类变体（D102 PseudoStates 位掩码的编译期对应物）：
/// None = 基础序列；<c>:active</c> 是 <see cref="Pressed"/> 的 CSS 别名。
/// 注册制伪类（E5）不走本枚举——<see cref="EustyleSetter.RegisteredState"/> 携带注册名，
/// 运行时掩码由 <c>PseudoStatesRegistry.Register</c> 启动期分配。
/// </summary>
public enum EustyleVariant
{
    None = 0,
    Hover = 1,
    Pressed = 2,
    Focus = 3,
    Disabled = 4,
}

/// <summary>
/// <c>@states {{ name; name; }}</c> 顶层声明块的一项（E5，design/17 §3.2/§7）：
/// 声明的名字在本样式表内可作为伪类选择器使用，产物是启动期
/// <c>PseudoStatesRegistry.Register</c> 调用（全局掩码命名空间——注册位是运行时全局的，
/// 不参与类名组件前缀机制，跨文件声明/引用同掩码幂等）。
/// </summary>
public sealed record EustyleStateDecl(string Name, TextSpan Span);

/// <summary>一个样式类：类名 + 属性/变体 setter 序列（声明序，重复声明按后者胜）。
/// <see cref="Nested"/> 是 E4 的嵌套结构选择器（编译期塌缩为派生类，见 <see cref="EustyleNested"/>）。</summary>
public sealed record EustyleRule(
    string ClassName, TextSpan NameSpan, IReadOnlyList<EustyleSetter> Setters,
    IReadOnlyList<EustyleNested> Nested);

/// <summary>
/// E4 嵌套结构选择器（design/17 §4.5）：编译期塌缩为 BEM 命名的派生类，运行时仍是类名直配。
/// <c>.panel { &gt; Button { … } }</c> → 派生类 <c>panel__button</c>；<c>TextBlock.title</c> →
/// 后代 + 类约束。命名由 <c>EustyleCollapse</c> 统一生成与查重（冲突 EUI053）。
/// </summary>
/// <param name="DirectChild"><c>&gt;</c> 直接子约束；false = 后代（任意深度）。</param>
/// <param name="TypeName">类型选择器名（模板标签名，kebab 化后比较；null = 纯类约束）。</param>
/// <param name="ClassConstraint">类约束名（<c>TextBlock.title</c> 的 <c>title</c>；null = 无）。</param>
/// <param name="SelectorSpan">选择器原文 span（诊断定位用）。</param>
/// <param name="Setters">本段声明（含 :state 变体归属——派生类的伪类变体）。</param>
/// <param name="Nested">更深一层嵌套（派生名链式累加，如 <c>panel__button__text</c>）。</param>
public sealed record EustyleNested(
    bool DirectChild, string? TypeName, string? ClassConstraint, TextSpan SelectorSpan,
    IReadOnlyList<EustyleSetter> Setters, IReadOnlyList<EustyleNested> Nested);

/// <summary>一条属性声明：所属变体（None = 基础）+ StyleBuilder setter 名 + 值。
/// <see cref="RegisteredState"/> 非空 = 注册制伪类归属（E5）——此时 <see cref="Variant"/>
/// 恒为 None，注册名经运行时 <c>PseudoStatesRegistry.Register</c> 解析为掩码。</summary>
public sealed record EustyleSetter(
    EustyleVariant Variant, string Property, EustyleValue Value, TextSpan PropertySpan, TextSpan ValueSpan,
    string? RegisteredState = null);

/// <summary>解析产物：全部类规则（按源顺序）+ <c>@states</c> 声明块（E5）。</summary>
public sealed record EustyleStylesheet(IReadOnlyList<EustyleRule> Rules)
{
    /// <summary>顶层 <c>@states</c> 声明（E5）：按源顺序去重；解析撞平台位名已报错。</summary>
    public IReadOnlyList<EustyleStateDecl> States { get; init; } = Array.Empty<EustyleStateDecl>();
}

/// <summary>
/// 元素级 <c>Style="…"</c> 的解析产物（E2，design/17 §4.8②）：无类语境的声明序列，
/// 全部 <see cref="EustyleVariant.None"/>——元素属性没有「状态」语境，变体一律走类名通道。
/// </summary>
public sealed record EustyleDeclarations(IReadOnlyList<EustyleSetter> Setters);
