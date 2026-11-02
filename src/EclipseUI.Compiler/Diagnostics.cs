namespace EclipseUI.Compiler;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record UiDiagnostic(string Code, string Message, DiagnosticSeverity Severity, TextSpan Span)
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
    public const string LegacySyntax = "EUI043";

    // 语法诊断
    public const string Syntax = "EUI100";

    public static UiDiagnostic Error(string code, string message, TextSpan span)
        => new UiDiagnostic(code, message, DiagnosticSeverity.Error, span);

    public static UiDiagnostic Warning(string code, string message, TextSpan span)
        => new UiDiagnostic(code, message, DiagnosticSeverity.Warning, span);
}
