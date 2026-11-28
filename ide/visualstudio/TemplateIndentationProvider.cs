using System;
using System.ComponentModel.Composition;
using BubuUI.Compiler;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Utilities;

namespace BubuUI.VisualStudio;

[Export(typeof(ISmartIndentProvider))]
[ContentType(ContentTypes.Template)]
[ContentType(ContentTypes.Stylesheet)]
internal sealed class TemplateIndentationProvider : ISmartIndentProvider
{
    public ISmartIndent CreateSmartIndent(ITextView textView) => new SmartIndent(textView);

    private sealed class SmartIndent : ISmartIndent
    {
        private readonly ITextView _view;
        public SmartIndent(ITextView view) => _view = view;
        public int? GetDesiredIndentation(ITextSnapshotLine line)
        {
            var lines = TemplateIndentation.Analyze(line.Snapshot.GetText(),
                line.Snapshot.ContentType.IsOfType(ContentTypes.Stylesheet));
            var info = lines[line.LineNumber];
            if (info.Preserve) return null;
            return info.Depth * _view.Options.GetOptionValue(DefaultOptions.IndentSizeOptionId);
        }
        public void Dispose() { }
    }
}

/// <summary>Keep paste and its indentation in a single undo transaction.</summary>
[Export(typeof(ICommandHandler))]
[Name("BubuUI paste indentation")]
[ContentType(ContentTypes.Template)]
[ContentType(ContentTypes.Stylesheet)]
[Order(Before = "default")]
internal sealed class TemplatePasteHandler : IChainedCommandHandler<PasteCommandArgs>
{
    [Import] internal ITextUndoHistoryRegistry UndoHistory = null!;
    public string DisplayName => "BubuUI paste indentation";
    public CommandState GetCommandState(PasteCommandArgs args, Func<CommandState> nextHandler) => nextHandler();

    public void ExecuteCommand(PasteCommandArgs args, Action nextHandler, CommandExecutionContext executionContext)
    {
        var before = args.SubjectBuffer.CurrentSnapshot.GetText();
        var history = UndoHistory.RegisterHistory(args.SubjectBuffer);
        using (var transaction = history.CreateTransaction(DisplayName))
        {
            nextHandler();
            var snapshot = args.SubjectBuffer.CurrentSnapshot;
            var after = snapshot.GetText();
            int start = 0;
            while (start < before.Length && start < after.Length && before[start] == after[start]) start++;
            int oldEnd = before.Length, end = after.Length;
            while (oldEnd > start && end > start && before[oldEnd - 1] == after[end - 1]) { oldEnd--; end--; }
            if (end > start && after.Substring(start, end - start).IndexOf('\n') >= 0)
            {
                var options = args.TextView.Options;
                int indentSize = options.GetOptionValue(DefaultOptions.IndentSizeOptionId);
                int tabSize = options.GetOptionValue(DefaultOptions.TabSizeOptionId);
                bool spaces = options.GetOptionValue(DefaultOptions.ConvertTabsToSpacesOptionId);
                using (var edit = args.SubjectBuffer.CreateEdit())
                {
                    foreach (var line in TemplateIndentation.Analyze(after, snapshot.ContentType.IsOfType(ContentTypes.Stylesheet)))
                    {
                        if (line.Preserve || line.End < start || line.Start >= end || line.ContentStart == line.End) continue;
                        int columns = line.Depth * indentSize;
                        var desired = spaces ? new string(' ', columns)
                            : new string('\t', columns / Math.Max(1, tabSize)) + new string(' ', columns % Math.Max(1, tabSize));
                        if (after.Substring(line.Start, line.ContentStart - line.Start) != desired)
                            edit.Replace(line.Start, line.ContentStart - line.Start, desired);
                    }
                    edit.Apply();
                }
            }
            transaction.Complete();
        }
    }
}
