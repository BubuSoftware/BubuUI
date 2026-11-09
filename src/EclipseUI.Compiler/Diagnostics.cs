namespace EclipseUI.Compiler;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// 诊断位置可指向 code-behind 的 C# 源声明（spec/02 §10 的 EUI045），而非 .ui 文件。
/// 纯数据记录：增量生成器的缓存边界不能持有 Roslyn Location/SyntaxTree。
/// 行列是 0 基（与 Roslyn LinePosition 一致）。
/// </summary>
public sealed record UiSourceLocation(
    string Path,
    int Start,
    int Length,
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter);

public sealed record UiDiagnostic(
    string Code,
    string Message,
    DiagnosticSeverity Severity,
    TextSpan Span,
    UiSourceLocation? Source = null)
{
    public bool IsError => Severity == DiagnosticSeverity.Error;
}

public static class UiDiagnostics
{
    // 语义诊断
    public const string UnknownTag = "EUI001";
    public const string UnknownProperty = "EUI002";
    public const string LiteralFormat = "EUI003";
    public const string EventNeedsExpression = "EUI004";
    public const string CodeBehindMissing = "EUI005";
    public const string TextNotAllowed = "EUI006";
    public const string ForEachItemTypeUnknown = "EUI007";
    // EUI008（@switch 不支持）已随 K23 ③ 停用：@switch 转正后不再发出，编号不复用，故无对应常量。
    public const string UnknownDirective = "EUI009";
    public const string KeyMisuse = "EUI010";
    public const string CodeBehindNotPartial = "EUI011";
    public const string RootElementExpected = "EUI012";
    public const string InvalidClassName = "EUI013";
    public const string CodeBehindNested = "EUI015";

    // 控件/组件契约诊断（design/14 §14.9.1）。C1 的目录构建先产出 EUI017/EUI018/EUI019/EUI021/
    // EUI026/EUI028/EUI030 的元数据问题；使用点落地在 C3/C5，编号此后再不重复分配。
    public const string TagConflict = "EUI016";
    public const string TagNotExposed = "EUI017";
    public const string NoPublicParameterlessConstructor = "EUI018";
    public const string PropertyNotBindable = "EUI019";
    public const string EventFieldMissing = "EUI020";
    public const string ContentTargetInvalid = "EUI021";
    public const string SlotInvalid = "EUI022";
    public const string NoLiteralConverter = "EUI023";
    public const string EventHandlerSignature = "EUI024";
    public const string DuplicateAttribute = "EUI025";
    public const string MetadataDeclarationInvalid = "EUI026";
    public const string RequiredParameterMissing = "EUI027";
    public const string ParameterDeclarationInvalid = "EUI028";
    public const string RefTargetInvalid = "EUI029";
    public const string GenericTagNotSupported = "EUI030";
    // E2 转正（design/17 §5）：EUI031 从「Class/Style 保留未实现」改为「Class/Style 属性不合法」
    // ——未知类名（样式目录查无）、Class 表达式形态不符、Style= 表达式（只允许静态值）、
    // @bind-Class/@bind-Style。同一码仍服务于 <Class>/<Style> **标签**形态保留（UiCatalogFactory）。
    public const string StyleAttributeInvalid = "EUI031";

    public const string SameTargetDuplicate = "EUI033";
    public const string BindSourceInvalid = "EUI037";
    public const string BindTargetInvalid = "EUI040";
    // EUI043（旧语法 P:bind/P:value 弃用告警）已随 K27 停用——S6 移除旧语法后不再发出，
    // 测试与快照必须断言零出现（spec/01 §6.2 停用码纪律）。

    public const string CodeBehindConflict = "EUI045";

    // S6（K27）：旧绑定语法 P:bind/P:value 已移除（替代已停用的 EUI043，级别升为 Error）。
    public const string LegacySyntaxRemoved = "EUI046";
    // S6（K29 ②）：@switch 的 case 标签必须是编译期常量。
    public const string CaseLabelNotConstant = "EUI047";
    // K31：@using 别名（@using Btn = My.Controls.Button）不合法——目标不在控件目录、
    // 同一模板重复定义、或与 @using 命名空间同名。
    public const string UsingAliasInvalid = "EUI048";
    // G4（表达式语义分析最小闭环）：模板表达式（属性 {expr}、@if 条件、插值文本段）放进
    // 探针编译后句内仍存在 CS 错误——拼错成员/类型不匹配在使用点直接给出 Warning 预检。
    // 刻意不设 Error：Error 会短路生成（EUI024 口径），而类型错误的 #line 映射契约
    // （spec/02 §2.4 规则 9/10）要求生成代码带着类型错误走出去由 C# 报权威错误；
    // 规则 8 的「不重复报」在此退让为「预检警告 + C# 权威错误」双可见，属既定权衡。
    public const string ExpressionSemantic = "EUI049";

    // E1（样式表编译器，design/17 §5）：.eustyle / @style 块诊断。码位从 EUI050 起——
    // EUI048/EUI049 已被 @using 别名（K31）与表达式语义（G4）占用（design/17 初版的
    // EUI048–052 分配按实际占用顺延）；EUI053 落地为 E4（派生名冲突）/EUI054 预留 E5（双通道冲突）。
    public const string StyleSyntax = "EUI050";
    public const string StyleUnknownProperty = "EUI051";
    public const string StyleValueType = "EUI052";
    // E4（design/17 §4.5/§5）：嵌套塌缩的派生类自动命名冲突——同名派生类声明两次、
    // 或与样式表里的显式类名撞车。第一版不提供显式改名，拆两个中间类规避。
    public const string StyleDerivedNameConflict = "EUI053";

    // 语法诊断
    public const string Syntax = "EUI100";

    public static UiDiagnostic Error(string code, string message, TextSpan span,
        UiSourceLocation? source = null)
        => new(code, message, DiagnosticSeverity.Error, span, source);

    public static UiDiagnostic Warning(string code, string message, TextSpan span,
        UiSourceLocation? source = null)
        => new(code, message, DiagnosticSeverity.Warning, span, source);
}
