using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;

namespace BubuUI.VisualStudio;

/// <summary>Read the actual document view options, including per-file .editorconfig overrides.</summary>
[Export(typeof(IWpfTextViewCreationListener))]
[ContentType(ContentTypes.Template)]
[ContentType(ContentTypes.Stylesheet)]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class CompletionEditorSettings : IWpfTextViewCreationListener
{
    private static readonly List<(IWpfTextView View, ITextDocument Document)> Views = new();
    [Import] internal ITextDocumentFactoryService Documents = null!;

    public void TextViewCreated(IWpfTextView view)
    {
        if (!Documents.TryGetTextDocument(view.TextBuffer, out var document)) return;
        Views.Add((view, document));
        view.Closed += (_, _) => Views.RemoveAll(entry => ReferenceEquals(entry.View, view));
    }

    internal static async Task<JObject?> ReadAsync(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile) return null;
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        IWpfTextView? match = null;
        foreach (var entry in Views)
        {
            if (entry.View.IsClosed || !string.Equals(entry.Document.FilePath, parsed.LocalPath, StringComparison.OrdinalIgnoreCase)) continue;
            match = entry.View;
            if (match.HasAggregateFocus) break;
        }
        if (match == null) return null;
        return new JObject
        {
            ["tabSize"] = match.Options.GetOptionValue(DefaultOptions.TabSizeOptionId),
            ["indentSize"] = match.Options.GetOptionValue(DefaultOptions.IndentSizeOptionId),
            ["insertSpaces"] = match.Options.GetOptionValue(DefaultOptions.ConvertTabsToSpacesOptionId),
        };
    }
}
