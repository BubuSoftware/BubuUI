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
    // EUI008（@switch 不支持）已随 K23 ③ 停用——@switch 转正后不再发出，编号不复用。
    public const string SwitchNotSupported = "EUI008";
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
    public const string ReservedSyntax = "EUI031";

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

    // 语法诊断
    public const string Syntax = "EUI100";

    public static UiDiagnostic Error(string code, string message, TextSpan span,
        UiSourceLocation? source = null)
        => new(code, message, DiagnosticSeverity.Error, span, source);

    public static UiDiagnostic Warning(string code, string message, TextSpan span,
        UiSourceLocation? source = null)
        => new(code, message, DiagnosticSeverity.Warning, span, source);
}
