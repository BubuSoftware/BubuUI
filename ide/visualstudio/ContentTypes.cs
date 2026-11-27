using System.ComponentModel.Composition;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Utilities;

namespace BubuUI.VisualStudio;

internal static class ContentTypes
{
    public const string Template = "bubuui";
    public const string Stylesheet = "bss";

#pragma warning disable CS0649 // MEF exports are populated by the editor.
    [Export, Name(Template), BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
    internal static ContentTypeDefinition? TemplateContentType;

    [Export, FileExtension(".bui"), ContentType(Template)]
    internal static FileExtensionToContentTypeDefinition? TemplateExtension;

    [Export, Name(Stylesheet), BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
    internal static ContentTypeDefinition? StylesheetContentType;

    [Export, FileExtension(".bss"), ContentType(Stylesheet)]
    internal static FileExtensionToContentTypeDefinition? StylesheetExtension;
#pragma warning restore CS0649
}
