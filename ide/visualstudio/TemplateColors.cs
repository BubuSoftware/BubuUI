using System.ComponentModel.Composition;
using System.IO;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;

namespace BubuUI.VisualStudio;

internal static class TemplateClassifications
{
#pragma warning disable CS0649
    [Export, Name("BubuUI type"), BaseDefinition("text")]
    internal static ClassificationTypeDefinition? Type;
    [Export, Name("BubuUI attribute"), BaseDefinition("text")]
    internal static ClassificationTypeDefinition? Attribute;
    [Export, Name("BubuUI directive"), BaseDefinition("text")]
    internal static ClassificationTypeDefinition? Directive;
    [Export, Name("BubuUI literal"), BaseDefinition("text")]
    internal static ClassificationTypeDefinition? Literal;
    [Export, Name("BubuUI comment"), BaseDefinition("text")]
    internal static ClassificationTypeDefinition? Comment;
    [Export, Name("BubuUI punctuation"), BaseDefinition("text")]
    internal static ClassificationTypeDefinition? Punctuation;
#pragma warning restore CS0649
}

internal abstract class TemplateColorFormat : ClassificationFormatDefinition
{
    private static readonly JObject Palette = ReadPalette();

    protected TemplateColorFormat(string role)
    {
        DisplayName = "BubuUI " + role;
        var background = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
        var dark = (background.R * 299 + background.G * 587 + background.B * 114) < 128000;
        ForegroundColor = (Color)ColorConverter.ConvertFromString(Palette[role]![dark ? "dark" : "light"]!.Value<string>()!);
    }

    private static JObject ReadPalette()
    {
        using var stream = typeof(TemplateColorFormat).Assembly.GetManifestResourceStream("BubuUI.VisualStudio.palette.json")!;
        using var reader = new StreamReader(stream);
        return JObject.Parse(reader.ReadToEnd());
    }
}

[Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = "BubuUI type"), Name("BubuUI type"), UserVisible(true)]
internal sealed class TemplateTypeFormat : TemplateColorFormat { public TemplateTypeFormat() : base("type") { } }
[Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = "BubuUI attribute"), Name("BubuUI attribute"), UserVisible(true)]
internal sealed class TemplateAttributeFormat : TemplateColorFormat { public TemplateAttributeFormat() : base("attribute") { } }
[Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = "BubuUI directive"), Name("BubuUI directive"), UserVisible(true)]
internal sealed class TemplateDirectiveFormat : TemplateColorFormat { public TemplateDirectiveFormat() : base("directive") { } }
[Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = "BubuUI literal"), Name("BubuUI literal"), UserVisible(true)]
internal sealed class TemplateLiteralFormat : TemplateColorFormat { public TemplateLiteralFormat() : base("literal") { } }
[Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = "BubuUI comment"), Name("BubuUI comment"), UserVisible(true)]
internal sealed class TemplateCommentFormat : TemplateColorFormat { public TemplateCommentFormat() : base("comment") { } }
[Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = "BubuUI punctuation"), Name("BubuUI punctuation"), UserVisible(true)]
internal sealed class TemplatePunctuationFormat : TemplateColorFormat { public TemplatePunctuationFormat() : base("punctuation") { } }
