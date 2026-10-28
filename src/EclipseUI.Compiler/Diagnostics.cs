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
    public const string SignalInItemTemplate = "EUI014";
    public const string CodeBehindNested = "EUI015";

    // 语法诊断
    public const string Syntax = "EUI100";

    public static UiDiagnostic Error(string code, string message, TextSpan span)
        => new UiDiagnostic(code, message, DiagnosticSeverity.Error, span);

    public static UiDiagnostic Warning(string code, string message, TextSpan span)
        => new UiDiagnostic(code, message, DiagnosticSeverity.Warning, span);
}
